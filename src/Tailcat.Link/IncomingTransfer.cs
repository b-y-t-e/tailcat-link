// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using Tailcat.Link.Protocol;

namespace Tailcat.Link;

/// <summary>
/// A transfer arriving from the peer: what it says about itself, and the
/// content as a stream to read at whatever pace suits.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Content"/> is the whole of the API. It is an ordinary
/// forward-only stream — copy it to a file, hash it, parse it — and it hides
/// everything underneath: the blocks it arrives in, the sessions it crosses,
/// and the reconnections it waits through. A read blocks while the link is
/// down and returns when it comes back, so a handler never has to know that
/// anything happened.
/// </para>
/// <para>
/// Reading slowly is safe and is the intended way to hold a 20 GB transfer to
/// what the disk can take: only a few megabytes are ever buffered here, and
/// the sender is held back by the transport once they are full. Returning
/// from the handler before the end is safe too — the rest is discarded rather
/// than left to block the sender.
/// </para>
/// </remarks>
public sealed class IncomingTransfer
{
    /// <summary>
    /// How much of the content is buffered here while the application reads.
    /// </summary>
    /// <remarks>
    /// The one number that decides how much memory a transfer costs the
    /// receiving machine, whatever its size. Past it the writer waits, which
    /// the transport turns into back-pressure on the sender.
    /// </remarks>
    private const int BufferBytes = 4 * 1024 * 1024;

    private const int ResumeBufferBytes = BufferBytes / 2;

    private readonly Pipe _pipe = new(new PipeOptions(
        pauseWriterThreshold: BufferBytes,
        resumeWriterThreshold: ResumeBufferBytes,
        useSynchronizationContext: false));

    private readonly Lock _mu = new();
    private readonly Action _forget;
    private readonly TimeSpan _retention;
    private readonly ITimer _expiry;
    private readonly Stream _content;

    private long _received;
    private bool _delivering;
    private bool _bodyEnded;
    private bool _discarding;
    private bool _expired;
    private bool _finished;
    private LinkAnswer _answer;
    private Task<LinkAnswer>? _handler;

    internal IncomingTransfer(Guid id, TransferOffer offer, TimeProvider time, TimeSpan retention, Action forget)
    {
        Id = id;
        Name = offer.Name;
        ContentType = offer.ContentType;
        Length = offer.Length;
        Metadata = offer.Metadata;
        _retention = retention;
        _forget = forget;
        _content = _pipe.Reader.AsStream();
        _expiry = time.CreateTimer(_ => Expire(), null, retention, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// What identifies this transfer, on both machines and across every
    /// session it takes to deliver.
    /// </summary>
    public Guid Id { get; }

    /// <summary>What the sender called it. Not to be trusted as a path.</summary>
    /// <seealso cref="SuggestedFileName"/>
    public string Name { get; }

    /// <summary>The media type the sender declared, or an empty string.</summary>
    public string ContentType { get; }

    /// <summary>How many bytes are coming, when the sender knew.</summary>
    public long? Length { get; }

    /// <summary>Whatever the sender attached to the offer.</summary>
    public ReadOnlyMemory<byte> Metadata { get; }

    /// <summary>The content, as a stream that spans every reconnection.</summary>
    /// <remarks>
    /// Forward-only and not seekable: the point of a transfer is that neither
    /// machine holds it. Reading it to the end is what completes the
    /// transfer for the sender.
    /// </remarks>
    public Stream Content => _content;

    /// <summary>How much of the content has arrived so far.</summary>
    public long BytesReceived => Interlocked.Read(ref _received);

    /// <summary>
    /// <see cref="Name"/> reduced to something safe to append to a directory.
    /// </summary>
    /// <remarks>
    /// The name comes from the other machine, which makes it exactly as
    /// trustworthy as anything else off a network: <c>../../etc/passwd</c> is
    /// a name a peer may send. This keeps the last path segment, drops what
    /// the file system would refuse, and falls back to <c>transfer</c> when
    /// nothing usable is left.
    /// </remarks>
    public string SuggestedFileName
    {
        get
        {
            // Both separators, whatever this machine's are: the name was
            // written by the other one, which may not agree.
            string bare = Name.Replace('\\', '/');
            bare = bare[(bare.LastIndexOf('/') + 1)..];
            Span<char> cleaned = stackalloc char[bare.Length];
            int kept = 0;
            foreach (char c in bare)
            {
                if (!char.IsControl(c) && Array.IndexOf(Path.GetInvalidFileNameChars(), c) < 0)
                {
                    cleaned[kept++] = c;
                }
            }
            string name = new string(cleaned[..kept]).Trim(' ', '.');
            return name.Length == 0 ? "transfer" : name;
        }
    }

    /// <summary>Writes the content to a file, replacing whatever is there.</summary>
    /// <param name="path">Where to write it. Chosen by this machine, not the peer.</param>
    /// <param name="progress">Told after each block, when given.</param>
    /// <param name="cancellationToken">Gives up on the transfer.</param>
    public async Task SaveToAsync(
        string path,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        FileStream file = new(path, FileMode.Create, FileAccess.Write, FileShare.None, TransferFrame.BlockBytes, useAsync: true);
        await using (file.ConfigureAwait(false))
        {
            await CopyToAsync(file, progress, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Writes the content wherever it is wanted, reporting progress.</summary>
    /// <param name="destination">Where the bytes go.</param>
    /// <param name="progress">Told after each block, when given.</param>
    /// <param name="cancellationToken">Gives up on the transfer.</param>
    public async Task CopyToAsync(
        Stream destination,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);

        byte[] buffer = ArrayPool<byte>.Shared.Rent(TransferFrame.BlockBytes);
        try
        {
            long copied = 0;
            while (true)
            {
                int read = await Content.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    return;
                }
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                copied += read;
                progress?.Report(new TransferProgress(copied, Length));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Starts the application's handler, once per transfer.</summary>
    /// <param name="handler">What the application does with the content.</param>
    /// <param name="linkClosed">
    /// The handler's lifetime, which is the link's rather than the session's:
    /// a session dropping mid-transfer is what resuming is for, and
    /// cancelling the handler for it would throw away everything read so far.
    /// </param>
    internal void Start(LinkTransferHandler handler, CancellationToken linkClosed) =>
        _handler ??= Task.Run(() => RunAsync(handler, linkClosed), CancellationToken.None);

    /// <summary>
    /// Takes the content off one session's stream, from wherever the last
    /// session got to, and answers when it is all there.
    /// </summary>
    internal async Task DeliverAsync(Stream stream, Guid exchange, CancellationToken cancellationToken)
    {
        BeginDelivery();
        try
        {
            await LinkFrame.WriteAsync(
                stream,
                (byte)LinkFrameStatus.Ok,
                exchange,
                TransferFrame.EncodeOffset(BytesReceived),
                idle: null,
                cancellationToken).ConfigureAwait(false);

            await ReadBodyAsync(stream, cancellationToken).ConfigureAwait(false);

            // Only now: the sender's call returns when the receiving handler
            // has finished, so that a transfer reported as sent is a transfer
            // the other machine has actually dealt with.
            LinkAnswer answer = await AnswerAsync().ConfigureAwait(false);
            await LinkFrame.WriteAsync(
                stream, (byte)answer.Status, exchange, answer.Payload, idle: null, cancellationToken)
                .ConfigureAwait(false);

            Finish(answer);
        }
        finally
        {
            // Whichever way this ended — delivered, or a session that died
            // mid-file — what has arrived stays, and the timer decides how
            // long it is held for the sender to come back to.
            EndDelivery();
        }
    }

    /// <summary>
    /// Forgets the transfer: either one nobody came back for, or one whose
    /// answer has been remembered for long enough.
    /// </summary>
    internal void Expire()
    {
        bool delivered;
        lock (_mu)
        {
            if (_expired)
            {
                return;
            }
            _expired = true;
            delivered = _finished;
        }
        _forget();
        if (!delivered)
        {
            _pipe.Writer.Complete(
                new LinkException($"the transfer stopped and was not resumed within {_retention}"));
        }
        _expiry.Dispose();
    }

    private async Task ReadBodyAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(TransferFrame.BlockBytes);
        try
        {
            while (true)
            {
                int length = await TransferFrame.ReadBlockHeaderAsync(stream, cancellationToken).ConfigureAwait(false);
                if (length == 0)
                {
                    break;
                }
                if (_bodyEnded)
                {
                    throw new LinkException("the peer sent more of a transfer it had already ended");
                }

                await stream.ReadExactlyAsync(buffer.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
                await AcceptAsync(buffer.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
            }

            if (!_bodyEnded)
            {
                _bodyEnded = true;
                // A transfer that ends short of the length it announced is
                // broken, and the handler hears about it as a failed read
                // rather than as a clean end — otherwise a half a file is
                // saved as though it were the whole one. It is failed rather
                // than retried because the content on the other machine is
                // what ran out, and asking again would produce the same.
                _pipe.Writer.Complete(
                    Length is long announced && BytesReceived != announced
                        ? new LinkException(
                            $"the transfer ended after {BytesReceived} of the {announced} bytes it announced")
                        : null);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Hands one block to the application, or drops it on the floor.</summary>
    /// <remarks>
    /// Copied into the pipe first and counted second, with the wait for room
    /// last. That order is what makes the count honest: a block is counted
    /// once it is somewhere the application can be given it, and the session
    /// can die during the wait — which it will, on a receiver whose disk is
    /// slower than the link — without the resumed attempt sending it twice.
    /// </remarks>
    private async Task AcceptAsync(ReadOnlyMemory<byte> block, CancellationToken cancellationToken)
    {
        if (_discarding)
        {
            // Counted even so: it has arrived, and a resumed attempt must not
            // send it again to a handler that is not listening anyway.
            Interlocked.Add(ref _received, block.Length);
            return;
        }

        try
        {
            _pipe.Writer.Write(block.Span);
            Interlocked.Add(ref _received, block.Length);

            // The handler has finished, or thrown, and is not reading any
            // more. The rest of the transfer is still taken off the wire —
            // dropping it would leave the sender writing into a stream that
            // will never be read, which looks to it exactly like a stall.
            FlushResult flush = await _pipe.Writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            _discarding = flush.IsCompleted;
        }
        catch (InvalidOperationException)
        {
            _discarding = true;
        }
    }

    private async Task<LinkAnswer> RunAsync(LinkTransferHandler handler, CancellationToken linkClosed)
    {
        try
        {
            await handler(this, linkClosed).ConfigureAwait(false);
            await _pipe.Reader.CompleteAsync().ConfigureAwait(false);
            return new LinkAnswer(LinkFrameStatus.Ok, TransferFrame.EncodeOffset(BytesReceived));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The application refused it. That is an answer, and the sender
            // hears it rather than retrying something that would fail again.
            await _pipe.Reader.CompleteAsync(ex).ConfigureAwait(false);
            return new LinkAnswer(LinkFrameStatus.Failed, Encoding.UTF8.GetBytes(ex.Message));
        }
    }

    /// <summary>What the handler made of the transfer, once it is done.</summary>
    private Task<LinkAnswer> AnswerAsync()
    {
        lock (_mu)
        {
            if (_finished)
            {
                return Task.FromResult(_answer);
            }
        }
        return _handler ?? throw new LinkException("the transfer was never started");
    }

    /// <summary>
    /// Remembers the answer for a little longer, for a sender whose session
    /// died between being handed the answer and reading it.
    /// </summary>
    /// <remarks>
    /// Without this its retry would arrive as a transfer nobody has heard of,
    /// and the handler would run a second time — a file saved twice.
    /// </remarks>
    private void Finish(LinkAnswer answer)
    {
        lock (_mu)
        {
            _answer = answer;
            _finished = true;
        }
    }

    /// <summary>
    /// Claims the transfer for one session, and stops the clock that would
    /// otherwise give up on it.
    /// </summary>
    /// <remarks>
    /// One at a time: a transfer resumed on a fresh session can reach here
    /// while the attempt on the session that died is still unwinding, and two
    /// deliveries would interleave blocks into one pipe. The loser is refused
    /// rather than queued — its sender retries a moment later, by which time
    /// the dead one has let go.
    /// </remarks>
    private void BeginDelivery()
    {
        lock (_mu)
        {
            if (_expired)
            {
                throw new LinkException($"the transfer was not resumed within {_retention}");
            }
            if (_delivering)
            {
                throw new LinkException("the transfer is already being delivered on another session");
            }
            _delivering = true;
            _expiry.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }

    private void EndDelivery()
    {
        lock (_mu)
        {
            _delivering = false;
            if (!_expired)
            {
                _expiry.Change(_retention, Timeout.InfiniteTimeSpan);
            }
        }
    }
}

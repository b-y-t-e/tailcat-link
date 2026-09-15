// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Buffers;
using System.IO.Pipelines;
using System.Runtime.InteropServices;
using System.Text;
using Tailcat.Link.Protocol;

namespace Tailcat.Link;

/// <summary>
/// Content arriving from the peer — a transfer, a request, or the answer to one
/// — with what it says about itself, and the bytes as a stream to read at
/// whatever pace suits.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Content"/> is the whole of the API. It is an ordinary
/// forward-only stream — copy it to a file, hash it, parse it — and it hides
/// everything underneath: the blocks it arrives in, the sessions it crosses,
/// and the reconnections it waits through. A read blocks while the link is
/// down and returns when it comes back, so a reader never has to know that
/// anything happened.
/// </para>
/// <para>
/// Reading slowly is safe and is the intended way to hold a 20 GB transfer to
/// what the disk can take: only a few megabytes are ever buffered here, and
/// the sender is held back by the transport once they are full. Stopping
/// before the end is safe too — the rest is discarded rather than left to
/// block the sender.
/// </para>
/// <para>
/// There is no size limit. <see cref="ReadAllBytesAsync"/> and
/// <see cref="ReadAllTextAsync"/> put the whole content in memory and fail the
/// way the runtime fails when it does not fit; the stream never does.
/// </para>
/// </remarks>
public sealed class IncomingTransfer : IAsyncDisposable
{
    /// <summary>
    /// How much of the content is buffered here while the application reads.
    /// </summary>
    /// <remarks>
    /// The one number that decides how much memory content costs the receiving
    /// machine, whatever its size. Past it the writer waits, which the
    /// transport turns into back-pressure on the sender.
    /// </remarks>
    private const int BufferBytes = 4 * 1024 * 1024;

    private const int ResumeBufferBytes = BufferBytes / 2;

    private readonly Pipe? _pipe;
    private readonly ReadOnlyMemory<byte> _preloaded;
    private readonly Stream _content;
    private readonly TimeProvider _time;
    private readonly Func<ValueTask>? _onDisposed;
    private readonly Lock _mu = new();

    private long _received;
    private long _lastArrived;
    private bool _bodyEnded;
    private bool _discarding;
    private bool _readerDone;
    private bool _disposed;

    internal IncomingTransfer(
        Guid id,
        string name,
        string contentType,
        long? length,
        ReadOnlyMemory<byte> metadata,
        TimeProvider time,
        Func<ValueTask>? onDisposed = null)
    {
        Id = id;
        Name = name;
        ContentType = contentType;
        Length = length;
        Metadata = metadata;
        _time = time;
        _onDisposed = onDisposed;
        _lastArrived = time.GetTimestamp();
        _pipe = new Pipe(new PipeOptions(
            pauseWriterThreshold: BufferBytes,
            resumeWriterThreshold: ResumeBufferBytes,
            useSynchronizationContext: false));
        _content = _pipe.Reader.AsStream();
    }

    private IncomingTransfer(Guid id, ReadOnlyMemory<byte> whole, string contentType, TimeProvider time)
    {
        Id = id;
        Name = string.Empty;
        ContentType = contentType;
        Length = whole.Length;
        _time = time;
        _preloaded = whole;
        _received = whole.Length;
        _lastArrived = time.GetTimestamp();
        _bodyEnded = true;
        _content = MemoryMarshal.TryGetArray(whole, out ArraySegment<byte> array) && array.Array is not null
            ? new MemoryStream(array.Array, array.Offset, array.Count, writable: false)
            : new MemoryStream(whole.ToArray(), writable: false);
    }

    /// <summary>
    /// What identifies this content, on both machines and across every
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

    /// <summary>Whatever the sender attached.</summary>
    public ReadOnlyMemory<byte> Metadata { get; }

    /// <summary>The content, as a stream that spans every reconnection.</summary>
    /// <remarks>
    /// Forward-only and not seekable: the point is that neither machine holds
    /// all of it. Reading it to the end is what completes it for the sender.
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
            // written by the other one, which may not agree. Built on the
            // heap: a name is as long as the sender likes, and the stack is
            // not somewhere to put a peer's say-so.
            string bare = Name.Replace('\\', '/');
            bare = bare[(bare.LastIndexOf('/') + 1)..];
            StringBuilder cleaned = new(bare.Length);
            foreach (char c in bare)
            {
                if (!char.IsControl(c) && Array.IndexOf(Path.GetInvalidFileNameChars(), c) < 0)
                {
                    cleaned.Append(c);
                }
            }
            string name = cleaned.ToString().Trim(' ', '.');
            return name.Length == 0 ? "transfer" : name;
        }
    }

    /// <summary>When a block last arrived, for telling a slow link from a dead one.</summary>
    internal long LastArrived => Interlocked.Read(ref _lastArrived);

    /// <summary>Whether the block that ends the content has arrived.</summary>
    internal bool BodyEnded
    {
        get
        {
            lock (_mu)
            {
                return _bodyEnded;
            }
        }
    }

    /// <summary>Writes the content to a file, replacing whatever is there.</summary>
    /// <param name="path">Where to write it. Chosen by this machine, not the peer.</param>
    /// <param name="progress">Told after each block, when given.</param>
    /// <param name="cancellationToken">Gives up on the content.</param>
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
    /// <param name="cancellationToken">Gives up on the content.</param>
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

    /// <summary>Reads the whole content into memory.</summary>
    /// <remarks>
    /// For content that is known to be small enough, which is most requests.
    /// There is no limit here: content larger than one array can hold, or than
    /// this machine has memory for, fails the way the runtime fails.
    /// </remarks>
    /// <param name="cancellationToken">Gives up on the content.</param>
    public async Task<byte[]> ReadAllBytesAsync(CancellationToken cancellationToken = default)
    {
        if (_pipe is null)
        {
            // Already whole, as it arrived from a machine that sends messages
            // in one frame. Handed over rather than copied.
            return MemoryMarshal.TryGetArray(_preloaded, out ArraySegment<byte> array)
                && array.Array is not null && array.Offset == 0 && array.Count == array.Array.Length
                ? array.Array
                : _preloaded.ToArray();
        }

        if (Length is long known)
        {
            if (known > Array.MaxLength)
            {
                throw new LinkException(
                    $"{known} bytes do not fit in one array; read {nameof(Content)} as a stream instead");
            }
            byte[] whole = new byte[known];
            await Content.ReadExactlyAsync(whole, cancellationToken).ConfigureAwait(false);
            return whole;
        }

        MemoryStream collected = new();
        await Content.CopyToAsync(collected, cancellationToken).ConfigureAwait(false);
        return collected.ToArray();
    }

    /// <summary>Reads the whole content into memory as UTF-8 text.</summary>
    /// <param name="cancellationToken">Gives up on the content.</param>
    public async Task<string> ReadAllTextAsync(CancellationToken cancellationToken = default)
    {
        using StreamReader reader = new(Content, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Stops reading, and lets go of whatever is still arriving.</summary>
    /// <remarks>
    /// On the answer to a request this abandons the rest of the answer: the
    /// link stops asking for it. On content a handler was given it only
    /// discards the rest, which is what returning from the handler does anyway.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        await CompleteReaderAsync(error: null).ConfigureAwait(false);
        if (_onDisposed is not null)
        {
            await _onDisposed().ConfigureAwait(false);
        }
    }

    /// <summary>A content that has already arrived whole.</summary>
    internal static IncomingTransfer FromBytes(Guid id, ReadOnlyMemory<byte> whole, string contentType, TimeProvider time) =>
        new(id, whole, contentType, time);

    /// <summary>
    /// Takes content off one stream, the first block starting at
    /// <paramref name="startsAt"/> in the content, until the block that ends it.
    /// </summary>
    /// <param name="stream">Where the blocks arrive.</param>
    /// <param name="startsAt">
    /// Where in the content the first block begins: what this end said it
    /// had, or zero for content sent before anyone was asked. Whatever overlaps
    /// what is already here is dropped, so neither a resumed attempt nor a
    /// repeated one can put the same byte in twice.
    /// </param>
    /// <param name="silence">
    /// How long a read from the network may wait, or null for no bound. Only
    /// the network is timed: waiting for room while the application reads
    /// slowly is not the peer being silent.
    /// </param>
    /// <param name="cancellationToken">Gives up on this attempt.</param>
    /// <exception cref="LinkException">
    /// If the peer sent a block beyond what is here, or more after the end.
    /// </exception>
    internal async Task ReadBodyAsync(Stream stream, long startsAt, TimeSpan? silence, CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(TransferFrame.BlockBytes);
        try
        {
            long position = startsAt;
            while (true)
            {
                int length = await FromNetworkAsync(
                    ct => TransferFrame.ReadBlockHeaderAsync(stream, ct), silence, cancellationToken).ConfigureAwait(false);
                if (length == 0)
                {
                    break;
                }
                await FromNetworkAsync(
                    async ct =>
                    {
                        await stream.ReadExactlyAsync(buffer.AsMemory(0, length), ct).ConfigureAwait(false);
                        return length;
                    },
                    silence,
                    cancellationToken).ConfigureAwait(false);

                await AcceptAsync(buffer.AsMemory(0, length), position, cancellationToken).ConfigureAwait(false);
                position += length;
            }
            EndBody();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Ends the content with an error, if it had not already ended.</summary>
    /// <remarks>
    /// The reader hears the error on its next read. Only called by whoever
    /// owns the writing, once nothing else is writing.
    /// </remarks>
    internal void Fail(Exception error)
    {
        if (_pipe is null)
        {
            return;
        }
        lock (_mu)
        {
            if (_bodyEnded)
            {
                return;
            }
            _bodyEnded = true;
        }
        _pipe.Writer.Complete(error);
    }

    /// <summary>
    /// Says nobody is reading any more, so that whatever still arrives is
    /// counted and dropped rather than left to fill the buffer.
    /// </summary>
    internal async ValueTask CompleteReaderAsync(Exception? error)
    {
        if (_pipe is null)
        {
            return;
        }
        lock (_mu)
        {
            if (_readerDone)
            {
                return;
            }
            _readerDone = true;
        }
        await _pipe.Reader.CompleteAsync(error).ConfigureAwait(false);
    }

    private async Task<T> FromNetworkAsync<T>(
        Func<CancellationToken, Task<T>> read,
        TimeSpan? silence,
        CancellationToken cancellationToken)
    {
        if (silence is not TimeSpan limit)
        {
            return await read(cancellationToken).ConfigureAwait(false);
        }
        using CancellationTokenSource quiet = new(limit, _time);
        using CancellationTokenSource either =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, quiet.Token);
        return await read(either.Token).ConfigureAwait(false);
    }

    private void EndBody()
    {
        if (_pipe is null)
        {
            return;
        }
        lock (_mu)
        {
            if (_bodyEnded)
            {
                return;
            }
            _bodyEnded = true;
        }
        // Content that ends short of the length it announced is broken, and
        // the reader hears about it as a failed read rather than as a clean
        // end — otherwise half a file is saved as though it were the whole
        // one. It is failed rather than retried because the content on the
        // other machine is what ran out, and asking again would produce the
        // same.
        long received = BytesReceived;
        _pipe.Writer.Complete(
            Length is long announced && received != announced
                ? new LinkException($"the content ended after {received} of the {announced} bytes it announced")
                : null);
    }

    /// <summary>Hands one block to the reader, or drops it on the floor.</summary>
    /// <remarks>
    /// Copied into the pipe first and counted second, with the wait for room
    /// last. That order is what makes the count honest: a block is counted
    /// once it is somewhere the reader can be given it, and the session can
    /// die during the wait — which it will, on a receiver whose disk is slower
    /// than the link — without the resumed attempt sending it twice.
    /// </remarks>
    private async Task AcceptAsync(ReadOnlyMemory<byte> block, long position, CancellationToken cancellationToken)
    {
        long have = BytesReceived;
        if (position > have)
        {
            throw new LinkException($"the peer sent content from byte {position} with only {have} here");
        }
        int already = (int)Math.Min(block.Length, have - position);
        block = block[already..];
        if (block.IsEmpty)
        {
            return;
        }
        if (BodyEnded)
        {
            throw new LinkException("the peer sent more of content it had already ended");
        }

        Interlocked.Exchange(ref _lastArrived, _time.GetTimestamp());
        if (_discarding)
        {
            // Counted even so: it has arrived, and a resumed attempt must not
            // send it again to a reader that is not listening anyway.
            Interlocked.Add(ref _received, block.Length);
            return;
        }

        try
        {
            _pipe!.Writer.Write(block.Span);
            Interlocked.Add(ref _received, block.Length);

            // The reader has stopped. The rest is still taken off the wire —
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
}

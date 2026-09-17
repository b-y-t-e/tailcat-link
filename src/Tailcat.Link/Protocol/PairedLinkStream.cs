// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Buffers;
using System.Buffers.Binary;
using System.Net.Quic;
using System.Threading.Channels;

namespace Tailcat.Link.Protocol;

/// <summary>
/// A <see cref="LinkStream"/> on one transport stream of one session.
/// </summary>
/// <remarks>
/// <para>
/// On the wire it is a channel's framing both ways (<see cref="ChannelFrame"/>):
/// length-prefixed pieces, a zero length that ends one direction, and one more
/// length no piece can have, <see cref="AbortMarker"/>, that abandons the lot.
/// <c>docs/streams.md</c> is the specification.
/// </para>
/// <para>
/// Two rules about the transport underneath make the endings tell the truth.
/// An end that has ended its writes keeps the transport open for as long as it
/// still reads, so the transport ending after the end marker means "closed".
/// And an end that aborts keeps it open until the other end has read the abort
/// marker and let go: closed at once, the close races the marker, and on QUIC a
/// writer at the other end hears the close first — as a stream closed, not
/// abandoned.
/// </para>
/// <para>
/// A task reads ahead, one piece at a time, so that the other end letting go
/// is noticed while this end is only writing: a writer into a machine that has
/// stopped reading would otherwise wait on flow control until the session
/// died. It holds a piece or two at most, so reading still paces the sender.
/// </para>
/// </remarks>
internal sealed class PairedLinkStream : LinkStream
{
    /// <summary>The length that abandons a stream instead of carrying a piece.</summary>
    /// <remarks>
    /// Free because no piece can have it: a writer sends at most a block, and
    /// the channel format this shares already refuses lengths past 2 GiB.
    /// </remarks>
    public const uint AbortMarker = uint.MaxValue;

    // Pieces of a write go out no larger than a transfer's block, and a piece
    // read in is handed on in parts no larger either: one size for the pool,
    // and no length a peer announces is ever allocated on its word.
    private const int PieceBytes = TransferFrame.BlockBytes;

    private readonly string _name;
    private readonly ILinkPeer _peer;
    private readonly Stream _transport;
    private readonly TimeSpan _goodbyePatience;
    private readonly TimeProvider _time;
    private readonly CancellationToken _sessionAlive;

    // Two, because an abort stops the writes at once but has to keep reading
    // until the other end has heard it.
    private readonly CancellationTokenSource _stopWriting;
    private readonly CancellationTokenSource _stopReading;

    // Released and never disposed, for the reason OutgoingChannel gives: a
    // disposed SemaphoreSlim abandons its waiters rather than failing them.
    private readonly SemaphoreSlim _writing = new(1, 1);
    private readonly Channel<(byte[] Buffer, int Length)> _inbound =
        Channel.CreateBounded<(byte[] Buffer, int Length)>(
            new BoundedChannelOptions(1) { SingleReader = true, SingleWriter = true });
    private readonly Lock _mu = new();

    private Task _readAhead = Task.CompletedTask;
    private LinkStreamException? _failure;
    private bool _writesCompleted;
    // A piece whose write began and did not finish: the stream is out of step
    // from there, and no marker can follow it.
    private bool _midPiece;
    private bool _disposed;
    private bool _sourcesDisposed;
    private int _transportReleased;

    // The piece a read is part-way through.
    private byte[]? _current;
    private int _currentOffset;
    private int _currentLength;

    /// <param name="name">What the stream was opened as.</param>
    /// <param name="peer">The machine at the other end.</param>
    /// <param name="transport">The session's stream it runs on, already past the frame that opened it.</param>
    /// <param name="goodbyePatience">
    /// How long an ending waits to be heard: for the end marker to go out, or
    /// for the other end to read an abort. A peer that stopped reading grants
    /// no flow control, and disposing must still finish.
    /// </param>
    /// <param name="time">The clock that patience is measured on.</param>
    /// <param name="sessionAlive">Cancelled when the session ends, which ends the stream.</param>
    public PairedLinkStream(
        string name,
        ILinkPeer peer,
        Stream transport,
        TimeSpan goodbyePatience,
        TimeProvider time,
        CancellationToken sessionAlive)
    {
        _name = name;
        _peer = peer;
        _transport = transport;
        _goodbyePatience = goodbyePatience;
        _time = time;
        _sessionAlive = sessionAlive;
        _stopWriting = CancellationTokenSource.CreateLinkedTokenSource(sessionAlive);
        _stopReading = CancellationTokenSource.CreateLinkedTokenSource(sessionAlive);
    }

    /// <summary>Starts reading ahead. Separate from construction so nothing runs on a half-built object.</summary>
    public PairedLinkStream Start()
    {
        _readAhead = Task.Run(ReadAheadAsync, CancellationToken.None);
        return this;
    }

    /// <inheritdoc/>
    public override string Name => _name;

    /// <inheritdoc/>
    public override ILinkPeer Peer => _peer;

    /// <inheritdoc/>
    public override bool CanRead => !_disposed;

    /// <inheritdoc/>
    public override bool CanWrite => !_disposed && !_writesCompleted;

    /// <inheritdoc/>
    public override bool CanSeek => false;

    /// <inheritdoc/>
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc/>
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void Flush()
    {
        // Every write is flushed as it goes.
    }

    /// <inheritdoc/>
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count) =>
        WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    /// <inheritdoc/>
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <inheritdoc/>
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <inheritdoc/>
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Failure() is { Ending: LinkStreamEnding.Aborted } aborted)
        {
            throw Copy(aborted);
        }
        if (buffer.IsEmpty)
        {
            return 0;
        }

        while (_current is null)
        {
            if (_inbound.Reader.TryRead(out (byte[] Buffer, int Length) next))
            {
                (_current, _currentOffset, _currentLength) = (next.Buffer, 0, next.Length);
                break;
            }
            bool more;
            try
            {
                more = await _inbound.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested && Failure() is { } failure)
            {
                // The read-ahead ended the inbound side with why; a fresh
                // exception per read rather than the one instance rethrown.
                throw Copy(failure);
            }
            if (!more)
            {
                if (_inbound.Reader.Completion.IsFaulted && Failure() is { } ended)
                {
                    throw Copy(ended);
                }
                return 0; // the other end finished its half, and all of it is read
            }
        }

        int taken = Math.Min(buffer.Length, _currentLength - _currentOffset);
        _current.AsSpan(_currentOffset, taken).CopyTo(buffer.Span);
        _currentOffset += taken;
        if (_currentOffset == _currentLength)
        {
            ArrayPool<byte>.Shared.Return(_current);
            _current = null;
        }
        return taken;
    }

    /// <inheritdoc/>
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (buffer.IsEmpty)
        {
            // Not sent: a piece of no bytes is what ends a direction on the wire.
            return;
        }

        await _writing.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowUnlessWritable();
            for (int at = 0; at < buffer.Length;)
            {
                // Between pieces, never inside one: a piece stopped part-way
                // leaves a length prefix whose bytes never come, and the other
                // end would read the next prefix out of the middle of this
                // piece's data. So the caller's cancellation is honoured exactly
                // where honouring it leaves the stream whole.
                cancellationToken.ThrowIfCancellationRequested();
                int piece = Math.Min(PieceBytes, buffer.Length - at);
                _midPiece = true;
                await ChannelFrame.WriteAsync(_transport, buffer.Slice(at, piece), _stopWriting.Token).ConfigureAwait(false);
                _midPiece = false;
                at += piece;
            }
        }
        catch (Exception ex) when (ex is not (LinkStreamException or InvalidOperationException)
            && !cancellationToken.IsCancellationRequested
            && SessionFailure.EndsTheSession(ex))
        {
            throw Copy(FailFromTransport(ex));
        }
        finally
        {
            _writing.Release();
        }
    }

    /// <inheritdoc/>
    public override async Task CompleteWritesAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _writing.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_writesCompleted)
            {
                return;
            }
            ThrowUnlessWritable();
            using CancellationTokenSource either =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopWriting.Token);
            await WriteMarkerAsync(0, either.Token).ConfigureAwait(false);
            _writesCompleted = true;
        }
        catch (Exception ex) when (ex is not (LinkStreamException or InvalidOperationException)
            && !cancellationToken.IsCancellationRequested
            && SessionFailure.EndsTheSession(ex))
        {
            throw Copy(FailFromTransport(ex));
        }
        finally
        {
            _writing.Release();
        }
    }

    /// <inheritdoc/>
    public override async Task AbortAsync()
    {
        if (_disposed)
        {
            return;
        }
        if (Fail(LinkStreamEnding.Aborted, $"this end aborted the \"{_name}\" stream").Ending == LinkStreamEnding.Aborted)
        {
            await SayAbortedAsync().ConfigureAwait(false);
        }
        await LetGoAsync().ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public override async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        // Before _disposed is set, so the marker still goes through the one
        // path that writes. Bounded, because waiting for the turn — a writer
        // parked on flow control holds it — and writing can both be held up by
        // a peer that stopped reading. Past the bound the stream is abandoned
        // instead, which the other end hears as such.
        if (Failure() is null && !_writesCompleted)
        {
            using CancellationTokenSource patience = new(_goodbyePatience, _time);
            try
            {
                await CompleteWritesAsync(patience.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                string detail = $"the \"{_name}\" stream could not be ended cleanly within {_goodbyePatience}";
                if (Fail(LinkStreamEnding.Aborted, detail).Ending == LinkStreamEnding.Aborted)
                {
                    await SayAbortedAsync().ConfigureAwait(false);
                }
            }
            catch (LinkStreamException)
            {
                // Already ended another way; there is nothing to say goodbye on.
            }
        }

        _disposed = true;
        // Not an abort: after the end marker the transport ending is what tells
        // the other end this one has let go, and whatever it still sends goes
        // nowhere, as it would to a closed socket.
        await LetGoAsync().ConfigureAwait(false);
        lock (_mu)
        {
            _sourcesDisposed = true;
            _stopWriting.Dispose();
            _stopReading.Dispose();
        }
        await base.DisposeAsync().ConfigureAwait(false);
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        base.Dispose(disposing);
    }

    // Writes the abort marker, unless a piece was cut part-way — the other end
    // then hears the transport stop inside a piece, which is an abort too — and
    // waits, within patience, for the other end to read it and let go.
    private async Task SayAbortedAsync()
    {
        using CancellationTokenSource patience = new(_goodbyePatience, _time);
        using CancellationTokenSource either =
            CancellationTokenSource.CreateLinkedTokenSource(patience.Token, _sessionAlive);
        try
        {
            await _writing.WaitAsync(either.Token).ConfigureAwait(false);
            try
            {
                if (_midPiece)
                {
                    return;
                }
                await WriteMarkerAsync(AbortMarker, either.Token).ConfigureAwait(false);
            }
            finally
            {
                _writing.Release();
            }

            // The read-ahead discards from here, and ends when the other end
            // lets go of the transport — which is it having read the marker.
            await _readAhead.WaitAsync(either.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (SessionFailure.EndsTheSession(ex))
        {
            // Not heard in time, or the session went: letting go says the rest.
        }
    }

    // Stops reading ahead, lets go of the transport and hands back every
    // buffer still held. Safe to run twice.
    private async Task LetGoAsync()
    {
        await _stopWriting.CancelAsync().ConfigureAwait(false);
        await _stopReading.CancelAsync().ConfigureAwait(false);
        await _readAhead.ConfigureAwait(false); // it records its own ending and never throws
        await ReleaseTransportAsync().ConfigureAwait(false);

        while (_inbound.Reader.TryRead(out (byte[] Buffer, int Length) left))
        {
            ArrayPool<byte>.Shared.Return(left.Buffer);
        }
        if (_disposed && _current is { } current)
        {
            _current = null;
            ArrayPool<byte>.Shared.Return(current);
        }
    }

    // Once, and behind any write in progress — which every ending has already
    // cancelled — because disposing a transport under a write races the
    // write's own cleanup.
    private async Task ReleaseTransportAsync()
    {
        if (Interlocked.Exchange(ref _transportReleased, 1) == 1)
        {
            return;
        }
        await _writing.WaitAsync().ConfigureAwait(false);
        try
        {
            await _transport.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _writing.Release();
        }
    }

    private async Task ReadAheadAsync()
    {
        CancellationToken ct = _stopReading.Token;
        byte[] header = new byte[ChannelFrame.HeaderLength];
        try
        {
            bool peerEnded = false;
            while (true)
            {
                if (!await ReadHeaderAsync(header, ct).ConfigureAwait(false))
                {
                    if (peerEnded)
                    {
                        // It kept the transport open for as long as it was
                        // reading, so this is it letting go — which matters to
                        // writes only; reads have already ended cleanly.
                        Fail(LinkStreamEnding.PeerClosed, $"the other machine closed the \"{_name}\" stream");
                    }
                    else
                    {
                        Fail(LinkStreamEnding.PeerAborted, $"the other machine abandoned the \"{_name}\" stream");
                    }
                    return;
                }

                uint length = BinaryPrimitives.ReadUInt32BigEndian(header);
                if (length == AbortMarker)
                {
                    Fail(LinkStreamEnding.PeerAborted, $"the other machine aborted the \"{_name}\" stream");
                    // Letting go is what tells the aborting end it was heard.
                    await ReleaseTransportAsync().ConfigureAwait(false);
                    return;
                }
                if (peerEnded)
                {
                    Fail(LinkStreamEnding.PeerAborted, $"the other machine sent more on the \"{_name}\" stream after ending it");
                    await ReleaseTransportAsync().ConfigureAwait(false);
                    return;
                }
                if (length == 0)
                {
                    peerEnded = true;
                    _inbound.Writer.TryComplete(); // reads end cleanly from here
                    continue;
                }

                for (long remaining = length; remaining > 0;)
                {
                    int part = (int)Math.Min(remaining, PieceBytes);
                    byte[] buffer = ArrayPool<byte>.Shared.Rent(part);
                    try
                    {
                        await _transport.ReadExactlyAsync(buffer.AsMemory(0, part), ct).ConfigureAwait(false);
                        // Waits for the application to read: this is where the
                        // other end is paced.
                        await _inbound.Writer.WriteAsync((buffer, part), ct).ConfigureAwait(false);
                    }
                    catch (ChannelClosedException)
                    {
                        // Reads have ended — this end aborted — and what still
                        // arrives is discarded until the other end lets go.
                        ArrayPool<byte>.Shared.Return(buffer);
                    }
                    catch
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                        throw;
                    }
                    remaining -= part;
                }
            }
        }
        catch (OperationCanceledException) when (_stopReading.IsCancellationRequested && !_sessionAlive.IsCancellationRequested)
        {
            // This end let go; whoever did has already said why.
        }
        catch (EndOfStreamException ex)
        {
            Fail(LinkStreamEnding.PeerAborted, $"the other machine abandoned the \"{_name}\" stream part-way through a write", ex);
        }
        catch (Exception ex) when (SessionFailure.EndsTheSession(ex))
        {
            Fail(LinkStreamEnding.SessionEnded, $"the \"{_name}\" stream ended with its session: {ex.Message}", ex);
        }
    }

    // False on a clean end before any byte of a header.
    private async Task<bool> ReadHeaderAsync(byte[] header, CancellationToken ct)
    {
        if (await _transport.ReadAsync(header.AsMemory(0, 1), ct).ConfigureAwait(false) == 0)
        {
            return false;
        }
        await _transport.ReadExactlyAsync(header.AsMemory(1), ct).ConfigureAwait(false);
        return true;
    }

    private async Task WriteMarkerAsync(uint marker, CancellationToken ct)
    {
        byte[] bytes = new byte[ChannelFrame.HeaderLength];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, marker);
        _midPiece = true;
        await _transport.WriteAsync(bytes, ct).ConfigureAwait(false);
        await _transport.FlushAsync(ct).ConfigureAwait(false);
        _midPiece = false;
    }

    // A write refused because the other end closed its side of this one
    // stream is that end having let go — QUIC tells a writer before the
    // read-ahead has read the end marker in front of the close. Anything else
    // is the session going.
    private LinkStreamException FailFromTransport(Exception ex)
    {
        if (Failure() is { } already)
        {
            return already;
        }
        return ClosedByPeer(ex)
            ? Fail(LinkStreamEnding.PeerClosed, $"the other machine closed the \"{_name}\" stream", ex)
            : Fail(LinkStreamEnding.SessionEnded, $"the \"{_name}\" stream ended with its session: {ex.Message}", ex);
    }

    private static bool ClosedByPeer(Exception? ex)
    {
        for (; ex is not null; ex = ex.InnerException)
        {
            if (ex is QuicException { QuicError: QuicError.StreamAborted })
            {
                return true;
            }
        }
        return false;
    }

    private void ThrowUnlessWritable()
    {
        if (Failure() is { } failure)
        {
            throw Copy(failure);
        }
        if (_writesCompleted)
        {
            throw new InvalidOperationException($"writes on the \"{_name}\" stream were already completed");
        }
    }

    // The first ending wins; the one returned is whichever that was.
    private LinkStreamException Fail(LinkStreamEnding ending, string detail, Exception? cause = null)
    {
        LinkStreamException failure;
        lock (_mu)
        {
            _failure ??= new LinkStreamException(ending, detail, cause);
            failure = _failure;
            // Under the lock disposing takes, so an ending that races the
            // dispose cannot cancel a source already gone.
            if (!_sourcesDisposed)
            {
                _stopWriting.Cancel();
            }
        }
        // Reads that have not reached a clean end fail with it; ones that have
        // keep returning 0, which is still true.
        _inbound.Writer.TryComplete(failure);
        return failure;
    }

    private LinkStreamException? Failure()
    {
        lock (_mu)
        {
            return _failure;
        }
    }

    private static LinkStreamException Copy(LinkStreamException failure) =>
        new(failure.Ending, failure.Message, failure.InnerException);
}

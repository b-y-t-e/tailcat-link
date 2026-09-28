// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Buffers;
using System.Buffers.Binary;
using System.Net.Quic;
using System.Threading.Channels;
using Tailcat.Net;

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
/// Neither rule lets a write decide on its own, because the transport says the
/// same for both endings: the other end let go. Which it was is in the marker
/// that end wrote first, and a write that hears the transport go waits for the
/// read-ahead to reach it — see <see cref="FailFromTransportAsync"/>.
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

    // Cancelled when a write hears the other end let go of the transport.
    // Nothing more can arrive then, so the read-ahead stops waiting for the
    // application and reads on to the marker saying which ending it was.
    private readonly CancellationTokenSource _peerLetGo = new();
    // Set once the read side knows how the stream ended, or knows it never
    // will. A write that heard the other end let go waits on this to be told
    // which ending to report.
    private readonly TaskCompletionSource _endingKnown = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Task _readAhead = Task.CompletedTask;
    private LinkStreamException? _failure;
    private bool _writesCompleted;
    // A piece whose write began and did not finish: the stream is out of step
    // from there, and no marker can follow it.
    private bool _midPiece;
    private bool _disposed;
    private bool _sourcesDisposed;
    private int _transportReleased;

    // The piece a read is part-way through. Only under _currentLock: disposing
    // hands it back to the pool while a read may still be copying out of it,
    // and a buffer returned twice, or read after returning, corrupts the pool.
    private readonly Lock _currentLock = new();
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

        for (;;)
        {
            if (TryTakeFromCurrent(buffer.Span, out int taken))
            {
                return taken;
            }
            if (_inbound.Reader.TryRead(out (byte[] Buffer, int Length) next))
            {
                MakeCurrent(next);
                continue;
            }
            bool more;
            try
            {
                more = await _inbound.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested && ReadFailure() is { } failure)
            {
                // The read-ahead ended the inbound side with why; a fresh
                // exception per read rather than the one instance rethrown.
                throw Copy(failure);
            }
            if (!more)
            {
                if (_inbound.Reader.Completion.IsFaulted && ReadFailure() is { } ended)
                {
                    throw Copy(ended);
                }
                return 0; // the other end finished its half, and all of it is read
            }
        }
    }

    private bool TryTakeFromCurrent(Span<byte> destination, out int taken)
    {
        lock (_currentLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_current is null)
            {
                taken = 0;
                return false;
            }
            taken = Math.Min(destination.Length, _currentLength - _currentOffset);
            _current.AsSpan(_currentOffset, taken).CopyTo(destination);
            _currentOffset += taken;
            if (_currentOffset == _currentLength)
            {
                ArrayPool<byte>.Shared.Return(_current);
                _current = null;
            }
            return true;
        }
    }

    private void MakeCurrent((byte[] Buffer, int Length) piece)
    {
        lock (_currentLock)
        {
            if (_disposed)
            {
                // Disposing may already have handed back what it found; this
                // piece is nobody's now either.
                ArrayPool<byte>.Shared.Return(piece.Buffer);
                ObjectDisposedException.ThrowIf(_disposed, this);
            }
            (_current, _currentOffset, _currentLength) = (piece.Buffer, 0, piece.Length);
        }
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

        Exception? letGo = null;
        await _writing.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowUnlessWritable();
            using CancellationTokenSource either =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopWriting.Token);
            for (int at = 0; at < buffer.Length;)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int piece = Math.Min(PieceBytes, buffer.Length - at);
                _midPiece = true;
                await ChannelFrame.WriteAsync(_transport, buffer.Slice(at, piece), either.Token).ConfigureAwait(false);
                _midPiece = false;
                at += piece;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && _midPiece)
        {
            // A write parked on flow control must still hear its caller, but a
            // piece stopped part-way leaves a length prefix whose bytes never
            // come: the other end would read the next prefix out of the middle
            // of this piece, so the stream cannot go on.
            Fail(LinkStreamEnding.Aborted, $"a write on the \"{_name}\" stream was cancelled part-way through a piece");
            throw;
        }
        catch (Exception ex) when (ex is not (LinkStreamException or InvalidOperationException)
            && !cancellationToken.IsCancellationRequested
            && SessionFailure.EndsTheSession(ex))
        {
            letGo = ex;
        }
        finally
        {
            _writing.Release();
        }

        // Outside the lock, because telling a close from an abort asks the
        // read-ahead, and the read-ahead takes this lock to let go of the
        // transport once it knows.
        if (letGo is not null)
        {
            throw Copy(await FailFromTransportAsync(letGo).ConfigureAwait(false));
        }
    }

    /// <inheritdoc/>
    public override async Task CompleteWritesAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Exception? letGo = null;
        await _writing.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_writesCompleted)
            {
                return;
            }
            ThrowUnlessWritable();
            cancellationToken.ThrowIfCancellationRequested();
            using CancellationTokenSource either =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopWriting.Token);
            await WriteMarkerAsync(0, either.Token).ConfigureAwait(false);
            _writesCompleted = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && _midPiece)
        {
            // Cancelled with the marker part-way out: whatever follows would be
            // read from the middle of it, so the stream cannot go on, and the
            // other end must not take what it got for a clean end either.
            Fail(LinkStreamEnding.Aborted, $"ending the writes on the \"{_name}\" stream was cancelled part-way");
            throw;
        }
        catch (Exception ex) when (ex is not (LinkStreamException or InvalidOperationException)
            && !cancellationToken.IsCancellationRequested
            && SessionFailure.EndsTheSession(ex))
        {
            letGo = ex;
        }
        finally
        {
            _writing.Release();
        }

        if (letGo is not null)
        {
            throw Copy(await FailFromTransportAsync(letGo).ConfigureAwait(false));
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
        if (_sessionAlive.IsCancellationRequested)
        {
            // The end marker would still reach the other machine while the
            // session is being torn down, and tell it that a stream cut off
            // with its session had finished.
            Fail(LinkStreamEnding.SessionEnded, $"the \"{_name}\" stream ended with its session");
        }
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
        // A read still waiting would otherwise wait for good: nothing else
        // completes the inbound side on a clean path. One that already reached
        // the clean end keeps it.
        _inbound.Writer.TryComplete(new ObjectDisposedException(GetType().FullName));
        lock (_mu)
        {
            _sourcesDisposed = true;
            _stopWriting.Dispose();
            _stopReading.Dispose();
            _peerLetGo.Dispose();
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
        StopBothWays();
        await _readAhead.ConfigureAwait(false); // it records its own ending and never throws
        await ReleaseTransportAsync().ConfigureAwait(false);

        while (_inbound.Reader.TryRead(out (byte[] Buffer, int Length) left))
        {
            ArrayPool<byte>.Shared.Return(left.Buffer);
        }
        lock (_currentLock)
        {
            if (_disposed && _current is { } current)
            {
                _current = null;
                ArrayPool<byte>.Shared.Return(current);
            }
        }
    }

    // Under the lock disposing takes: an abort letting go while a dispose
    // finishes would otherwise cancel sources already gone.
    private void StopBothWays()
    {
        lock (_mu)
        {
            if (_sourcesDisposed)
            {
                return;
            }
            _stopWriting.Cancel();
            _stopReading.Cancel();
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
        CancellationToken peerLetGo = _peerLetGo.Token;
        byte[] header = new byte[ChannelFrame.HeaderLength];
        // The last piece handed on, while the application has yet to take it.
        // The next header is read meanwhile — that is how an end that only
        // writes still hears an abort — but no piece's body until it is taken.
        Task handing = Task.CompletedTask;
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
                    // Not waited for: the transport ending after the marker is
                    // how a writer hears the close, however much is unread.
                    peerEnded = true;
                    handing = EndInboundAfterAsync(handing);
                    continue;
                }

                for (long remaining = length; remaining > 0;)
                {
                    // Waits for the application to read: this is where the
                    // other end is paced.
                    await PaceAsync(handing, peerLetGo).ConfigureAwait(false);
                    int part = (int)Math.Min(remaining, PieceBytes);
                    byte[] buffer = ArrayPool<byte>.Shared.Rent(part);
                    try
                    {
                        await _transport.ReadExactlyAsync(buffer.AsMemory(0, part), ct).ConfigureAwait(false);
                    }
                    catch
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                        throw;
                    }
                    handing = HandOnAsync(handing, buffer, part, ct);
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
        catch (Exception ex) when (ClosedByPeer(ex) && !_sessionAlive.IsCancellationRequested)
        {
            // On QUIC a write the other end cancelled part-way aborts its side
            // of the stream, and no marker can follow: that is an abandonment,
            // not the session going.
            Fail(LinkStreamEnding.PeerAborted, $"the other machine abandoned the \"{_name}\" stream part-way through a write", ex);
        }
        catch (Exception ex) when (SessionFailure.EndsTheSession(ex))
        {
            Fail(LinkStreamEnding.SessionEnded, $"the \"{_name}\" stream ended with its session: {ex.Message}", ex);
        }
        finally
        {
            // Nothing else will say how the stream ended, so a write waiting
            // to be told stops waiting even when this end let go instead.
            _endingKnown.TrySetResult();
        }
        // Every other ending has completed the inbound side or cancelled the
        // read. After the other end closed, what it sent is still owed to the
        // application: this waits for it to be read, or for this end to let go,
        // and the writes have already been failed by then.
        await handing.ConfigureAwait(false);
    }

    // Waits for the application to take the piece handed on last, which is
    // what paces the other end. Once that end has let go the wait protects
    // nothing — no more can arrive — and only delays the marker saying how it
    // ended, so the rest is read on and queued behind what is still unread.
    private static async Task PaceAsync(Task handing, CancellationToken peerLetGo)
    {
        try
        {
            await handing.WaitAsync(peerLetGo).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The other end went; the piece stays queued for the application.
        }
    }

    // Never throws: a piece nobody will take goes back to the pool, whether
    // reads have ended — this end aborted, and what still arrives is discarded
    // until the other end lets go — or this end let go.
    private async Task HandOnAsync(Task previous, byte[] buffer, int length, CancellationToken ct)
    {
        try
        {
            // In turn, so pieces reach the application in the order they
            // arrived when the pacing was let past and several are waiting,
            // and so the inbound side keeps its one writer.
            await previous.ConfigureAwait(false);
            await _inbound.Writer.WriteAsync((buffer, length), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ChannelClosedException or OperationCanceledException)
        {
            ArrayPool<byte>.Shared.Return(buffer);
            FailIfSessionEnded(ex);
        }
    }

    // A piece dropped because the session went is data the application never
    // gets: its reads must end with the session, not with the clean end the
    // other machine sent, or a tunnel would finish a connection that was cut.
    private void FailIfSessionEnded(Exception ex)
    {
        if (_sessionAlive.IsCancellationRequested)
        {
            Fail(LinkStreamEnding.SessionEnded, $"the \"{_name}\" stream ended with its session before everything sent was read", ex);
        }
    }

    // Reads end cleanly once the last piece handed on has been taken.
    private async Task EndInboundAfterAsync(Task handing)
    {
        await handing.ConfigureAwait(false);
        _inbound.Writer.TryComplete();
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

    // A write refused because the other end let go of its side of this one
    // stream says that it let go, and no more: QUIC tells a writer that before
    // the read-ahead has read what that end wrote in front of letting go. The
    // marker saying whether it closed or abandoned the stream is already in
    // this end's buffer, so the read-ahead is let past the pacing — nothing
    // more can arrive from an end that has gone — and asked. Only a read-ahead
    // that still cannot say leaves the stream abandoned, which is what bytes
    // nobody accounted for deserve: told of a close, a tunnel would send a
    // clean end for a connection that was cut.
    private async Task<LinkStreamException> FailFromTransportAsync(Exception ex)
    {
        if (Failure() is { } already)
        {
            return already;
        }
        if (!ClosedByPeer(ex))
        {
            return Fail(LinkStreamEnding.SessionEnded, $"the \"{_name}\" stream ended with its session: {ex.Message}", ex);
        }

        LetPastThePacing();
        try
        {
            await _endingKnown.Task.WaitAsync(_goodbyePatience, _time).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // The read-ahead is somewhere no marker will reach it from.
        }
        return Failure() ?? Fail(
            LinkStreamEnding.PeerAborted,
            $"the other machine let go of the \"{_name}\" stream without saying how it ended",
            ex);
    }

    // Under the lock disposing takes, as every other cancellation here is.
    private void LetPastThePacing()
    {
        lock (_mu)
        {
            if (!_sourcesDisposed)
            {
                _peerLetGo.Cancel();
            }
        }
    }

    private static bool ClosedByPeer(Exception? ex)
    {
        for (; ex is not null; ex = ex.InnerException)
        {
            if (ex is QuicException { QuicError: QuicError.StreamAborted } or PeerReleasedStreamException)
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
        if (ending != LinkStreamEnding.PeerClosed)
        {
            // Reads that have not reached a clean end fail with what the read
            // side saw; ones that have keep returning 0, which is still true.
            _inbound.Writer.TryComplete(ReadSideFailure(failure, ending, detail, cause));
        }
        // A write refused because the other end closed says nothing about what
        // it sent before closing: the read-ahead goes on to its end marker, so
        // every byte is read however slowly the application reads.
        _endingKnown.TrySetResult();
        return failure;
    }

    // Once a write has heard the other end close, the transport stopping
    // without its end marker is still an abandonment to the reader.
    private static LinkStreamException ReadSideFailure(
        LinkStreamException failure, LinkStreamEnding ending, string detail, Exception? cause) =>
        failure.Ending == LinkStreamEnding.PeerClosed ? new LinkStreamException(ending, detail, cause) : failure;

    private LinkStreamException? Failure()
    {
        lock (_mu)
        {
            return _failure;
        }
    }

    // What the inbound side was ended with, which may differ from the stream's
    // first ending when a write heard the close before the reader did.
    private LinkStreamException? ReadFailure() =>
        _inbound.Reader.Completion is { IsFaulted: true } completion
            && completion.Exception?.InnerException is LinkStreamException readSide
            ? readSide
            : Failure();

    private static LinkStreamException Copy(LinkStreamException failure) =>
        new(failure.Ending, failure.Message, failure.InnerException);
}

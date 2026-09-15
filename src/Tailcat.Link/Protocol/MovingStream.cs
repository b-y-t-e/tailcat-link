// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

namespace Tailcat.Link.Protocol;

/// <summary>
/// A stream of a session that tells the session whenever bytes move on it.
/// </summary>
/// <remarks>
/// <para>
/// The heartbeat is one ping on a stream of its own, and on a link saturated by
/// a large exchange its answer can queue behind megabytes of that exchange. A
/// ping that took longer than its window used to condemn the session, which
/// then ended the very exchange that was proving the peer alive — a link
/// dropped at 0% of a large upload, over and over, with "sent nothing" as the
/// reason. Every byte that moves is as good a sign of life as a ping answer, so
/// the session keeps count of when any last did.
/// </para>
/// <para>
/// Writes count only once the peer has sent something on this same stream.
/// Flow control is per stream, so every new stream starts with credit a dead
/// peer never has to grant: an application sending into a machine that has
/// gone would otherwise look busy for as long as it kept sending. Once the
/// peer has answered on a stream, a write there completes only as fast as that
/// peer consumes, which is what makes it a sign of the peer.
/// </para>
/// </remarks>
internal sealed class MovingStream : Stream
{
    private readonly Stream _inner;
    private readonly Action _moved;
    private readonly Func<byte, bool> _countsStreamStartingWith;
    private bool _heardFromPeer;
    private bool _ignored;

    /// <param name="inner">The stream underneath.</param>
    /// <param name="moved">Told whenever bytes that count move.</param>
    /// <param name="countsStreamStartingWith">
    /// Decides from the first byte the peer sends whether this stream counts at
    /// all. What keeps the other machine's pings from excusing the silence of
    /// this machine's own: a ping must never be taken for other traffic.
    /// </param>
    public MovingStream(Stream inner, Action moved, Func<byte, bool>? countsStreamStartingWith = null)
    {
        _inner = inner;
        _moved = moved;
        _countsStreamStartingWith = countsStreamStartingWith ?? (_ => true);
    }

    public override bool CanRead => _inner.CanRead;

    public override bool CanSeek => false;

    public override bool CanWrite => _inner.CanWrite;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() => _inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) =>
        Heard(buffer.AsSpan(offset), _inner.Read(buffer, offset, count));

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        return Heard(buffer.Span, read);
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        _inner.Write(buffer, offset, count);
        Sent();
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await _inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        Sent();
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override async ValueTask DisposeAsync()
    {
        await _inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }
        base.Dispose(disposing);
    }

    private int Heard(ReadOnlySpan<byte> buffer, int read)
    {
        if (read <= 0)
        {
            return read;
        }
        if (!_heardFromPeer)
        {
            _heardFromPeer = true;
            _ignored = !_countsStreamStartingWith(buffer[0]);
        }
        Count();
        return read;
    }

    private void Sent()
    {
        if (_heardFromPeer)
        {
            Count();
        }
    }

    private void Count()
    {
        if (!_ignored)
        {
            _moved();
        }
    }
}

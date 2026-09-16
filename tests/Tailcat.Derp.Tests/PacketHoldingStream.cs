// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

namespace Tailcat.Derp.Tests;

/// <summary>
/// A stream that holds back every packet a client sends until
/// <see cref="Release"/>, while the login handshake and everything read pass
/// straight through: a replacement connection up, but slow to take a backlog.
/// </summary>
/// <remarks>
/// Recognises a packet by the frame type in its first byte, which works
/// because <see cref="DerpFrameStream"/> writes each frame as one write.
/// </remarks>
internal sealed class PacketHoldingStream(Stream inner) : Stream
{
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _firstHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes when the first packet is held back.</summary>
    public Task FirstPacketHeld => _firstHeld.Task;

    /// <summary>Lets every held packet, and all that follow, through.</summary>
    public void Release() => _released.TrySetResult();

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        inner.ReadAsync(buffer, cancellationToken);

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (!buffer.IsEmpty && buffer.Span[0] == (byte)DerpFrameType.SendPacket)
        {
            _firstHeld.TrySetResult();
            await _released.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override void Flush() => inner.Flush();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _released.TrySetResult();
            inner.Dispose();
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        _released.TrySetResult();
        await inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}

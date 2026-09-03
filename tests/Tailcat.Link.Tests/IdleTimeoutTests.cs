// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using Tailcat.Link.Protocol;

namespace Tailcat.Link.Tests;

/// <summary>
/// Covers the difference between a transfer that is slow and a peer that has
/// gone away — the two things a single deadline on the whole exchange cannot
/// tell apart, which is what would make a large payload impossible to send
/// through a shared relay however many times it was retried.
/// </summary>
public class IdleTimeoutTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// A frame that takes far longer than the timeout to arrive still
    /// arrives, as long as something keeps arriving.
    /// </summary>
    [Fact]
    public async Task ATransferSlowerThanTheTimeoutStillCompletes()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        byte[] payload = new byte[4096];
        Random.Shared.NextBytes(payload);
        using MemoryStream complete = new();
        await LinkFrame.WriteAsync(
            complete, (byte)LinkFrameKind.Request, Guid.NewGuid(), payload, idle: null, ct);

        // Around two seconds all told — four times the limit — with nothing
        // ever quiet for longer than a fraction of it.
        using TricklingStream slow = new(complete.ToArray(), TimeSpan.FromMilliseconds(20), chunk: 64);
        using IdleTimeout idle = new(Limit, TimeProvider.System);

        (_, _, byte[] body) = await LinkFrame.ReadAsync(slow, idle, idle.Token);

        Assert.Equal(payload, body);
        Assert.False(idle.Expired);
    }

    /// <summary>A peer that stops sending mid-frame is given up on, and quickly.</summary>
    [Fact]
    public async Task ATransferThatStopsIsGivenUpOn()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using MemoryStream complete = new();
        await LinkFrame.WriteAsync(
            complete, (byte)LinkFrameKind.Request, Guid.NewGuid(), new byte[4096], idle: null, ct);

        // Everything but the last byte, and then silence for good.
        using TricklingStream stalling = new(
            complete.ToArray()[..^1], TimeSpan.FromMilliseconds(1), chunk: 4096);
        using IdleTimeout idle = new(Limit, TimeProvider.System);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => LinkFrame.ReadAsync(stalling, idle, idle.Token));

        Assert.True(idle.Expired);
    }

    /// <summary>
    /// A stream that hands over a little at a time and then, having run out,
    /// says nothing for ever — which is what a peer that vanished looks like,
    /// since a relay accepts the bytes nobody is there to receive.
    /// </summary>
    private sealed class TricklingStream(byte[] bytes, TimeSpan perChunk, int chunk) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => bytes.Length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(perChunk, cancellationToken);
            if (_position == bytes.Length)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            int size = Math.Min(Math.Min(chunk, buffer.Length), bytes.Length - _position);
            bytes.AsMemory(_position, size).CopyTo(buffer);
            _position += size;
            return size;
        }

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

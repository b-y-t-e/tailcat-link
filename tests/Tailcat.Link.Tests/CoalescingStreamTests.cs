// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using Tailcat.Link.Protocol;

namespace Tailcat.Link.Tests;

public sealed class CoalescingStreamTests
{
    [Fact]
    public async Task SmallFramesFlushedOneByOneLeaveAsOneWrite()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using CountingStream inner = new();
        using CoalescingStream coalesced = new(inner);

        // What a small exchange is: a frame, its blocks and the end marker,
        // each writer flushing after itself.
        await LinkFrame.WriteAsync(coalesced, (byte)LinkFrameStatus.Ok, Guid.NewGuid(), new byte[40], idle: null, ct);
        await coalesced.WriteAsync(new byte[] { 0, 0, 0, 3, 1, 2, 3 }, ct);
        await coalesced.FlushAsync(ct);
        await coalesced.WriteAsync(new byte[] { 0, 0, 0, 0 }, ct);
        Assert.Equal(0, inner.Writes);

        await coalesced.SendAsync(ct);

        Assert.Equal(1, inner.Writes);
        Assert.Equal(1, inner.Flushes);
    }

    [Fact]
    public async Task AWriteTooLargeToHoldGoesThroughAsItIsAfterWhatWasHeld()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using CountingStream inner = new();
        using CoalescingStream coalesced = new(inner);
        byte[] block = new byte[CoalescingStream.CoalesceBytes + 1];
        block[0] = 7;

        await coalesced.WriteAsync(new byte[] { 1, 2 }, ct);
        await coalesced.WriteAsync(block, ct);
        await coalesced.SendAsync(ct);

        Assert.Equal(2, inner.Writes);
        byte[] written = inner.ToArray();
        Assert.Equal(2 + block.Length, written.Length);
        Assert.Equal(new byte[] { 1, 2, 7 }, written[..3]);
    }

    [Fact]
    public async Task NothingHeldSendsNothing()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using CountingStream inner = new();
        using CoalescingStream coalesced = new(inner);

        await coalesced.SendAsync(ct);

        Assert.Equal(0, inner.Writes);
    }

    [Fact]
    public async Task DisposingTwiceIsHarmless()
    {
        using CountingStream inner = new();
        CoalescingStream coalesced = new(inner);

        await coalesced.DisposeAsync();
        await coalesced.DisposeAsync();

        // The stream underneath belongs to the session, not to this.
        Assert.True(inner.CanWrite);
    }

    private sealed class CountingStream : MemoryStream
    {
        public int Writes { get; private set; }

        public int Flushes { get; private set; }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Writes++;
            return base.WriteAsync(buffer, cancellationToken);
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            Flushes++;
            return Task.CompletedTask;
        }
    }
}

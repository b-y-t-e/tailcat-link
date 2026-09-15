// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Buffers.Binary;
using Tailcat.Link.Protocol;

namespace Tailcat.Link.Tests;

/// <summary>
/// Covers the framing that turns a QUIC byte stream back into messages, and
/// what it does with a peer that is lying or has gone away mid-message.
/// </summary>
public class LinkFrameTests
{
    /// <summary>A frame reads back as what was written, tag and all.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5000)]
    public async Task AFrameRoundTrips(int size)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        byte[] payload = new byte[size];
        Random.Shared.NextBytes(payload);
        using MemoryStream stream = new();

        Guid exchange = Guid.NewGuid();

        await LinkFrame.WriteAsync(stream, (byte)LinkFrameKind.Request, exchange, payload, idle: null, ct);
        stream.Position = 0;
        (byte tag, Guid read, byte[] body) = await LinkFrame.ReadAsync(stream, idle: null, ct);

        Assert.Equal((byte)LinkFrameKind.Request, tag);
        Assert.Equal(exchange, read);
        Assert.Equal(payload, body);
    }

    /// <summary>Frames written back to back stay separate, in order.</summary>
    [Fact]
    public async Task FramesDoNotRunIntoEachOther()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using MemoryStream stream = new();

        await LinkFrame.WriteAsync(stream, (byte)LinkFrameKind.Notify, Guid.NewGuid(), "first"u8.ToArray(), idle: null, ct);
        await LinkFrame.WriteAsync(stream, (byte)LinkFrameKind.Ping, Guid.NewGuid(), "second"u8.ToArray(), idle: null, ct);
        stream.Position = 0;

        Assert.Equal("first"u8.ToArray(), (await LinkFrame.ReadAsync(stream, idle: null, ct)).Payload);
        Assert.Equal("second"u8.ToArray(), (await LinkFrame.ReadAsync(stream, idle: null, ct)).Payload);
    }

    /// <summary>
    /// A message larger than the sixteen megabytes frames used to be capped at
    /// is written and read like any other.
    /// </summary>
    [Fact]
    public async Task AMessageHasNoSizeLimit()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        byte[] large = new byte[(16 * 1024 * 1024) + 12345];
        Random.Shared.NextBytes(large);
        using MemoryStream stream = new();

        await LinkFrame.WriteAsync(stream, (byte)LinkFrameKind.Request, Guid.NewGuid(), large, idle: null, ct);
        stream.Position = 0;

        Assert.Equal(large, (await LinkFrame.ReadAsync(stream, idle: null, ct)).Payload);
    }

    /// <summary>
    /// A length that reads negative is past what one array can hold, and is
    /// said to be rather than attempted.
    /// </summary>
    [Fact]
    public async Task ALengthNoArrayCanHoldIsRefused()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using MemoryStream stream = new(HeaderAnnouncing(-1));

        await Assert.ThrowsAsync<LinkException>(() => LinkFrame.ReadAsync(stream, idle: null, ct));
    }

    /// <summary>
    /// A peer announcing gigabytes and sending nothing costs what it sent, not
    /// what it announced — which is what used to need the cap.
    /// </summary>
    [Fact]
    public async Task AnAnnouncedLengthIsNotAllocatedOnTheAnnouncement()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using MemoryStream stream = new(HeaderAnnouncing(int.MaxValue));

        // A MemoryStream answers synchronously, so the whole read happens on
        // this thread and is what this counter sees.
        long before = GC.GetAllocatedBytesForCurrentThread();
        await Assert.ThrowsAsync<EndOfStreamException>(() => LinkFrame.ReadAsync(stream, idle: null, ct));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated < 1024 * 1024, $"announcing two gigabytes allocated {allocated} bytes");
    }

    /// <summary>
    /// The one bounded read — the hello from a machine not yet known — refuses
    /// more than its bound before reading any of it.
    /// </summary>
    [Fact]
    public async Task ABoundedReadRefusesMoreThanItsBound()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using MemoryStream stream = new(HeaderAnnouncing(LinkProtocol.HelloFrameBytes + 1));

        await Assert.ThrowsAsync<LinkException>(
            () => LinkFrame.ReadAsync(stream, LinkProtocol.HelloFrameBytes, idle: null, ct));
    }

    private static byte[] HeaderAnnouncing(int length)
    {
        byte[] header = new byte[LinkFrame.HeaderLength];
        header[0] = (byte)LinkFrameKind.Request;
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(LinkFrame.HeaderLength - 4), length);
        return header;
    }

    /// <summary>A peer that stopped mid-frame ends the read, rather than returning half a message.</summary>
    [Fact]
    public async Task ATruncatedFrameIsAnEndOfStream()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using MemoryStream complete = new();
        await LinkFrame.WriteAsync(complete, (byte)LinkFrameKind.Request, Guid.NewGuid(), "abcdef"u8.ToArray(), idle: null, ct);

        using MemoryStream truncated = new(complete.ToArray()[..(LinkFrame.HeaderLength + 3)]);

        await Assert.ThrowsAsync<EndOfStreamException>(() => LinkFrame.ReadAsync(truncated, idle: null, ct));
    }
}

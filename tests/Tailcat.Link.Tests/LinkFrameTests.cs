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

    /// <summary>A message beyond the limit is refused before it is sent.</summary>
    [Fact]
    public async Task AnOversizedMessageIsRefused()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using MemoryStream stream = new();
        byte[] tooBig = new byte[LinkFrame.MaxPayloadBytes + 1];

        await Assert.ThrowsAsync<LinkException>(
            () => LinkFrame.WriteAsync(stream, (byte)LinkFrameKind.Request, Guid.NewGuid(), tooBig, idle: null, ct));
    }

    /// <summary>
    /// A peer claiming a length nobody could send is refused before the memory
    /// is allocated — which is the point of having a limit at all.
    /// </summary>
    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(-1)]
    public async Task AnImpossibleLengthIsRefused(int announced)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        byte[] header = new byte[LinkFrame.HeaderLength];
        header[0] = (byte)LinkFrameKind.Request;
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(LinkFrame.HeaderLength - 4), announced);
        using MemoryStream stream = new(header);

        await Assert.ThrowsAsync<LinkException>(() => LinkFrame.ReadAsync(stream, idle: null, ct));
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

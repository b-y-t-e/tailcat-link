// Copyright (c) Tailscale Inc & contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Buffers.Binary;

namespace Tailcat.Derp.Tests;

/// <summary>
/// Covers DERP's frame layer: a type byte, a big-endian uint32 length, then
/// that many payload bytes.
/// </summary>
public class DerpFrameStreamTests
{
    [Fact]
    public async Task FrameRoundTrips()
    {
        MemoryStream buf = new();
        DerpFrameStream writer = new(buf);
        byte[] payload = [1, 2, 3, 4, 5];

        await writer.WriteFrameAsync(DerpFrameType.SendPacket, payload, TestContext.Current.CancellationToken);

        buf.Position = 0;
        DerpFrameStream reader = new(buf);
        DerpFrame got = await reader.ReadFrameAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DerpFrameType.SendPacket, got.Type);
        Assert.Equal(payload, got.Payload.ToArray());
    }

    /// <summary>The header is exactly a type byte plus a big-endian length.</summary>
    [Fact]
    public async Task HeaderLayoutIsTypeThenBigEndianLength()
    {
        MemoryStream buf = new();
        DerpFrameStream writer = new(buf);

        await writer.WriteFrameAsync(DerpFrameType.Ping, new byte[258], TestContext.Current.CancellationToken);

        byte[] bytes = buf.ToArray();
        Assert.Equal((byte)DerpFrameType.Ping, bytes[0]);
        Assert.Equal(258u, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(1, 4)));
        Assert.Equal(DerpProtocol.FrameHeaderLen + 258, bytes.Length);
    }

    [Fact]
    public async Task EmptyPayloadRoundTrips()
    {
        MemoryStream buf = new();
        await new DerpFrameStream(buf).WriteFrameAsync(
            DerpFrameType.KeepAlive, ReadOnlyMemory<byte>.Empty, TestContext.Current.CancellationToken);

        buf.Position = 0;
        DerpFrame got = await new DerpFrameStream(buf).ReadFrameAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DerpFrameType.KeepAlive, got.Type);
        Assert.Equal(0, got.Payload.Length);
    }

    [Fact]
    public async Task ManyFramesReadBackInOrder()
    {
        MemoryStream buf = new();
        DerpFrameStream writer = new(buf);
        for (int i = 0; i < 5; i++)
        {
            await writer.WriteFrameAsync(DerpFrameType.SendPacket, new byte[] { (byte)i }, TestContext.Current.CancellationToken);
        }

        buf.Position = 0;
        DerpFrameStream reader = new(buf);
        for (int i = 0; i < 5; i++)
        {
            DerpFrame got = await reader.ReadFrameAsync(TestContext.Current.CancellationToken);
            Assert.Equal((byte)i, got.Payload.Span[0]);
        }
    }

    /// <summary>
    /// A frame claiming an absurd length must be rejected on the header
    /// alone, before any allocation the size implies.
    /// </summary>
    [Fact]
    public async Task OversizedFrameIsRejected()
    {
        MemoryStream buf = new();
        buf.WriteByte((byte)DerpFrameType.RecvPacket);
        byte[] len = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(len, uint.MaxValue);
        buf.Write(len);
        buf.Position = 0;

        await Assert.ThrowsAsync<DerpProtocolException>(
            () => new DerpFrameStream(buf).ReadFrameAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>A connection cut mid-frame is an error, not a short frame.</summary>
    [Fact]
    public async Task TruncatedFrameThrows()
    {
        MemoryStream buf = new();
        DerpFrameStream writer = new(buf);
        await writer.WriteFrameAsync(DerpFrameType.SendPacket, new byte[10], TestContext.Current.CancellationToken);

        MemoryStream truncated = new(buf.ToArray()[..8]);

        await Assert.ThrowsAsync<EndOfStreamException>(
            () => new DerpFrameStream(truncated).ReadFrameAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WritingAnOversizedPayloadIsRejected()
    {
        DerpFrameStream writer = new(new MemoryStream());

        await Assert.ThrowsAsync<ArgumentException>(
            () => writer.WriteFrameAsync(
                DerpFrameType.SendPacket,
                new byte[DerpProtocol.MaxPacketSize + 2048],
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DisposingTwiceIsHarmless()
    {
        DerpFrameStream frames = new(new MemoryStream());

        await frames.DisposeAsync();
        await frames.DisposeAsync();
    }
}

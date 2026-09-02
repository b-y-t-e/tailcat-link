// Copyright (c) Tailscale Inc & contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Text;
using Tailcat.Keys;

namespace Tailcat.Derp.Tests;

/// <summary>
/// Covers the DERP login handshake and packet exchange against an in-memory
/// relay, so nothing here needs the network.
/// </summary>
public class DerpClientTests
{
    private static async Task<DerpClient> ConnectAsync(FakeDerpRelay relay, NodePrivate key, CancellationToken ct) =>
        await DerpClient.ConnectOverStreamAsync(await relay.DialAsync(ct), key, relay.PublicKey, ct);

    [Fact]
    public async Task HandshakeLearnsTheRelayKeyAndReportsOurOwn()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using FakeDerpRelay relay = new();
        NodePrivate key = NodePrivate.NewKey();

        await using DerpClient client = await ConnectAsync(relay, key, ct);

        Assert.Equal(relay.PublicKey, client.ServerPublicKey);
        Assert.Equal(key.Public(), client.PublicKey);
        Assert.Equal(relay.ServerInfo.TokenBucketBytesPerSecond, client.ServerInfo.TokenBucketBytesPerSecond);
    }

    /// <summary>
    /// The client announces the protocol version whose framing it actually
    /// speaks — version 2, where received packets carry the source key.
    /// </summary>
    [Fact]
    public async Task ClientAnnouncesProtocolVersionTwo()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using FakeDerpRelay relay = new();

        await using DerpClient client = await ConnectAsync(relay, NodePrivate.NewKey(), ct);

        Assert.NotNull(relay.LastClientInfo);
        Assert.Equal(DerpProtocol.ProtocolVersion, relay.LastClientInfo.Version);
        Assert.True(relay.LastClientInfo.CanAckPings);
    }

    /// <summary>
    /// A relay whose greeting doesn't match the key its certificate
    /// advertised is rejected: that mismatch is what a rewritten connection
    /// would look like.
    /// </summary>
    [Fact]
    public async Task MismatchedRelayKeyIsRejected()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using FakeDerpRelay relay = new();
        NodePublic impostor = NodePrivate.NewKey().Public();

        await Assert.ThrowsAsync<DerpProtocolException>(async () =>
            await DerpClient.ConnectOverStreamAsync(await relay.DialAsync(ct), NodePrivate.NewKey(), impostor, ct));
    }

    [Fact]
    public async Task PacketsAreRoutedBetweenClientsByPublicKey()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using FakeDerpRelay relay = new();

        await using DerpClient a = await ConnectAsync(relay, NodePrivate.NewKey(), ct);
        await using DerpClient b = await ConnectAsync(relay, NodePrivate.NewKey(), ct);
        await relay.WaitForClientAsync(b.PublicKey, ct);

        Task<DerpReceivedPacket> received = b.ReceiveAsync(ct);
        byte[] payload = Encoding.UTF8.GetBytes("across the relay");
        await a.SendAsync(b.PublicKey, payload, ct);

        DerpReceivedPacket got = await received;
        Assert.Equal(a.PublicKey, got.Source);
        Assert.Equal(payload, got.Payload.ToArray());
    }

    /// <summary>
    /// The relay pings to check liveness; a client that doesn't answer gets
    /// dropped, so the pong must go out even while waiting for a packet.
    /// </summary>
    [Fact]
    public async Task RelayPingsAreAnsweredWhileReceiving()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using FakeDerpRelay relay = new();

        await using DerpClient a = await ConnectAsync(relay, NodePrivate.NewKey(), ct);
        await using DerpClient b = await ConnectAsync(relay, NodePrivate.NewKey(), ct);
        await relay.WaitForClientAsync(b.PublicKey, ct);

        Task<DerpReceivedPacket> received = b.ReceiveAsync(ct);

        // A ping mid-wait must not disturb the packet that follows it.
        await relay.PingAsync(b.PublicKey, [1, 2, 3, 4, 5, 6, 7, 8], ct);
        await a.SendAsync(b.PublicKey, "after the ping"u8.ToArray(), ct);

        DerpReceivedPacket got = await received;
        Assert.Equal("after the ping", Encoding.UTF8.GetString(got.Payload.Span));
    }

    /// <summary>Bookkeeping frames must not be mistaken for packets.</summary>
    [Fact]
    public async Task PeerGoneDoesNotSurfaceAsAPacket()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using FakeDerpRelay relay = new();

        await using DerpClient a = await ConnectAsync(relay, NodePrivate.NewKey(), ct);
        await using DerpClient b = await ConnectAsync(relay, NodePrivate.NewKey(), ct);
        await relay.WaitForClientAsync(b.PublicKey, ct);

        Task<DerpReceivedPacket> received = a.ReceiveAsync(ct);

        // Sending to an unknown peer makes the relay answer PeerGone.
        await a.SendAsync(NodePrivate.NewKey().Public(), "into the void"u8.ToArray(), ct);
        await b.SendAsync(a.PublicKey, "the real packet"u8.ToArray(), ct);

        DerpReceivedPacket got = await received;
        Assert.Equal(b.PublicKey, got.Source);
        Assert.Equal("the real packet", Encoding.UTF8.GetString(got.Payload.Span));
    }

    [Fact]
    public async Task OversizedPacketIsRejectedBeforeSending()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using FakeDerpRelay relay = new();
        await using DerpClient a = await ConnectAsync(relay, NodePrivate.NewKey(), ct);

        await Assert.ThrowsAsync<ArgumentException>(
            () => a.SendAsync(NodePrivate.NewKey().Public(), new byte[DerpProtocol.MaxPacketSize + 1], ct));
    }
}

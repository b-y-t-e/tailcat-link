// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Tailcat.Derp;
using Tailcat.Keys;

namespace Tailcat.Net.Tests;

/// <summary>
/// Covers the bridge that carries a local QUIC endpoint's datagrams over a
/// peer link.
/// </summary>
public class UdpBridgeTests
{
    private sealed class FakeRelay(NodePublic publicKey) : IRelay
    {
        private readonly Channel<DerpReceivedPacket> _packets = Channel.CreateUnbounded<DerpReceivedPacket>();

        public NodePublic PublicKey { get; } = publicKey;

        public ChannelReader<DerpReceivedPacket> Packets => _packets.Reader;

        public List<byte[]> Sent { get; } = [];

        public Task SendAsync(NodePublic destination, ReadOnlyMemory<byte> packet, CancellationToken cancellationToken = default)
        {
            lock (Sent)
            {
                Sent.Add(packet.ToArray());
            }
            return Task.CompletedTask;
        }
    }

    private static (PeerLink Link, FakeRelay Relay, Socket Udp) NewLink()
    {
        NodePrivate self = NodePrivate.NewKey();
        FakeRelay relay = new(self.Public());
        Socket udp = new(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp) { DualMode = true };
        udp.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));
        return (new PeerLink(self, NodePrivate.NewKey().Public(), 1, relay, udp), relay, udp);
    }

    /// <summary>
    /// What the local QUIC stack sends to the bridge goes out over the link.
    /// </summary>
    [Fact]
    public async Task DatagramsFromQuicGoOutOverTheLink()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        (PeerLink link, FakeRelay relay, Socket udp) = NewLink();
        using (udp)
        await using (link)
        await using (UdpBridge bridge = new(link))
        {
            bridge.Start();

            using Socket quic = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            await quic.SendToAsync("a quic packet"u8.ToArray(), SocketFlags.None, bridge.LocalEndPoint, ct);

            byte[]? sent = await WaitForSentAsync(relay, ct);
            Assert.NotNull(sent);
            Assert.Equal(PeerMessageType.Data, PeerMessage.TypeOf(sent));
            Assert.Equal("a quic packet"u8.ToArray(), PeerMessage.DecodeData(sent).ToArray());
        }
    }

    /// <summary>
    /// A datagram from the peer reaches the local QUIC endpoint, and reaches it
    /// intact even when the caller reuses the buffer straight afterwards.
    /// </summary>
    /// <remarks>
    /// The node's receive loop reads every packet into one shared buffer, so a
    /// bridge that forwarded that memory without copying would let the next
    /// packet overwrite the bytes mid-send. That corruption only appears once
    /// a direct path is in use and only under load, and reads as random loss.
    /// </remarks>
    [Fact]
    public async Task DatagramFromThePeerSurvivesTheBufferBeingReused()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        (PeerLink link, FakeRelay relay, Socket udp) = NewLink();
        using (udp)
        await using (link)
        await using (UdpBridge bridge = new(link))
        {
            bridge.Start();

            // The bridge learns where to deliver from the first packet it gets,
            // so wait until that packet has actually been forwarded.
            using Socket quic = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            quic.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            await quic.SendToAsync("hello"u8.ToArray(), SocketFlags.None, bridge.LocalEndPoint, ct);
            Assert.NotNull(await WaitForSentAsync(relay, ct));

            // One reused buffer, exactly as the receive loop has.
            byte[] shared = new byte[256];
            byte[] payload = "the real payload"u8.ToArray();
            PeerMessage.EncodeData(payload).CopyTo(shared, 0);
            int length = PeerMessage.HeaderLen + payload.Length;

            await link.HandlePacketAsync(shared.AsMemory(0, length), null, ct);

            // Whatever came next lands in the same buffer.
            shared.AsSpan().Fill(0xff);

            byte[] received = new byte[256];
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            SocketReceiveFromResult res = await quic.ReceiveFromAsync(
                received, SocketFlags.None, new IPEndPoint(IPAddress.Loopback, 0), cts.Token);

            Assert.Equal(payload, received[..res.ReceivedBytes]);
        }
    }

    /// <summary>Before the QUIC stack has spoken there is nowhere to deliver.</summary>
    [Fact]
    public async Task DatagramsArriveHarmlesslyBeforeQuicSpeaks()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        (PeerLink link, _, Socket udp) = NewLink();
        using (udp)
        await using (link)
        await using (UdpBridge bridge = new(link))
        {
            bridge.Start();

            await link.HandlePacketAsync(PeerMessage.EncodeData("dropped"u8), null, ct);
        }
    }

    [Fact]
    public async Task DisposingTwiceIsHarmless()
    {
        (PeerLink link, _, Socket udp) = NewLink();
        using (udp)
        await using (link)
        {
            UdpBridge bridge = new(link);
            bridge.Start();

            await bridge.DisposeAsync();
            await bridge.DisposeAsync();
        }
    }

    private static async Task<byte[]?> WaitForSentAsync(FakeRelay relay, CancellationToken ct)
    {
        for (int i = 0; i < 100; i++)
        {
            lock (relay.Sent)
            {
                byte[]? data = relay.Sent.FirstOrDefault(p => PeerMessage.TypeOf(p) == PeerMessageType.Data);
                if (data is not null)
                {
                    return data;
                }
            }
            await Task.Delay(50, ct);
        }
        return null;
    }
}

// Copyright (c) Tailscale Inc & contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Tailcat.Derp;
using Tailcat.Keys;

namespace Tailcat.Net.Tests;

/// <summary>
/// Covers what happens when a node's addresses change underneath it — moving
/// from Wi-Fi to a mobile network, say. Without this, every address a peer
/// knows goes stale at once and the session quietly dies on the relay.
/// </summary>
public class EndpointUpdateTests
{
    private sealed class FakeRelay(NodePublic publicKey) : IRelay
    {
        private readonly Channel<DerpReceivedPacket> _packets = Channel.CreateUnbounded<DerpReceivedPacket>();

        public NodePublic PublicKey { get; } = publicKey;

        public ChannelReader<DerpReceivedPacket> Packets => _packets.Reader;

        public Task SendAsync(NodePublic destination, ReadOnlyMemory<byte> packet, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private static (PeerLink Link, NodePrivate Self, NodePrivate Peer, Socket Udp) NewLink(ulong sessionId = 5)
    {
        NodePrivate self = NodePrivate.NewKey();
        NodePrivate peer = NodePrivate.NewKey();
        Socket udp = new(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp) { DualMode = true };
        udp.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));
        return (new PeerLink(self, peer.Public(), sessionId, new FakeRelay(self.Public()), udp), self, peer, udp);
    }

    /// <summary>An update from the peer adds its new addresses as candidates.</summary>
    [Fact]
    public async Task EndpointUpdateAddsNewCandidates()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        (PeerLink link, NodePrivate self, NodePrivate peer, Socket udp) = NewLink(sessionId: 5);
        using (udp)
        await using (link)
        {
            link.AddCandidates([new IPEndPoint(IPAddress.Parse("192.168.1.10"), 41641)]);

            PeerHello update = new(
                SessionId: 5,
                CertificateFingerprint: new byte[PeerHello.FingerprintLen],
                Endpoints: [new IPEndPoint(IPAddress.Parse("10.55.0.9"), 51820)],
                HomeRegionId: 1);
            byte[] msg = PeerMessage.Seal(
                PeerMessageType.EndpointUpdate, update.Encode(), peer, self.Public());

            await link.HandlePacketAsync(msg, null, ct);

            Assert.Contains(link.Paths, p => Equals(p.Remote, new IPEndPoint(IPAddress.Parse("10.55.0.9"), 51820)));
            // The old candidate stays until it ages out on its own.
            Assert.Contains(link.Paths, p => Equals(p.Remote, new IPEndPoint(IPAddress.Parse("192.168.1.10"), 41641)));
        }
    }

    /// <summary>An update for another session must not touch this one's paths.</summary>
    [Fact]
    public async Task EndpointUpdateForAnotherSessionIsIgnored()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        (PeerLink link, NodePrivate self, NodePrivate peer, Socket udp) = NewLink(sessionId: 5);
        using (udp)
        await using (link)
        {
            PeerHello update = new(
                SessionId: 6,
                CertificateFingerprint: new byte[PeerHello.FingerprintLen],
                Endpoints: [new IPEndPoint(IPAddress.Parse("10.55.0.9"), 51820)],
                HomeRegionId: 1);
            byte[] msg = PeerMessage.Seal(PeerMessageType.EndpointUpdate, update.Encode(), peer, self.Public());

            await link.HandlePacketAsync(msg, null, ct);

            Assert.DoesNotContain(link.Paths, p => p.Kind == PeerPathKind.Direct);
        }
    }

    /// <summary>
    /// A forged update is dropped: otherwise anyone able to reach the node
    /// could point a live session's traffic at an address of their choosing.
    /// </summary>
    [Fact]
    public async Task ForgedEndpointUpdateIsIgnored()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        (PeerLink link, NodePrivate self, _, Socket udp) = NewLink(sessionId: 5);
        using (udp)
        await using (link)
        {
            NodePrivate impostor = NodePrivate.NewKey();
            PeerHello update = new(
                SessionId: 5,
                CertificateFingerprint: new byte[PeerHello.FingerprintLen],
                Endpoints: [new IPEndPoint(IPAddress.Parse("203.0.113.66"), 9999)],
                HomeRegionId: 1);
            byte[] forged = PeerMessage.Seal(PeerMessageType.EndpointUpdate, update.Encode(), impostor, self.Public());

            await link.HandlePacketAsync(forged, null, ct);

            Assert.DoesNotContain(link.Paths, p => p.Kind == PeerPathKind.Direct);
        }
    }

    /// <summary>
    /// A hello round-trips its home region, which is what lets the answer go
    /// to the region the sender actually listens in.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(301)]
    public void HelloCarriesTheHomeRegion(int regionId)
    {
        PeerHello hello = new(
            SessionId: 1,
            CertificateFingerprint: new byte[PeerHello.FingerprintLen],
            Endpoints: [new IPEndPoint(IPAddress.Loopback, 1234)],
            HomeRegionId: regionId);

        Assert.True(PeerHello.TryDecode(hello.Encode(), out PeerHello? got));
        Assert.Equal(regionId, got.HomeRegionId);
        Assert.Equal(hello.Endpoints, got.Endpoints);
    }

    /// <summary>
    /// An IPv4 peer seen through a dual-stack socket must not become a second,
    /// separate path from the same peer's IPv4 candidate.
    /// </summary>
    [Fact]
    public void MappedAddressesNormalizeToOnePath()
    {
        IPEndPoint plain = new(IPAddress.Parse("192.0.2.7"), 4242);
        IPEndPoint mapped = new(IPAddress.Parse("192.0.2.7").MapToIPv6(), 4242);

        Assert.Equal(plain, PeerLink.Normalize(mapped));
        Assert.Equal(plain, PeerLink.Normalize(plain));

        // A real IPv6 address is left alone.
        IPEndPoint v6 = new(IPAddress.Parse("2001:db8::1"), 4242);
        Assert.Equal(v6, PeerLink.Normalize(v6));
    }

    /// <summary>A candidate given in mapped form lands on the same path as the plain one.</summary>
    [Fact]
    public async Task MappedCandidateDoesNotDuplicateAPath()
    {
        (PeerLink link, _, _, Socket udp) = NewLink();
        using (udp)
        await using (link)
        {
            link.AddCandidates([new IPEndPoint(IPAddress.Parse("192.0.2.7"), 4242)]);
            link.AddCandidates([new IPEndPoint(IPAddress.Parse("192.0.2.7").MapToIPv6(), 4242)]);

            Assert.Single(link.Paths, p => p.Kind == PeerPathKind.Direct);
        }
    }
}

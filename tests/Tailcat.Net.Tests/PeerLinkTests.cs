// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Tailcat.Derp;
using Tailcat.Keys;

namespace Tailcat.Net.Tests;

/// <summary>
/// Covers how a link chooses between the relay and a direct path, and how it
/// treats probes. These run against an in-memory relay and a loopback UDP
/// socket, so no network is involved.
/// </summary>
public class PeerLinkTests
{
    // A relay that hands packets straight to whoever is registered for the
    // destination key, standing in for a DERP server.
    private sealed class FakeRelay(NodePublic publicKey) : IRelay
    {
        private readonly Channel<DerpReceivedPacket> _packets = Channel.CreateUnbounded<DerpReceivedPacket>();

        public NodePublic PublicKey { get; } = publicKey;

        public ChannelReader<DerpReceivedPacket> Packets => _packets.Reader;

        public List<(NodePublic Destination, byte[] Packet)> Sent { get; } = [];

        public FakeRelay? Peer { get; set; }

        public Task SendAsync(NodePublic destination, ReadOnlyMemory<byte> packet, CancellationToken cancellationToken = default)
        {
            lock (Sent)
            {
                Sent.Add((destination, packet.ToArray()));
            }
            Peer?._packets.Writer.TryWrite(new DerpReceivedPacket(PublicKey, packet.ToArray()));
            return Task.CompletedTask;
        }
    }

    private sealed record Harness(
        PeerLink Link,
        FakeRelay Relay,
        Socket Udp,
        NodePrivate SelfKey,
        NodePrivate PeerKey) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Link.DisposeAsync();
            Udp.Dispose();
        }
    }

    private static Harness NewLink(ulong sessionId = 42, TimeProvider? time = null)
    {
        NodePrivate self = NodePrivate.NewKey();
        NodePrivate peer = NodePrivate.NewKey();
        FakeRelay relay = new(self.Public());
        Socket udp = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        udp.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        PeerLink link = new(self, peer.Public(), sessionId, relay, udp, time);
        return new Harness(link, relay, udp, self, peer);
    }

    /// <summary>Before any path is confirmed, everything goes over the relay.</summary>
    [Fact]
    public async Task StartsOnTheRelay()
    {
        await using Harness h = NewLink();

        Assert.Equal(PeerPathKind.Relay, h.Link.CurrentPath.Kind);

        await h.Link.SendDatagramAsync("payload"u8.ToArray(), TestContext.Current.CancellationToken);

        lock (h.Relay.Sent)
        {
            Assert.Single(h.Relay.Sent);
            Assert.Equal(h.PeerKey.Public(), h.Relay.Sent[0].Destination);
            Assert.Equal(PeerMessageType.Data, PeerMessage.TypeOf(h.Relay.Sent[0].Packet));
        }
    }

    /// <summary>Candidates that name no real address are not worth probing.</summary>
    [Fact]
    public async Task UnusableCandidatesAreIgnored()
    {
        await using Harness h = NewLink();

        h.Link.AddCandidates(
        [
            new IPEndPoint(IPAddress.Any, 41641),
            new IPEndPoint(IPAddress.IPv6Any, 41641),
            new IPEndPoint(IPAddress.Parse("192.0.2.1"), 0),
        ]);

        Assert.Single(h.Link.Paths);
        Assert.Equal(PeerPathKind.Relay, Assert.Single(h.Link.Paths).Kind);
    }

    [Fact]
    public async Task CandidatesBecomePaths()
    {
        await using Harness h = NewLink();

        h.Link.AddCandidates([new IPEndPoint(IPAddress.Parse("192.0.2.1"), 41641)]);

        Assert.Equal(2, h.Link.Paths.Count);
        PeerPath direct = h.Link.Paths.Single(p => p.Kind == PeerPathKind.Direct);
        Assert.Equal(PeerLink.BaseMtu, direct.Mtu);

        // Until it answers, it isn't used.
        Assert.Equal(PeerPathKind.Relay, h.Link.CurrentPath.Kind);
    }

    /// <summary>
    /// A probe arriving over UDP is answered, and its source becomes a
    /// candidate: that is how the side behind a punched-open NAT is found.
    /// </summary>
    [Fact]
    public async Task AnIncomingProbeIsAnsweredAndItsSourceLearned()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using Harness h = NewLink(sessionId: 7);

        // A socket standing in for the peer, so we can see the answer.
        using Socket peerSocket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        peerSocket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        IPEndPoint peerAddr = (IPEndPoint)peerSocket.LocalEndPoint!;

        byte[] ping = PeerMessage.Seal(
            PeerMessageType.Ping, new PeerPing(99, 7).Encode(), h.PeerKey, h.SelfKey.Public());
        await h.Link.HandlePacketAsync(ping, peerAddr, ct);

        byte[] buf = new byte[2048];
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(5));
        SocketReceiveFromResult res = await peerSocket.ReceiveFromAsync(
            buf, SocketFlags.None, new IPEndPoint(IPAddress.Loopback, 0), cts.Token);

        Assert.True(PeerMessage.TryOpen(
            buf.AsSpan(0, res.ReceivedBytes), h.PeerKey, h.SelfKey.Public(),
            out PeerMessageType type, out byte[]? payload));
        Assert.Equal(PeerMessageType.Pong, type);
        Assert.True(PeerPing.TryDecode(payload, out PeerPing pong));
        Assert.Equal(99ul, pong.Id);

        // The address it came from is now a candidate path.
        Assert.Contains(h.Link.Paths, p => Equals(p.Remote, peerAddr));
    }

    /// <summary>A probe for another session must not touch this link's paths.</summary>
    [Fact]
    public async Task ProbeForAnotherSessionIsIgnored()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using Harness h = NewLink(sessionId: 7);
        IPEndPoint from = new(IPAddress.Parse("192.0.2.55"), 4242);

        byte[] ping = PeerMessage.Seal(
            PeerMessageType.Ping, new PeerPing(1, SessionId: 8).Encode(), h.PeerKey, h.SelfKey.Public());
        await h.Link.HandlePacketAsync(ping, from, ct);

        Assert.DoesNotContain(h.Link.Paths, p => Equals(p.Remote, from));
    }

    /// <summary>
    /// A probe that isn't sealed by the peer is dropped. Otherwise anyone who
    /// could send us a packet could add a path and take over the session's
    /// traffic.
    /// </summary>
    [Fact]
    public async Task ForgedProbeIsIgnored()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using Harness h = NewLink(sessionId: 7);
        IPEndPoint attacker = new(IPAddress.Parse("198.51.100.9"), 6666);

        NodePrivate impostor = NodePrivate.NewKey();
        byte[] forged = PeerMessage.Seal(
            PeerMessageType.Ping, new PeerPing(1, 7).Encode(), impostor, h.SelfKey.Public());
        await h.Link.HandlePacketAsync(forged, attacker, ct);

        Assert.DoesNotContain(h.Link.Paths, p => Equals(p.Remote, attacker));
        Assert.Equal(PeerPathKind.Relay, h.Link.CurrentPath.Kind);
    }

    /// <summary>Data messages reach the caller; they are what carries QUIC.</summary>
    [Fact]
    public async Task DataIsDeliveredToTheCaller()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using Harness h = NewLink();

        TaskCompletionSource<byte[]> got = new(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Link.DatagramReceived += d => got.TrySetResult(d.ToArray());

        await h.Link.HandlePacketAsync(PeerMessage.EncodeData("quic packet"u8), null, ct);

        Assert.Equal("quic packet"u8.ToArray(), await got.Task.WaitAsync(TimeSpan.FromSeconds(5), ct));
    }

    /// <summary>Anything that isn't ours is dropped without a fuss.</summary>
    [Fact]
    public async Task ForeignPacketsAreDropped()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using Harness h = NewLink();

        bool delivered = false;
        h.Link.DatagramReceived += _ => delivered = true;

        await h.Link.HandlePacketAsync(new byte[] { 1, 2, 3, 4, 5 }, null, ct);
        await h.Link.HandlePacketAsync(ReadOnlyMemory<byte>.Empty, null, ct);

        Assert.False(delivered);
    }

    /// <summary>
    /// The relay path advertises a much larger MTU than a direct path, which
    /// is what lets an oversized datagram still get through.
    /// </summary>
    [Fact]
    public async Task RelayPathCarriesMoreThanADirectPath()
    {
        await using Harness h = NewLink();

        PeerPath relay = Assert.Single(h.Link.Paths, p => p.Kind == PeerPathKind.Relay);

        Assert.True(relay.Mtu > PeerLink.MaxDirectMtu,
            $"relay mtu {relay.Mtu} should exceed the direct ceiling {PeerLink.MaxDirectMtu}");
        Assert.Equal(PeerLink.RelayMtu, relay.Mtu);
    }

    /// <summary>
    /// The path in use is kept when a rival is only marginally faster, and an
    /// oversized datagram going over the relay must not undo that.
    /// </summary>
    /// <remarks>
    /// Choosing a path used to be a side effect of asking where a datagram
    /// would fit. One packet too large for the direct path therefore cleared
    /// the incumbent, and the next small send spent the whole switch margin
    /// again — which is exactly the oscillation the margin exists to prevent.
    /// </remarks>
    [Fact]
    public async Task AnOversizedDatagramDoesNotChangeThePathInUse()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        FakeTimeProvider time = new(DateTimeOffset.UnixEpoch);
        await using Harness h = NewLink(time: time);

        using Socket first = Bound();
        using Socket second = Bound();
        IPEndPoint firstAddr = (IPEndPoint)first.LocalEndPoint!;
        IPEndPoint secondAddr = (IPEndPoint)second.LocalEndPoint!;
        h.Link.AddCandidates([firstAddr, secondAddr]);
        h.Link.Start();

        // The first path answers after 50 ms and becomes the one in use.
        ulong firstProbe = await NextProbeIdAsync(first, h, ct);
        time.Advance(TimeSpan.FromMilliseconds(50));
        await AnswerAsync(h, firstProbe, firstAddr, ct);
        Assert.Equal(firstAddr, h.Link.CurrentPath.Remote);

        // The second answers in 45 ms: faster, but not by enough to be worth
        // moving the session, so the first keeps carrying it.
        await DrainAsync(second, ct);
        ulong secondProbe = await NextProbeIdAsync(second, h, ct);
        time.Advance(TimeSpan.FromMilliseconds(45));
        await AnswerAsync(h, secondProbe, secondAddr, ct);
        Assert.Equal(firstAddr, h.Link.CurrentPath.Remote);

        // A datagram no direct path can carry goes over the relay ...
        int before = SentCount(h.Relay);
        await h.Link.SendDatagramAsync(new byte[PeerLink.MaxDirectMtu + 200], ct);
        Assert.True(SentCount(h.Relay) > before, "an oversized datagram should have gone over the relay");

        // ... and leaves the chosen path alone.
        Assert.Equal(firstAddr, h.Link.CurrentPath.Remote);
    }

    /// <summary>
    /// A candidate the peer has stopped claiming is forgotten, so a peer that
    /// keeps moving networks doesn't leave every address it ever had behind.
    /// </summary>
    [Fact]
    public async Task CandidatesThePeerNoLongerClaimsAreForgotten()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        FakeTimeProvider time = new(DateTimeOffset.UnixEpoch);
        await using Harness h = NewLink(time: time);

        IPEndPoint old = new(IPAddress.Parse("192.0.2.1"), 41641);
        h.Link.AddCandidates([old]);
        Assert.Equal(2, h.Link.Paths.Count);

        h.Link.Start();

        // The peer moved: this is the set it is reachable at now, and the
        // address it used to claim is not in it.
        h.Link.AddCandidates([new IPEndPoint(IPAddress.Parse("192.0.2.9"), 41641)]);
        time.Advance(TimeSpan.FromMinutes(5));

        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));
        while (h.Link.Paths.Any(p => Equals(p.Remote, old)))
        {
            await Task.Delay(50, cts.Token);
        }
        Assert.DoesNotContain(h.Link.Paths, p => Equals(p.Remote, old));
    }

    /// <summary>
    /// While a session is still relayed, the peer's advertised addresses keep
    /// being tried. One burst at the start is not enough: whether two NATs let
    /// a hole open depends on mappings that move, and a pair that missed its
    /// first five seconds used to stay relayed for the life of the session —
    /// which is what happened on the first test between two real NATs.
    /// </summary>
    [Fact]
    public async Task ARelayedSessionKeepsTryingToPunch()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        FakeTimeProvider time = new(DateTimeOffset.UnixEpoch);
        await using Harness h = NewLink(time: time);

        IPEndPoint candidate = new(IPAddress.Parse("192.0.2.1"), 41641);
        h.Link.AddCandidates([candidate]);
        h.Link.Start();

        // Far past both the first burst and the sweep that forgets a dead
        // path: the only way this address is still here is a later burst.
        for (int i = 0; i < 10; i++)
        {
            time.Advance(TimeSpan.FromMinutes(1));
            await Task.Delay(20, ct);
        }

        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));
        while (!h.Link.Paths.Any(p => Equals(p.Remote, candidate)))
        {
            time.Advance(TimeSpan.FromSeconds(30));
            await Task.Delay(50, cts.Token);
        }
        Assert.Contains(h.Link.Paths, p => Equals(p.Remote, candidate));
        Assert.Equal(PeerPathKind.Relay, h.Link.CurrentPath.Kind);
    }

    /// <summary>
    /// A candidate that cannot be sent to must not cost the others their
    /// turn. While punching, a dead address is the normal case — and the one
    /// listed after it may be the only reachable one.
    /// </summary>
    [Fact]
    public async Task AFailingCandidateDoesNotStopTheOthersBeingProbed()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using Harness h = NewLink();

        // Answers whatever reaches it, so being probed is observable.
        using Socket peer = Bound();
        IPEndPoint reachable = (IPEndPoint)peer.LocalEndPoint!;

        // An IPv4-mapped multicast address the socket will refuse to send to,
        // ordered before the reachable one in the probe sweep.
        h.Link.AddCandidates([new IPEndPoint(IPAddress.Parse("240.0.0.1"), 1), reachable]);
        h.Link.Start();

        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));
        byte[] buf = new byte[2048];
        while (true)
        {
            SocketReceiveFromResult got = await peer.ReceiveFromAsync(
                buf, new IPEndPoint(IPAddress.Any, 0), cts.Token);
            if (PeerMessage.IsPeerMessage(buf.AsSpan(0, got.ReceivedBytes)))
            {
                break;
            }
        }
    }

    private static Socket Bound()
    {
        Socket s = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        s.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return s;
    }

    private static int SentCount(FakeRelay relay)
    {
        lock (relay.Sent)
        {
            return relay.Sent.Count;
        }
    }

    // NextProbeIdAsync waits for the link's next probe to this socket and
    // returns its id, so the test can answer that exact probe.
    private static async Task<ulong> NextProbeIdAsync(Socket socket, Harness h, CancellationToken ct)
    {
        byte[] buffer = new byte[2048];
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));
        while (true)
        {
            SocketReceiveFromResult res = await socket.ReceiveFromAsync(
                buffer, SocketFlags.None, new IPEndPoint(IPAddress.Loopback, 0), cts.Token);
            if (PeerMessage.TryOpen(
                    buffer.AsSpan(0, res.ReceivedBytes), h.PeerKey, h.SelfKey.Public(),
                    out PeerMessageType type, out byte[]? payload) &&
                type == PeerMessageType.Ping &&
                PeerPing.TryDecode(payload, out PeerPing ping))
            {
                return ping.Id;
            }
        }
    }

    // DrainAsync throws away probes already sent, so the next one read was
    // sent at the clock's current reading and its round trip is the test's to
    // choose.
    private static async Task DrainAsync(Socket socket, CancellationToken ct)
    {
        byte[] buffer = new byte[2048];
        for (int quiet = 0; quiet < 3; quiet++)
        {
            while (socket.Available > 0)
            {
                socket.Receive(buffer);
                quiet = 0;
            }
            await Task.Delay(50, ct);
        }
    }

    private static Task AnswerAsync(Harness h, ulong probeId, IPEndPoint from, CancellationToken ct) =>
        h.Link.HandlePacketAsync(
            PeerMessage.Seal(
                PeerMessageType.Pong,
                new PeerPing(probeId, h.Link.SessionId).Encode(),
                h.PeerKey,
                h.SelfKey.Public()),
            from,
            ct);

    /// <summary>Disposing twice must be harmless: a connection and its node both do it.</summary>
    [Fact]
    public async Task DisposingTwiceIsHarmless()
    {
        Harness h = NewLink();
        h.Link.Start();

        await h.Link.DisposeAsync();
        await h.Link.DisposeAsync();
        h.Udp.Dispose();
    }
}

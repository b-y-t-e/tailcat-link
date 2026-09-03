// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Tailcat.Derp;
using Tailcat.Keys;
using Tailcat.Tailcfg;

namespace Tailcat.Net.Tests;

/// <summary>
/// Covers how a node tracks a session's lifetime: what it remembers while a
/// session is up, and what it must forget when one ends.
/// </summary>
/// <remarks>
/// These run offline, against an in-memory relay. The equivalent coverage
/// used to exist only behind <c>TAILCAT_LIVE_TESTS</c>, which means CI never
/// ran it — and this is the most concurrent code in the library: sessions get
/// replaced mid-handshake, links are disposed while packets are still
/// arriving for them, and abandoned handshakes are swept on a timer.
/// </remarks>
public class NodeSessionTests
{
    // Records what the node says it is doing, so a test can assert on the
    // reason a handshake failed rather than only on its absence.
    private sealed class RecordingObserver : ITailcatObserver
    {
        public ConcurrentQueue<(NodePublic Peer, string Reason)> Failures { get; } = new();

        public void RelayConnected(int regionId)
        {
        }

        public void RelayReconnected(int regionId, int attempt)
        {
        }

        public void HandshakeStarted(NodePublic peer, int peerRegionId)
        {
        }

        public void HandshakeCompleted(NodePublic peer, TimeSpan elapsed)
        {
        }

        public void HandshakeFailed(NodePublic peer, string reason) => Failures.Enqueue((peer, reason));

        public void PathChanged(NodePublic peer, PeerPath path)
        {
        }

        public void EndpointsDiscovered(IReadOnlyList<IPEndPoint> endpoints)
        {
        }
    }

    private const int RegionId = 900;

    // One region, dialled straight into the fake relay. STUN is switched off
    // with an empty list: a node on loopback already knows every address a
    // peer on the same machine can reach it at, and asking a STUN server that
    // isn't there would only cost the handshake timeout.
    private static TailcatNodeOptions OptionsFor(
        FakeDerpRelay relay,
        TimeProvider? time = null,
        ITailcatObserver? observer = null,
        TimeSpan? handshakeTimeout = null) => new()
        {
            DerpMap = new DerpMap
            {
                Regions =
                {
                    [RegionId] = new DerpRegion
                    {
                        RegionID = RegionId,
                        Nodes = [new DerpNode { Name = "fake", HostName = "relay.invalid" }],
                    },
                },
            },
            HomeRegionId = RegionId,
            StunServers = [],
            HandshakeTimeout = handshakeTimeout ?? TimeSpan.FromSeconds(20),
            TimeProvider = time ?? TimeProvider.System,
            Observer = observer ?? NullTailcatObserver.Instance,
            ConnectRelay = async (_, token) => await DerpClient.ConnectOverStreamAsync(
                await relay.DialAsync(token), NodePrivate.NewKey(), relay.PublicKey, token),
        };

    // The node's own key must be the one it logs into the relay with, or the
    // peer's packets are routed to a stranger.
    private static TailcatNodeOptions OptionsFor(FakeDerpRelay relay, NodePrivate key, TailcatNodeOptions basedOn) =>
        new()
        {
            PrivateKey = key,
            DerpMap = basedOn.DerpMap,
            HomeRegionId = basedOn.HomeRegionId,
            StunServers = basedOn.StunServers,
            HandshakeTimeout = basedOn.HandshakeTimeout,
            TimeProvider = basedOn.TimeProvider,
            Observer = basedOn.Observer,
            ConnectRelay = async (_, token) => await DerpClient.ConnectOverStreamAsync(
                await relay.DialAsync(token), key, relay.PublicKey, token),
        };

    private static async Task<TailcatNode> NodeAsync(
        FakeDerpRelay relay,
        CancellationToken ct,
        TimeProvider? time = null,
        ITailcatObserver? observer = null,
        TimeSpan? handshakeTimeout = null)
    {
        NodePrivate key = NodePrivate.NewKey();
        TailcatNodeOptions options = OptionsFor(relay, time, observer, handshakeTimeout);
        return await TailcatNode.CreateAsync(OptionsFor(relay, key, options), ct);
    }

    /// <summary>
    /// A peer asking for a transport this node does not have is told so, and
    /// leaves nothing behind. Dropping its hello instead would spend the
    /// dialler's whole handshake timeout and report only silence — the one
    /// failure that looks identical to a peer that is switched off.
    /// </summary>
    [Fact]
    public async Task AHelloAskingForAnUnknownTransportIsRefusedRatherThanIgnored()
    {
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromMinutes(1));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        RecordingObserver observer = new();
        await using TailcatNode listener = await NodeAsync(relay, ct, observer: observer);
        await relay.WaitForClientAsync(listener.PublicKey, ct);

        // Not a node: just a key on the relay, speaking the peer protocol by
        // hand, which is what a client built against a transport this node
        // does not have looks like from here.
        NodePrivate stranger = NodePrivate.NewKey();
        await using DerpClient client = await DerpClient.ConnectOverStreamAsync(
            await relay.DialAsync(ct), stranger, relay.PublicKey, ct);

        PeerHello hello = new(
            SessionId: 1,
            new byte[PeerHello.FingerprintLen],
            [],
            HomeRegionId: RegionId,
            Transport: (PeerTransport)200);
        await client.SendAsync(
            listener.PublicKey,
            PeerMessage.Seal(PeerMessageType.Hello, hello.Encode(), stranger, listener.PublicKey),
            ct);

        DerpReceivedPacket answer = await client.ReceiveAsync(ct);
        Assert.Equal(listener.PublicKey, answer.Source);
        Assert.True(PeerMessage.TryOpen(
            answer.Payload.Span, stranger, listener.PublicKey, out PeerMessageType type, out byte[]? payload));
        Assert.Equal(PeerMessageType.HelloAck, type);
        Assert.True(PeerHello.TryDecode(payload, out PeerHello? ack));

        // The answer names what this node does have, so the caller can say why
        // it is giving up rather than only that it did.
        Assert.Equal(PeerTransport.Quic, ack.Transport);
        Assert.Equal(0, listener.SessionCount);
        Assert.Contains(observer.Failures, f => f.Reason.Contains("200", StringComparison.Ordinal));
    }

    /// <summary>
    /// Closing a connection ends its session on both sides. The session used
    /// to survive its connection, leaving a disposed link in the maps that
    /// every receive loop kept searching for the rest of the node's life.
    /// </summary>
    [Fact]
    public async Task ClosingAConnectionEndsTheSession()
    {
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromMinutes(1));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        await using TailcatNode listener = await NodeAsync(relay, ct);
        await using TailcatNode dialer = await NodeAsync(relay, ct);

        Task<TailcatConnection> accepted = listener.AcceptConnectionAsync(ct);
        TailcatConnection client = await dialer.ConnectAsync(listener.Address, ct);
        TailcatConnection server = await accepted;

        Assert.Equal(1, dialer.SessionCount);
        Assert.Equal(1, listener.SessionCount);

        await client.DisposeAsync();
        await server.DisposeAsync();

        Assert.Equal(0, dialer.SessionCount);
        Assert.Equal(0, listener.SessionCount);
        Assert.Equal(0, dialer.RoutedEndpointCount);
        Assert.Equal(0, listener.RoutedEndpointCount);
    }

    /// <summary>
    /// A peer that connects, leaves, and comes back gets a working session
    /// again — including over the same NAT mapping, which is the ordinary
    /// case and the one a stale routing entry used to break.
    /// </summary>
    [Fact]
    public async Task APeerThatReconnectsGetsAWorkingSession()
    {
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromMinutes(2));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        await using TailcatNode listener = await NodeAsync(relay, ct);
        await using TailcatNode dialer = await NodeAsync(relay, ct);

        for (int attempt = 1; attempt <= 2; attempt++)
        {
            Task<TailcatConnection> accepted = listener.AcceptConnectionAsync(ct);
            await using TailcatConnection client = await dialer.ConnectAsync(listener.Address, ct);
            await using TailcatConnection server = await accepted;

            // The dialer speaks first: QUIC opens a stream lazily, so a
            // listener that waited here would wait forever.
            await using Stream outbound = await client.OpenStreamAsync(ct);
            await outbound.WriteAsync(Encoding.UTF8.GetBytes($"attempt {attempt}"), ct);
            await outbound.FlushAsync(ct);

            await using Stream inbound = await server.AcceptStreamAsync(ct);
            byte[] buffer = new byte[64];
            int read = await inbound.ReadAsync(buffer, ct);

            Assert.Equal($"attempt {attempt}", Encoding.UTF8.GetString(buffer, 0, read));
        }
    }

    /// <summary>
    /// A peer that sends a Hello and then vanishes is swept away, session and
    /// all, without any further traffic to trigger it.
    /// </summary>
    /// <remarks>
    /// The sweep used to run only just before a blocking accept, so a socket
    /// and a probing link stayed alive until some unrelated peer happened to
    /// connect. Leaving the session behind was worse than a leak: the same
    /// peer retrying its session id was answered with a bare ack and waited
    /// forever for a bridge that no longer existed.
    /// </remarks>
    [Fact]
    public async Task AnAbandonedHandshakeIsSweptAway()
    {
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromMinutes(1));
        CancellationToken ct = cts.Token;

        FakeTimeProvider time = new(DateTimeOffset.UnixEpoch);
        RecordingObserver observer = new();
        await using FakeDerpRelay relay = new();
        await using TailcatNode listener = await NodeAsync(
            relay, ct, time, observer, handshakeTimeout: TimeSpan.FromSeconds(5));

        // A peer that says hello over the relay and then does nothing: no QUIC
        // connection ever arrives for it.
        NodePrivate ghostKey = NodePrivate.NewKey();
        await using DerpClient ghost = await DerpClient.ConnectOverStreamAsync(
            await relay.DialAsync(ct), ghostKey, relay.PublicKey, ct);
        await relay.WaitForClientAsync(listener.PublicKey, ct);

        PeerHello hello = new(SessionId: 12345, CertificateFingerprint: new byte[32], Endpoints: [], HomeRegionId: RegionId);
        await ghost.SendAsync(
            listener.PublicKey,
            PeerMessage.Seal(PeerMessageType.Hello, hello.Encode(), ghostKey, listener.PublicKey),
            ct);

        await WaitUntilAsync(() => listener.SessionCount == 1, "the Hello should have opened a session", ct);

        // The session goes in before the pending accept whose start time is
        // the handshake deadline, so advancing the clock in between would
        // stamp that deadline from the advanced clock and never expire.
        await WaitUntilAsync(
            () => listener.PendingAcceptCount == 1, "the Hello should have opened a pending accept", ct);

        // Nothing else happens; only the clock moves past the timeout.
        time.Advance(TimeSpan.FromSeconds(30));

        await WaitUntilAsync(() => listener.SessionCount == 0, "the abandoned handshake should have been swept", ct);
        Assert.Contains(
            observer.Failures,
            f => f.Peer == ghostKey.Public() && f.Reason.Contains("never completed", StringComparison.Ordinal));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string because, CancellationToken ct)
    {
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        while (!condition())
        {
            try
            {
                await Task.Delay(50, cts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                Assert.Fail(because);
            }
        }
    }
}

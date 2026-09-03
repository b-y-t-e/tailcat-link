// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Collections.Concurrent;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;
using Tailcat.Derp;
using Tailcat.Keys;
using Tailcat.Tailcfg;

namespace Tailcat.Net;

/// <summary>How to bring up a <see cref="TailcatNode"/>.</summary>
public sealed class TailcatNodeOptions
{
    /// <summary>An existing node key to reuse. A fresh one is generated if null.</summary>
    public NodePrivate? PrivateKey { get; init; }

    /// <summary>
    /// The relay to connect to. If null, the DERP map is fetched and a region
    /// is chosen from it.
    /// </summary>
    /// <remarks>
    /// A node given only a single relay knows of only one region, so it cannot
    /// reach a peer listening elsewhere. To pin the home region while keeping
    /// the ability to reach other regions, set <see cref="DerpMap"/> and
    /// <see cref="HomeRegionId"/> instead.
    /// </remarks>
    public DerpNode? Relay { get; init; }

    /// <summary>
    /// The DERP map to use instead of fetching one. It names every region the
    /// node can reach a peer in, not only its own.
    /// </summary>
    public DerpMap? DerpMap { get; init; }

    /// <summary>
    /// The region to listen in. If null, the closest is measured and chosen.
    /// </summary>
    public int? HomeRegionId { get; init; }

    /// <summary>Where to fetch the DERP map from, when <see cref="Relay"/> is null.</summary>
    public ExpandOptions DerpMapOptions { get; init; } = new();

    /// <summary>
    /// STUN servers used to learn this node's public address, which peers need
    /// in order to punch a hole to it. Defaults to the STUN servers in the
    /// DERP map.
    /// </summary>
    public IReadOnlyList<IPEndPoint>? StunServers { get; init; }

    /// <summary>
    /// Servers to ask when the ones above answer nothing, resolved by name at
    /// the moment they are needed. Empty disables the fallback, and so does
    /// setting <see cref="StunServers"/>: naming the servers yourself means
    /// all of them, or the fallback would quietly reach past a node that was
    /// configured to talk to one network only.
    /// </summary>
    /// <remarks>
    /// The DERP map is the natural place to look for a STUN server and the
    /// only one Go consults, but it is not a guarantee: as of writing, none
    /// of the four relays in tailcat's own map answer on 3478. A node that
    /// never learns its public address advertises only its LAN addresses, so
    /// no peer on another network has anything to aim at and every session
    /// stays on the relay for good. That failure is silent — the session
    /// works, it is merely slow — which is why the fallback is on by default
    /// rather than something to be discovered and switched on.
    /// </remarks>
    public IReadOnlyList<string> StunFallbackHosts { get; init; } =
        ["stun.cloudflare.com:3478", "stun.l.google.com:19302"];

    /// <summary>How long to wait for a peer to answer a session handshake.</summary>
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// The clock the node measures with. Defaults to the system clock; tests
    /// pass their own so endpoint freshness needs no waiting.
    /// </summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>
    /// Where the node reports what it is doing. Defaults to reporting
    /// nothing; pass a <see cref="TextTailcatObserver"/> to see the steps.
    /// </summary>
    public ITailcatObserver Observer { get; init; } = NullTailcatObserver.Instance;

    /// <summary>
    /// How the relay region is chosen when <see cref="Relay"/> is null.
    /// Defaults to measuring latency to each region with STUN and taking the
    /// closest, since every relayed byte crosses the relay twice.
    /// </summary>
    public IRegionPicker RegionPicker { get; init; } = new StunRegionPicker();

    /// <summary>
    /// How a relay region is dialled. Null dials the region named in the DERP
    /// map, which is what every caller outside a test wants.
    /// </summary>
    /// <remarks>
    /// The session layer is the most concurrent part of this library and the
    /// hardest to reason about: sessions are replaced mid-handshake, links are
    /// disposed while packets are still arriving for them, and abandoned
    /// handshakes are swept on a timer. None of that could be tested without
    /// standing a node up against an in-memory relay, and the alternative —
    /// covering it only against the public relays, behind
    /// <c>TAILCAT_LIVE_TESTS</c> — means CI never runs it at all.
    /// </remarks>
    internal Func<int, CancellationToken, Task<DerpClient>>? ConnectRelay { get; init; }
}

/// <summary>
/// A node that can reach, and be reached by, other tailcat nodes across
/// networks that cannot see each other.
/// </summary>
/// <remarks>
/// <para>
/// Each node is addressed by its public key. Connecting needs no inbound port
/// and no account: both sides meet at a DERP relay, authenticate each other
/// with sealed messages, then try to punch a direct UDP path and move onto it
/// if it works.
/// </para>
/// <para>
/// Traffic runs over QUIC, which supplies the encryption (TLS 1.3),
/// reliability, and stream multiplexing. The relay only ever sees QUIC
/// packets it cannot read.
/// </para>
/// </remarks>
public sealed class TailcatNode : IAsyncDisposable
{
    /// <summary>The ALPN protocol tailcat sessions negotiate.</summary>
    public const string AlpnProtocol = "tailcat/1";

    // How long a discovered set of endpoints is reused before asking again.
    private static readonly TimeSpan EndpointCacheLifetime = TimeSpan.FromSeconds(30);

    // How often abandoned half-finished accepts are swept. The accept loop
    // cannot do it: it blocks in AcceptConnectionAsync, so a peer that sends a
    // Hello and then vanishes would keep a socket and a probing link alive
    // until some unrelated peer happened to connect.
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(1);

    // How often to re-ask STUN while a session is up, so a NAT that moved our
    // mapping is noticed. A local address change raises an event; a mapping
    // being reassigned behind the same local address raises nothing, and the
    // peer goes on probing a port that no longer exists — measured between two
    // real NATs, where the advertised port changed from 65264 to 65204 and the
    // peer was never told. Shorter than the mapping lifetimes NATs commonly
    // use, so the announcement arrives while the old port is still the one the
    // peer holds.
    private static readonly TimeSpan EndpointRecheckInterval = TimeSpan.FromSeconds(20);

    // How long to wait for one STUN server, and how many to try. A node only
    // needs one public address to be punchable.
    private static readonly TimeSpan StunTimeout = TimeSpan.FromSeconds(2);
    private const int MaxStunServersToAsk = 2;

    private readonly NodeIdentity _identity;
    private readonly DerpRegionPool _relays;
    private readonly DerpMap _derpMap;
    private readonly Socket _udp;
    private readonly QuicListener? _listener;
    private readonly IReadOnlyList<PeerTransport> _transports;
    private readonly IReadOnlyList<IPEndPoint> _stunServers;
    private readonly IReadOnlyList<string> _stunFallbackHosts;
    private IReadOnlyList<IPEndPoint>? _resolvedFallback;
    private readonly TimeSpan _handshakeTimeout;
    private readonly ITailcatObserver _observer;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    private readonly ConcurrentDictionary<NodePublic, Session> _sessions = new();
    private readonly ConcurrentDictionary<IPEndPoint, PeerLink> _linksByEndpoint = new();
    private readonly ConcurrentDictionary<IPEndPoint, PendingAccept> _acceptsByBridge = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<IPEndPoint>> _stunWaiters = new();
    private readonly Lock _endpointMu = new();
    private IReadOnlyList<IPEndPoint>? _cachedEndpoints;
    private DateTimeOffset _endpointsDiscoveredAt;
    // Bounded: a caller that never accepts must not be able to grow this
    // without limit. Refusing the newest is the honest failure - the peer
    // sees no session rather than one that is never served.
    private readonly Channel<TailcatConnection> _incoming =
        Channel.CreateBounded<TailcatConnection>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
        });

    private readonly Task _derpLoop;
    private readonly Task _udpLoop;
    private readonly Task _acceptLoop;
    private readonly Task _sweepLoop;
    private readonly Task _endpointLoop;
    private IReadOnlyList<IPEndPoint>? _announcedEndpoints;

    private TailcatNode(
        NodeIdentity identity,
        DerpRegionPool relays,
        DerpMap derpMap,
        Socket udp,
        QuicListener? listener,
        IReadOnlyList<PeerTransport> transports,
        IReadOnlyList<IPEndPoint> stunServers,
        IReadOnlyList<string> stunFallbackHosts,
        TimeSpan handshakeTimeout,
        ITailcatObserver observer,
        TimeProvider timeProvider)
    {
        _identity = identity;
        _relays = relays;
        _derpMap = derpMap;
        _udp = udp;
        _listener = listener;
        _transports = transports;
        _stunServers = stunServers;
        _stunFallbackHosts = stunFallbackHosts;
        _handshakeTimeout = handshakeTimeout;
        _observer = observer;
        _time = timeProvider;

        _derpLoop = Task.Run(() => DerpReceiveLoopAsync(_cts.Token));
        _udpLoop = Task.Run(() => UdpReceiveLoopAsync(_cts.Token));
        _acceptLoop = listener is null ? Task.CompletedTask : Task.Run(() => QuicAcceptLoopAsync(_cts.Token));
        _sweepLoop = Task.Run(() => SweepLoopAsync(_cts.Token));
        _endpointLoop = Task.Run(() => EndpointWatchLoopAsync(_cts.Token));

        // Moving between networks changes every address a peer knows us by,
        // so the node re-discovers them and says so rather than going quiet.
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        _relays.RegionReconnected += OnRegionReconnected;
    }

    /// <summary>This node's public key: the address peers connect to.</summary>
    public NodePublic PublicKey => _identity.PublicKey;

    /// <summary>
    /// This node's disco public key, derived from its node key. It is the
    /// part of the identity peers may see on a direct path.
    /// </summary>
    public DiscoPublic DiscoPublicKey => _identity.DiscoPublicKey;

    /// <summary>The relay region this node listens in.</summary>
    public int HomeRegionId => _relays.HomeRegionId;

    /// <summary>The relay regions this node currently holds a connection to.</summary>
    public IReadOnlyCollection<int> ConnectedRegions => _relays.ConnectedRegions;

    // The two maps whose entries used to outlive what they point at. They are
    // the subject of several tests and have no meaning to a caller, so they
    // are visible to the test assembly rather than published.
    internal int SessionCount => _sessions.Count;

    internal int RoutedEndpointCount => _linksByEndpoint.Count;

    // A Hello registers its session before the pending accept that carries the
    // handshake deadline, so a test that advances the clock between the two
    // stamps the deadline from the already-advanced clock and waits forever.
    internal int PendingAcceptCount => _acceptsByBridge.Count;

    /// <summary>
    /// This node's address: its public key together with the region it
    /// listens in, in the same compact form the Go implementation uses.
    /// </summary>
    /// <remarks>
    /// The region is part of the address because a peer must send to the
    /// region this node is listening in. A bare public key is only enough
    /// when both nodes happen to have chosen the same region.
    /// </remarks>
    public ConnBlob Address => new ConnInfo
    {
        ServerPublic = PublicKey,
        ServerDiscoPublic = DiscoPublicKey,
        RegionID = HomeRegionId,
    }.ToConnBlob();

    /// <summary>Brings up a node and connects it to a relay.</summary>
    /// <exception cref="TailcatException">If no relay can be reached.</exception>
    /// <exception cref="PlatformNotSupportedException">If the platform has no QUIC support.</exception>
    public static async Task<TailcatNode> CreateAsync(
        TailcatNodeOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new TailcatNodeOptions();

        // QUIC is not everywhere: Windows 10 has none at all, and Linux needs
        // libmsquic from the distribution. A node without it is not broken,
        // it simply has one transport fewer to offer — so what it can speak
        // is worked out here rather than being assumed, and only a node left
        // with nothing at all is refused.
        List<PeerTransport> transports = [];
        if (QuicListener.IsSupported)
        {
            transports.Add(PeerTransport.Quic);
        }
        if (transports.Count == 0)
        {
            throw new PlatformNotSupportedException(
                "this platform has no QUIC (Windows 10 has none; Linux needs libmsquic installed), " +
                "and no other transport is implemented yet — see docs/relay1.md");
        }

        NodeIdentity identity = NodeIdentity.Create(options.PrivateKey);

        // Nothing built here has an owner until the node is constructed, so
        // every step past the first has to undo the ones before it. A relay
        // connection or a bound socket dropped on the floor keeps its
        // background loops running with nobody left holding a reference.
        DerpRegionPool? relays = null;
        Socket? udp = null;
        QuicListener? listener = null;
        TaskCompletionSource<TailcatNode> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            (DerpMap map, int homeRegionId, IReadOnlyList<IPEndPoint> stun, IReadOnlyList<string> stunFallback) =
                await ResolveHomeRegionAsync(options, cancellationToken).ConfigureAwait(false);

            // A pool, not one connection: this node listens in its home region,
            // but must be able to send into whichever region a peer listens in.
            relays = await DerpRegionPool
                .CreateAsync(
                    map, identity.PrivateKey, homeRegionId,
                    timeProvider: options.TimeProvider,
                    connect: options.ConnectRelay,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            // One UDP socket carries STUN and every direct path, because a NAT
            // maps per source port: an address learned on another socket would
            // be useless to a peer. It is dual-stack so IPv6 peers are
            // reachable too.
            udp = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp)
            {
                DualMode = true,
            };
            DisableConnectionReset(udp);
            udp.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));

            // The listener accepts from the moment ListenAsync returns, which
            // is before the node it must ask for options exists. Waiting on the
            // promise makes that window a short delay rather than a
            // NullReferenceException raised inside MsQuic.
            listener = await QuicListener.ListenAsync(
                new QuicListenerOptions
                {
                    ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
                    ApplicationProtocols = [new SslApplicationProtocol(AlpnProtocol)],
                    ConnectionOptionsCallback = async (conn, _, ct) =>
                        (await ready.Task.WaitAsync(ct).ConfigureAwait(false)).ServerOptionsFor(conn),
                },
                cancellationToken).ConfigureAwait(false);

            TailcatNode node = new(
                identity, relays, map, udp, listener, transports, stun, stunFallback,
                options.HandshakeTimeout, options.Observer, options.TimeProvider);
            ready.SetResult(node);
            options.Observer.RelayConnected(homeRegionId);
            return node;
        }
        catch (Exception ex)
        {
            // A connection caught in the window above gets the real reason
            // rather than hanging until the listener is torn down.
            ready.TrySetException(ex);
            if (listener is not null)
            {
                await listener.DisposeAsync().ConfigureAwait(false);
            }
            udp?.Dispose();
            if (relays is not null)
            {
                await relays.DisposeAsync().ConfigureAwait(false);
            }
            identity.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Opens a session to the node at <paramref name="address"/>, meeting it
    /// in its home relay region and then trying for a direct path.
    /// </summary>
    /// <param name="address">
    /// The peer address: its public key and the region it listens in, as
    /// printed by <see cref="Address"/>.
    /// </param>
    /// <param name="cancellationToken">Cancels the attempt.</param>
    /// <exception cref="TailcatException">If the peer does not answer in time.</exception>
    public async Task<TailcatConnection> ConnectAsync(ConnBlob address, CancellationToken cancellationToken = default)
    {
        ConnInfo info = address.Parse();
        int peerRegion = info.RegionID != 0
            ? info.RegionID
            : info.Region.FirstOrDefault()?.RegionID ?? HomeRegionId;
        return await ConnectAsync(info.ServerPublic, peerRegion, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Opens a session to <paramref name="peer"/>, assuming it listens in this
    /// node own region.
    /// </summary>
    /// <remarks>
    /// This only works when both nodes chose the same region, which two nodes
    /// far apart will not. Prefer <see cref="ConnectAsync(ConnBlob, CancellationToken)"/>,
    /// whose address says where the peer is listening.
    /// </remarks>
    public Task<TailcatConnection> ConnectAsync(NodePublic peer, CancellationToken cancellationToken = default) =>
        ConnectAsync(peer, HomeRegionId, cancellationToken);

    /// <summary>
    /// Opens a session to <paramref name="peer"/> in relay region
    /// <paramref name="peerRegionId"/>.
    /// </summary>
    public async Task<TailcatConnection> ConnectAsync(
        NodePublic peer,
        int peerRegionId,
        CancellationToken cancellationToken = default)
    {
        // Send into the region the peer listens in, not ours.
        DerpConnection relay = await _relays.ForRegionAsync(peerRegionId, cancellationToken).ConfigureAwait(false);

        ulong sessionId = BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8));
        PeerLink link = new(_identity.PrivateKey, peer, sessionId, relay, _udp);
        Session session = new(link) { SessionId = sessionId, RegionId = peerRegionId };
        await ReplaceSessionAsync(peer, session).ConfigureAwait(false);
        link.PathChanged += path => OnPathChanged(peer, path);
        link.DirectProbeSent += to => _observer.DirectProbeSent(peer, to);
        link.Start();

        _observer.HandshakeStarted(peer, peerRegionId);
        TailcatMetrics.SessionsStarted.Add(1);
        long startedAt = _time.GetTimestamp();

        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_handshakeTimeout);

        // Tell the peer our certificate fingerprint, where it might reach us,
        // and which region to answer in; then wait for the same in return.
        PeerHello hello = new(
            sessionId,
            _identity.Fingerprint,
            await LocalEndpointsAsync(cts.Token).ConfigureAwait(false),
            HomeRegionId,
            _transports);
        byte[] msg = PeerMessage.Seal(PeerMessageType.Hello, hello.Encode(), _identity.PrivateKey, peer);

        PeerHello ack;
        try
        {
            ack = await SendUntilAnsweredAsync(relay, peer, msg, session.HelloAck, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await CloseSessionAsync(peer).ConfigureAwait(false);
            string reason = $"no answer within {_handshakeTimeout}";
            _observer.HandshakeFailed(peer, reason);
            TailcatMetrics.SessionsFailed.Add(1);
            throw new TailcatException($"peer {peer} in region {peerRegionId} did not answer: {reason}");
        }

        // The answer names the one transport the peer chose out of what was
        // offered. Anything else means it shares none of them, and going on
        // regardless would leave this end sending QUIC packets to somebody
        // who is not listening for them — silence, rather than an error
        // anyone could act on.
        if (ack.Transports.Count != 1 || !_transports.Contains(ack.Transports[0]))
        {
            await CloseSessionAsync(peer).ConfigureAwait(false);
            string reason =
                $"no transport in common: this node speaks [{Describe(_transports)}], " +
                $"the peer [{Describe(ack.Transports)}]";
            _observer.HandshakeFailed(peer, reason);
            TailcatMetrics.SessionsFailed.Add(1);
            throw new TailcatException($"peer {peer} in region {peerRegionId} refused the session: {reason}");
        }

        link.AddCandidates(ack.Endpoints);

        UdpBridge bridge = new(link);
        bridge.Start();

        QuicConnection quic;
        try
        {
            quic = await QuicConnection.ConnectAsync(
                new QuicClientConnectionOptions
                {
                    RemoteEndPoint = bridge.LocalEndPoint,
                    DefaultStreamErrorCode = 0,
                    DefaultCloseErrorCode = 0,
                    MaxInboundBidirectionalStreams = 64,
                    ClientAuthenticationOptions = new SslClientAuthenticationOptions
                    {
                        ApplicationProtocols = [new SslApplicationProtocol(AlpnProtocol)],
                        TargetHost = "tailcat",
                        ClientCertificates = [_identity.Certificate],
                        // The peer named its exact certificate inside a sealed
                        // message, so pinning it is stronger than any CA check.
                        RemoteCertificateValidationCallback = (_, cert, _, _) =>
                            NodeIdentity.MatchesFingerprint(cert as X509Certificate2, ack.CertificateFingerprint),
                    },
                },
                cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Everything built for this attempt is ours to release; leaving it
            // would keep a socket and a probe loop alive for a session that
            // never existed.
            await bridge.DisposeAsync().ConfigureAwait(false);
            await CloseSessionAsync(peer).ConfigureAwait(false);
            _observer.HandshakeFailed(peer, ex.Message);
            TailcatMetrics.SessionsFailed.Add(1);
            throw;
        }

        TailcatConnection connection = new(
            quic, link, bridge, peer, c => OnConnectionClosedAsync(peer, link, c));
        session.Connection = connection;

        _observer.HandshakeCompleted(peer, _time.GetElapsedTime(startedAt));
        TailcatMetrics.SessionsEstablished.Add(1);
        return connection;
    }

    private void OnRegionReconnected(int regionId, int attempt)
    {
        _observer.RelayReconnected(regionId, attempt);
        TailcatMetrics.RelayReconnects.Add(1);
    }

    private void OnPathChanged(NodePublic peer, PeerPath path)
    {
        _observer.PathChanged(peer, path);
        TailcatMetrics.PathSwitches.Add(1);
    }

    /// <summary>
    /// Yields sessions other nodes open to this one, until the node is
    /// disposed or <paramref name="cancellationToken"/> fires.
    /// </summary>
    public IAsyncEnumerable<TailcatConnection> AcceptConnectionsAsync(CancellationToken cancellationToken = default) =>
        _incoming.Reader.ReadAllAsync(cancellationToken);

    /// <summary>Accepts the next session another node opens to this one.</summary>
    public async Task<TailcatConnection> AcceptConnectionAsync(CancellationToken cancellationToken = default) =>
        await _incoming.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Re-discovers this node addresses and announces them to every live
    /// session, for use after the network changed.
    /// </summary>
    /// <returns>The addresses now being advertised.</returns>
    public async Task<IReadOnlyList<IPEndPoint>> RefreshEndpointsAsync(CancellationToken cancellationToken = default)
    {
        lock (_endpointMu)
        {
            _cachedEndpoints = null;
        }
        IReadOnlyList<IPEndPoint> endpoints = await LocalEndpointsAsync(cancellationToken).ConfigureAwait(false);
        await AnnounceEndpointsAsync(endpoints, cancellationToken).ConfigureAwait(false);
        return endpoints;
    }

    /// <summary>
    /// Tells every live session which addresses to try, and remembers what was
    /// said so a later change can be recognised as one.
    /// </summary>
    private async Task AnnounceEndpointsAsync(
        IReadOnlyList<IPEndPoint> endpoints,
        CancellationToken cancellationToken)
    {
        foreach (Session session in _sessions.Values)
        {
            PeerHello update = new(session.SessionId, _identity.Fingerprint, endpoints, HomeRegionId);
            byte[] msg = PeerMessage.Seal(
                PeerMessageType.EndpointUpdate, update.Encode(), _identity.PrivateKey, session.Link.Peer);
            try
            {
                DerpConnection relay = await _relays.ForRegionAsync(session.RegionId, cancellationToken).ConfigureAwait(false);
                await relay.SendAsync(session.Link.Peer, msg, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TailcatException or IOException or ObjectDisposedException)
            {
                // One unreachable peer must not stop the others being told.
            }
        }

        lock (_endpointMu)
        {
            _announcedEndpoints = endpoints;
        }
    }

    /// <summary>
    /// Re-asks STUN while sessions are up, and announces the answer if it is
    /// not what the peers were told.
    /// </summary>
    /// <remarks>
    /// Only a change is announced, and only while there is somebody to
    /// announce it to: on an idle node this loop is one STUN round trip every
    /// <see cref="EndpointRecheckInterval"/> and nothing else.
    /// </remarks>
    private async Task EndpointWatchLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(EndpointRecheckInterval, _time, ct).ConfigureAwait(false);
                if (_sessions.IsEmpty)
                {
                    continue;
                }

                IReadOnlyList<IPEndPoint>? announced;
                lock (_endpointMu)
                {
                    // What the peers hold: whatever was last announced, or —
                    // before any announcement — the set the handshake sent.
                    announced = _announcedEndpoints ?? _cachedEndpoints;
                    _cachedEndpoints = null;
                }

                IReadOnlyList<IPEndPoint> found;
                try
                {
                    found = await LocalEndpointsAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is SocketException or ObjectDisposedException or TailcatException)
                {
                    continue;
                }

                if (announced is not null && found.Count == announced.Count && !found.Except(announced).Any())
                {
                    continue;
                }

                await AnnounceEndpointsAsync(found, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e) =>
        _ = Task.Run(async () =>
        {
            try
            {
                // Addresses settle a moment after the event fires.
                await Task.Delay(TimeSpan.FromSeconds(1), _cts.Token).ConfigureAwait(false);
                await RefreshEndpointsAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or TailcatException)
            {
            }
        });

    /// <summary>
    /// The addresses a peer could try to reach this node at: every local
    /// address on the UDP socket's port, plus whatever STUN reports.
    /// </summary>
    public async Task<IReadOnlyList<IPEndPoint>> LocalEndpointsAsync(CancellationToken cancellationToken = default)
    {
        // Discovery costs a network round trip, and every handshake asks for
        // it, so the answer is cached: a NAT mapping doesn't move that often.
        lock (_endpointMu)
        {
            if (_cachedEndpoints is not null &&
                _time.GetUtcNow() - _endpointsDiscoveredAt < EndpointCacheLifetime)
            {
                return _cachedEndpoints;
            }
        }

        int port = ((IPEndPoint)_udp.LocalEndPoint!).Port;
        List<IPEndPoint> endpoints = [];
        foreach (IPAddress addr in LocalAddresses())
        {
            endpoints.Add(new IPEndPoint(addr, port));
        }

        IPEndPoint? mapped = await DiscoverPublicEndpointAsync(cancellationToken).ConfigureAwait(false);
        if (mapped is not null && !endpoints.Contains(mapped))
        {
            endpoints.Add(mapped);
        }

        lock (_endpointMu)
        {
            _cachedEndpoints = endpoints;
            _endpointsDiscoveredAt = _time.GetUtcNow();
        }
        _observer.EndpointsDiscovered(endpoints);
        return endpoints;
    }

    /// <summary>
    /// Asks the STUN servers what public address this node's UDP socket maps
    /// to, returning the first answer.
    /// </summary>
    /// <remarks>
    /// The request goes out on the shared socket and the reply comes back
    /// through the node's own receive loop: only one reader may own that
    /// socket, or STUN answers and peer traffic would steal each other's
    /// packets.
    /// </remarks>
    private async Task<IPEndPoint?> DiscoverPublicEndpointAsync(CancellationToken cancellationToken)
    {
        IPEndPoint? mapped = await AskAsync(_stunServers.Take(MaxStunServersToAsk), cancellationToken)
            .ConfigureAwait(false);
        return mapped ?? await AskAsync(
            await ResolveFallbackAsync(cancellationToken).ConfigureAwait(false), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Asks every server at once and takes the first answer.
    /// </summary>
    /// <remarks>
    /// One at a time would be tidier, but it spends the timeout on each
    /// server that has gone quiet before reaching one that has not — and the
    /// servers most likely to be first in the list are the DERP map's, which
    /// are exactly the ones that may not run STUN at all. Asking together
    /// bounds the whole discovery by one timeout however many are dead.
    /// </remarks>
    private async Task<IPEndPoint?> AskAsync(
        IEnumerable<IPEndPoint> servers,
        CancellationToken cancellationToken)
    {
        List<string> keys = [];
        List<Task<IPEndPoint>> answers = [];
        try
        {
            foreach (IPEndPoint server in servers)
            {
                byte[] request = Stun.BuildBindingRequest(out byte[] transactionId);
                string key = Convert.ToHexString(transactionId);
                TaskCompletionSource<IPEndPoint> answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _stunWaiters[key] = answer;
                keys.Add(key);
                answers.Add(answer.Task);
                try
                {
                    await _udp.SendToAsync(request, SocketFlags.None, server, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
                {
                    // Unreachable from here; the others may still answer.
                }
            }

            if (answers.Count == 0)
            {
                return null;
            }

            Task delay = Task.Delay(StunTimeout, _time, cancellationToken);
            Task finished = await Task.WhenAny([delay, .. answers]).ConfigureAwait(false);
            return finished == delay ? null : await ((Task<IPEndPoint>)finished).ConfigureAwait(false);
        }
        finally
        {
            foreach (string key in keys)
            {
                _stunWaiters.TryRemove(key, out _);
            }
        }
    }

    /// <summary>
    /// Resolves the fallback servers, once per node: a name lookup is a
    /// network round trip of its own, and these are asked on every cache miss.
    /// </summary>
    private async Task<IReadOnlyList<IPEndPoint>> ResolveFallbackAsync(CancellationToken cancellationToken)
    {
        if (_resolvedFallback is not null)
        {
            return _resolvedFallback;
        }

        List<IPEndPoint> resolved = [];
        foreach (string host in _stunFallbackHosts)
        {
            int colon = host.LastIndexOf(':');
            string name = colon < 0 ? host : host[..colon];
            if (colon < 0 || !int.TryParse(host[(colon + 1)..], out int port))
            {
                port = Stun.DefaultPort;
            }

            try
            {
                IPAddress[] addresses = await Dns.GetHostAddressesAsync(name, cancellationToken).ConfigureAwait(false);
                if (Array.Find(addresses, a => a.AddressFamily == AddressFamily.InterNetwork) is IPAddress ip)
                {
                    resolved.Add(new IPEndPoint(ip, port));
                }
            }
            catch (Exception ex) when (ex is SocketException or ArgumentException)
            {
                // No DNS, or a name that no longer exists. The node still
                // works; it just stays on the relay.
            }
        }

        _resolvedFallback = resolved;
        return resolved;
    }

    /// <summary>
    /// Stops a bounced datagram from breaking the next receive, on the one
    /// platform where it does.
    /// </summary>
    /// <remarks>
    /// Windows turns the ICMP "port unreachable" that comes back from a
    /// datagram nobody was listening for into a connection reset, raised on
    /// the socket's *next* receive rather than on the send that caused it.
    /// For a socket whose whole job is probing addresses that may be dead
    /// that is fatal: every probe to a candidate that has gone away aborts
    /// the receive a peer's answer was about to arrive on, so the two ends
    /// probe each other indefinitely and neither ever hears anything. It cost
    /// a session between two NATs that were both perfectly punchable.
    /// </remarks>
    private static void DisableConnectionReset(Socket socket)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // SIO_UDP_CONNRESET. There is no named constant for it in .NET.
        const int SioUdpConnReset = -1744830452;
        try
        {
            socket.IOControl(SioUdpConnReset, [0, 0, 0, 0], null);
        }
        catch (SocketException)
        {
            // Older or unusual stacks may not know the option. Probing still
            // works; it is just interruptible again.
        }
    }

    private static IEnumerable<IPAddress> LocalAddresses()
    {
        foreach (System.Net.NetworkInformation.NetworkInterface nic in
                 System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up ||
                nic.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
            {
                continue;
            }
            foreach (System.Net.NetworkInformation.UnicastIPAddressInformation ua in
                     nic.GetIPProperties().UnicastAddresses)
            {
                IPAddress addr = ua.Address;
                if (addr.AddressFamily == AddressFamily.InterNetwork)
                {
                    yield return addr;
                }
                else if (addr.AddressFamily == AddressFamily.InterNetworkV6 &&
                         !addr.IsIPv6LinkLocal && !addr.IsIPv6SiteLocal && !addr.IsIPv6Teredo)
                {
                    // A routable IPv6 address is often the easiest direct
                    // path there is: no NAT to punch through at all.
                    yield return addr;
                }
            }
        }
    }

    private QuicServerConnectionOptions ServerOptionsFor(QuicConnection connection)
    {
        // The bridge address identifies which peer this connection belongs to,
        // and therefore which fingerprint to demand.
        byte[]? expected = null;
        if (connection.RemoteEndPoint is IPEndPoint ep &&
            _acceptsByBridge.TryGetValue(ep, out PendingAccept pending))
        {
            expected = pending.Fingerprint;
        }

        return new QuicServerConnectionOptions
        {
            DefaultStreamErrorCode = 0,
            DefaultCloseErrorCode = 0,
            MaxInboundBidirectionalStreams = 64,
            ServerAuthenticationOptions = new SslServerAuthenticationOptions
            {
                ApplicationProtocols = [new SslApplicationProtocol(AlpnProtocol)],
                ServerCertificate = _identity.Certificate,
                ClientCertificateRequired = true,
                RemoteCertificateValidationCallback = (_, cert, _, _) =>
                    expected is not null && NodeIdentity.MatchesFingerprint(cert as X509Certificate2, expected),
            },
        };
    }

    private async Task DerpReceiveLoopAsync(CancellationToken ct)
    {
        try
        {
            // The channel outlives any single relay connection: reconnection
            // happens underneath, and packets keep arriving here.
            await foreach (DerpReceivedPacket packet in _relays.Packets.ReadAllAsync(ct).ConfigureAwait(false))
            {
                await HandlePacketAsync(packet.Source, packet.Payload, from: null, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task UdpReceiveLoopAsync(CancellationToken ct)
    {
        byte[] buffer = new byte[64 * 1024];
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                SocketReceiveFromResult res = await _udp
                    .ReceiveFromAsync(buffer, SocketFlags.None, any, ct).ConfigureAwait(false);
                IPEndPoint from = PeerLink.Normalize((IPEndPoint)res.RemoteEndPoint);
                ReadOnlyMemory<byte> packet = buffer.AsMemory(0, res.ReceivedBytes);
                _observer.DatagramArrived(
                    from,
                    res.ReceivedBytes,
                    Stun.IsStunPacket(packet.Span) ? "stun"
                        : PeerMessage.IsPeerMessage(packet.Span) ? PeerMessage.TypeOf(packet.Span).ToString()
                        : "unknown");

                if (Stun.IsStunPacket(packet.Span))
                {
                    OnStunPacket(packet.Span);
                    continue;
                }
                if (!PeerMessage.IsPeerMessage(packet.Span))
                {
                    continue;
                }

                // A known address goes straight to its link. An unknown one
                // can only be identified by opening its sealed message: if a
                // peer's key opens it, the address is that peer's.
                if (_linksByEndpoint.TryGetValue(from, out PeerLink? link))
                {
                    await link.HandlePacketAsync(packet, from, ct).ConfigureAwait(false);
                    continue;
                }
                if (PeerMessage.TypeOf(packet.Span) == PeerMessageType.Data)
                {
                    continue; // Unattributable; the path must be probed first.
                }

                foreach (Session session in _sessions.Values)
                {
                    if (PeerMessage.TryOpen(packet.Span, _identity.PrivateKey, session.Link.Peer, out _, out _))
                    {
                        _linksByEndpoint[from] = session.Link;
                        await session.Link.HandlePacketAsync(packet, from, ct).ConfigureAwait(false);
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                // Only our own socket closing ends the loop. The same exception
                // from a link that was disposed concurrently must not: that
                // would take every other peer's traffic down with it.
                if (ex is ObjectDisposedException && _disposed)
                {
                    return;
                }
            }
        }
    }

    private void OnStunPacket(ReadOnlySpan<byte> packet)
    {
        if (!Stun.TryGetTransactionId(packet, out ReadOnlySpan<byte> transactionId) ||
            !_stunWaiters.TryGetValue(Convert.ToHexString(transactionId), out TaskCompletionSource<IPEndPoint>? waiter))
        {
            return;
        }
        if (Stun.TryParseBindingResponse(packet, transactionId, out IPEndPoint? mapped))
        {
            waiter.TrySetResult(mapped);
        }
    }

    private async Task HandlePacketAsync(NodePublic source, ReadOnlyMemory<byte> packet, IPEndPoint? from, CancellationToken ct)
    {
        if (!PeerMessage.IsPeerMessage(packet.Span))
        {
            return;
        }

        // A Hello opens a session, so it is handled before any link exists.
        if (PeerMessage.TypeOf(packet.Span) is PeerMessageType.Hello or PeerMessageType.HelloAck &&
            PeerMessage.TryOpen(packet.Span, _identity.PrivateKey, source, out PeerMessageType type, out byte[]? payload) &&
            PeerHello.TryDecode(payload, out PeerHello? hello))
        {
            if (type == PeerMessageType.HelloAck)
            {
                if (_sessions.TryGetValue(source, out Session? waiting))
                {
                    waiting.HelloAck.TrySetResult(hello);
                }
                return;
            }
            // Answering involves endpoint discovery, which can take a
            // moment; doing it here would stall every other peer's packets.
            _ = Task.Run(async () =>
            {
                try
                {
                    await OnHelloAsync(source, hello, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // hello.HomeRegionId is the peer's to choose, so it can
                    // name a region we cannot reach. Failing silently here
                    // leaves the caller staring at an unexplained timeout.
                    _observer.HandshakeFailed(source, ex.Message);
                    TailcatMetrics.SessionsFailed.Add(1);
                }
            }, ct);
            return;
        }

        if (_sessions.TryGetValue(source, out Session? session))
        {
            await session.Link.HandlePacketAsync(packet, from, ct).ConfigureAwait(false);
        }
    }

    private async Task OnHelloAsync(NodePublic peer, PeerHello hello, CancellationToken ct)
    {
        // Answer into the region the peer says it listens in, which is only
        // ours when the two nodes happen to be near each other.
        int peerRegion = hello.HomeRegionId != 0 ? hello.HomeRegionId : HomeRegionId;
        DerpConnection relay = await _relays.ForRegionAsync(peerRegion, ct).ConfigureAwait(false);

        // The dialler's list is in its order of preference, so the first of
        // it that this node also has is the best the pair can do. A peer that
        // shares none is told what this node speaks rather than dropped: the
        // answer costs one relayed message and turns what would be a
        // handshake timeout into an error naming the cause.
        PeerTransport? agreed = null;
        foreach (PeerTransport offered in hello.Transports)
        {
            if (_transports.Contains(offered))
            {
                agreed = offered;
                break;
            }
        }
        if (agreed is null)
        {
            // Recorded before the answer goes out, not after: the peer acts on
            // the answer at once, and a caller watching this node would
            // otherwise see the refusal arrive there before the reason
            // appears here.
            _observer.HandshakeFailed(
                peer,
                $"no transport in common: the peer offered [{Describe(hello.Transports)}], " +
                $"this node speaks [{Describe(_transports)}]");
            TailcatMetrics.SessionsFailed.Add(1);
            await SendHelloAckAsync(relay, peer, hello.SessionId, _transports, ct).ConfigureAwait(false);
            return;
        }

        // A repeated Hello for a session we already have is just the peer
        // retrying because our answer was lost; answer again, do not restart.
        if (_sessions.TryGetValue(peer, out Session? existing) && existing.SessionId == hello.SessionId)
        {
            await SendHelloAckAsync(relay, peer, hello.SessionId, [agreed.Value], ct).ConfigureAwait(false);
            return;
        }

        PeerLink link = new(_identity.PrivateKey, peer, hello.SessionId, relay, _udp);
        Session session = new(link) { SessionId = hello.SessionId, RegionId = peerRegion };
        await ReplaceSessionAsync(peer, session).ConfigureAwait(false);
        link.PathChanged += path => OnPathChanged(peer, path);
        link.DirectProbeSent += to => _observer.DirectProbeSent(peer, to);
        link.Start();

        _observer.HandshakeStarted(peer, peerRegion);
        TailcatMetrics.SessionsStarted.Add(1);
        link.AddCandidates(hello.Endpoints);

        // The bridge points at our QUIC listener, and its address is how the
        // arriving QUIC connection is matched back to this peer.
        UdpBridge bridge = new(link, (IPEndPoint)_listener!.LocalEndPoint);
        bridge.Start();
        _acceptsByBridge[bridge.LocalEndPoint] = new PendingAccept(
            peer, hello.CertificateFingerprint, link, bridge, _time.GetTimestamp());

        await SendHelloAckAsync(relay, peer, hello.SessionId, [agreed.Value], ct).ConfigureAwait(false);
    }

    // Readable in a message an operator has to act on: "[Quic]" says more
    // than a byte would.
    private static string Describe(IReadOnlyList<PeerTransport> transports) =>
        transports.Count == 0 ? "none" : string.Join(", ", transports);

    private async Task SendHelloAckAsync(
        DerpConnection relay,
        NodePublic peer,
        ulong sessionId,
        IReadOnlyList<PeerTransport> transports,
        CancellationToken ct)
    {
        PeerHello ack = new(
            sessionId,
            _identity.Fingerprint,
            await LocalEndpointsAsync(ct).ConfigureAwait(false),
            HomeRegionId,
            transports);
        byte[] msg = PeerMessage.Seal(PeerMessageType.HelloAck, ack.Encode(), _identity.PrivateKey, peer);
        await relay.SendAsync(peer, msg, ct).ConfigureAwait(false);
    }

    private async Task SweepLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(SweepInterval, _time, ct).ConfigureAwait(false);
                await ForgetStalePendingAcceptsAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task QuicAcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                QuicConnection quic = await _listener!.AcceptConnectionAsync(ct).ConfigureAwait(false);
                if (quic.RemoteEndPoint is not IPEndPoint ep || !_acceptsByBridge.TryRemove(ep, out PendingAccept pending))
                {
                    await quic.DisposeAsync().ConfigureAwait(false);
                    continue;
                }

                TailcatConnection connection = new(
                    quic, pending.Link, pending.Bridge, pending.Peer,
                    c => OnConnectionClosedAsync(pending.Peer, pending.Link, c));
                _observer.HandshakeCompleted(
                    pending.Peer, _time.GetElapsedTime(pending.StartedAt));
                TailcatMetrics.SessionsEstablished.Add(1);

                // Only the session this handshake belongs to. A newer Hello may
                // have replaced it while QUIC was completing, and attaching
                // there would leave the new session holding a connection built
                // on the old, already-released link.
                if (_sessions.TryGetValue(pending.Peer, out Session? session) &&
                    ReferenceEquals(session.Link, pending.Link))
                {
                    session.Connection = connection;
                }
                if (!_incoming.Writer.TryWrite(connection))
                {
                    // Nobody is accepting and the queue is full. Dropping the
                    // reference alone would strand a QUIC connection, a bridge
                    // and a probe loop with no owner to close them.
                    _observer.HandshakeFailed(pending.Peer, "the inbound session queue is full");
                    TailcatMetrics.SessionsFailed.Add(1);
                    await connection.DisposeAsync().ConfigureAwait(false);
                    await CloseSessionAsync(pending.Peer).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is QuicException or AuthenticationException or ObjectDisposedException)
            {
                // One peer's handshake failing must not end the loop: a TLS
                // alert arrives here as an AuthenticationException, and a
                // stranger whose certificate this node refuses would otherwise
                // take away its ability to accept anyone ever again.
                if (ex is ObjectDisposedException)
                {
                    return;
                }
            }
        }
    }

    // SendUntilAnsweredAsync resends msg until the peer answers: the relay may
    // not have seen the peer yet when the first one goes out.
    private static async Task<PeerHello> SendUntilAnsweredAsync(
        DerpConnection relay,
        NodePublic peer,
        ReadOnlyMemory<byte> msg,
        TaskCompletionSource<PeerHello> answer,
        CancellationToken ct)
    {
        while (true)
        {
            await relay.SendAsync(peer, msg, ct).ConfigureAwait(false);
            Task delay = Task.Delay(TimeSpan.FromMilliseconds(500), ct);
            if (await Task.WhenAny(answer.Task, delay).ConfigureAwait(false) == answer.Task)
            {
                return await answer.Task.ConfigureAwait(false);
            }
            ct.ThrowIfCancellationRequested();
        }
    }

    private async Task CloseSessionAsync(NodePublic peer)
    {
        if (_sessions.TryRemove(peer, out Session? session))
        {
            await ReleaseSessionAsync(session).ConfigureAwait(false);
        }
    }

    // ReplaceSessionAsync installs a session for a peer, releasing whatever it
    // displaces. Without this, a peer reconnecting leaves the previous link
    // probing forever and still reporting path changes for a dead session.
    private async Task ReplaceSessionAsync(NodePublic peer, Session session)
    {
        // Removed before released, not after: releasing disposes the
        // connection, whose close callback looks the session up again.
        if (_sessions.TryRemove(peer, out Session? previous) && !ReferenceEquals(previous, session))
        {
            await ReleaseSessionAsync(previous).ConfigureAwait(false);
        }
        _sessions[peer] = session;
    }

    // OnConnectionClosedAsync is how a session ends when the caller simply
    // disposes its connection, which is the ordinary case and the only one
    // that used to leave the session behind.
    private async ValueTask OnConnectionClosedAsync(
        NodePublic peer,
        PeerLink link,
        TailcatConnection connection)
    {
        // Unconditionally, and before the session lookup: a connection whose
        // session was replaced mid-handshake belongs to nobody, but its link is
        // still in the routing map and a disposed link there is what silences
        // a reconnecting peer.
        ForgetEndpointsOf(link);

        if (_sessions.TryGetValue(peer, out Session? session) &&
            ReferenceEquals(session.Connection, connection) &&
            _sessions.TryRemove(new KeyValuePair<NodePublic, Session>(peer, session)))
        {
            await session.Link.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task ReleaseSessionAsync(Session session)
    {
        ForgetEndpointsOf(session.Link);
        if (session.Connection is not null)
        {
            await session.Connection.DisposeAsync().ConfigureAwait(false);
        }
        await session.Link.DisposeAsync().ConfigureAwait(false);
    }

    // ForgetEndpointsOf drops the addresses that route to a link. That map is
    // what steers arriving datagrams, so a dead entry left in it hands every
    // packet from that address to a disposed link — and a peer reconnecting
    // through the same NAT mapping, which is the usual case, arrives from
    // exactly that address and never learns a direct path again.
    private void ForgetEndpointsOf(PeerLink link)
    {
        foreach ((IPEndPoint endPoint, PeerLink mapped) in _linksByEndpoint)
        {
            if (ReferenceEquals(mapped, link))
            {
                _linksByEndpoint.TryRemove(new KeyValuePair<IPEndPoint, PeerLink>(endPoint, mapped));
            }
        }
    }

    // ForgetPendingAcceptsAsync drops half-finished accepts that no QUIC
    // connection ever claimed.
    private async Task ForgetStalePendingAcceptsAsync()
    {
        foreach ((IPEndPoint bridgeEndPoint, PendingAccept pending) in _acceptsByBridge)
        {
            if (_time.GetElapsedTime(pending.StartedAt) <= _handshakeTimeout)
            {
                continue;
            }
            if (!_acceptsByBridge.TryRemove(new KeyValuePair<IPEndPoint, PendingAccept>(bridgeEndPoint, pending)))
            {
                continue;
            }
            _observer.HandshakeFailed(pending.Peer, "the peer never completed its QUIC handshake");
            TailcatMetrics.SessionsFailed.Add(1);

            // The session went in when the Hello arrived, before the QUIC
            // handshake it is waiting for, so dropping the accept has to drop
            // it too. Leaving it behind is not just a stale entry: a peer that
            // retries the same session id is answered with a bare ack and
            // waits forever for a bridge that no longer exists. Only if it is
            // still this session, though — a newer Hello may have replaced it.
            if (_sessions.TryGetValue(pending.Peer, out Session? abandoned) &&
                ReferenceEquals(abandoned.Link, pending.Link))
            {
                _sessions.TryRemove(new KeyValuePair<NodePublic, Session>(pending.Peer, abandoned));
            }
            ForgetEndpointsOf(pending.Link);
            await pending.Bridge.DisposeAsync().ConfigureAwait(false);
            await pending.Link.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task<(DerpMap Map, int HomeRegionId, IReadOnlyList<IPEndPoint> Stun,
        IReadOnlyList<string> StunFallback)> ResolveHomeRegionAsync(
        TailcatNodeOptions options,
        CancellationToken ct)
    {
        IReadOnlyList<string> fallback = options.StunFallbackHosts;
        DerpMap map;
        if (options.DerpMap is not null)
        {
            map = options.DerpMap;
        }
        else if (options.Relay is not null)
        {
            // A caller-supplied relay is its own one-region map. Such a node
            // can only reach peers in that same region.
            int only = options.Relay.RegionID != 0 ? options.Relay.RegionID : 1;
            map = new DerpMap
            {
                Regions = { [only] = new DerpRegion { RegionID = only, Nodes = [options.Relay] } },
            };
        }
        else
        {
            map = await DerpMapFetcher.FetchAsync(options.DerpMapOptions, ct).ConfigureAwait(false);
        }

        if (map.Regions.Count == 0)
        {
            throw new TailcatException("the DERP map contains no regions");
        }

        DerpRegion home;
        if (options.HomeRegionId is int pinned)
        {
            if (!map.Regions.TryGetValue(pinned, out DerpRegion? chosen))
            {
                throw new TailcatException($"the DERP map has no region {pinned}");
            }
            home = chosen;
        }
        else if (options.Relay is not null && options.DerpMap is null)
        {
            home = map.Regions.Values.First();
        }
        else
        {
            // Measure the regions and take the closest. If nothing answers,
            // fall back to the lowest-numbered region rather than failing: a
            // relay we could not time still relays.
            int best = await options.RegionPicker.PickBestRegionAsync(map, ct).ConfigureAwait(false);
            home = best != 0 && map.Regions.TryGetValue(best, out DerpRegion? measured)
                ? measured
                : map.Regions.Values.OrderBy(r => r.RegionID).First();
        }

        if (!home.Nodes.Any(n => !n.STUNOnly))
        {
            throw new TailcatException($"DERP region {home.RegionID} contains no usable relay");
        }

        List<IPEndPoint> stun = [];
        if (options.StunServers is not null)
        {
            stun.AddRange(options.StunServers);
            fallback = [];
        }
        else
        {
            foreach (DerpNode node in home.Nodes)
            {
                if (node.STUNPort >= 0 && IPAddress.TryParse(node.IPv4, out IPAddress? ip))
                {
                    stun.Add(new IPEndPoint(ip, node.STUNPort == 0 ? Stun.DefaultPort : node.STUNPort));
                }
            }
        }
        return (map, home.RegionID, stun, fallback);
    }

    /// <summary>Closes every session and disconnects from the relay.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        _relays.RegionReconnected -= OnRegionReconnected;
        await _cts.CancelAsync().ConfigureAwait(false);
        _incoming.Writer.TryComplete();

        foreach (Session session in _sessions.Values)
        {
            await ReleaseSessionAsync(session).ConfigureAwait(false);
        }
        _sessions.Clear();

        foreach (PendingAccept pending in _acceptsByBridge.Values)
        {
            await pending.Bridge.DisposeAsync().ConfigureAwait(false);
            await pending.Link.DisposeAsync().ConfigureAwait(false);
        }
        _acceptsByBridge.Clear();
        _linksByEndpoint.Clear();

        if (_listener is not null)
        {
            await _listener.DisposeAsync().ConfigureAwait(false);
        }
        await _relays.DisposeAsync().ConfigureAwait(false);
        _udp.Dispose();

        foreach (Task loop in new[] { _derpLoop, _udpLoop, _acceptLoop, _sweepLoop, _endpointLoop })
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
            }
        }
        _cts.Dispose();
        _identity.Dispose();
    }

    private sealed class Session(PeerLink link)
    {
        public PeerLink Link { get; } = link;

        public required ulong SessionId { get; init; }

        /// <summary>The relay region this peer listens in.</summary>
        public required int RegionId { get; init; }

        public TaskCompletionSource<PeerHello> HelloAck { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TailcatConnection? Connection { get; set; }
    }

    private readonly record struct PendingAccept(
        NodePublic Peer,
        byte[] Fingerprint,
        PeerLink Link,
        UdpBridge Bridge,
        long StartedAt);
}

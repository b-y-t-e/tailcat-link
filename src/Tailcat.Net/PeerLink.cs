// Copyright (c) Tailscale Inc & contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Tailcat.Derp;
using Tailcat.Keys;

namespace Tailcat.Net;

/// <summary>How datagrams to a peer are currently travelling.</summary>
public enum PeerPathKind
{
    /// <summary>Through a DERP relay: always available, rate limited, higher latency.</summary>
    Relay,

    /// <summary>Straight to the peer over UDP, after a successful hole punch.</summary>
    Direct,
}

/// <summary>One candidate way of reaching a peer, and what we know about it.</summary>
/// <param name="Kind">Whether this path goes through a relay or straight to the peer.</param>
/// <param name="Remote">The peer's address, for a direct path.</param>
/// <param name="Rtt">The last measured round trip, if the path has ever answered.</param>
/// <param name="LastAnswer">When the path last answered a probe.</param>
/// <param name="Mtu">The largest datagram known to survive this path.</param>
public sealed record PeerPath(PeerPathKind Kind, IPEndPoint? Remote, TimeSpan? Rtt, DateTimeOffset LastAnswer, int Mtu)
{
    /// <summary>Whether the path has answered a probe recently enough to be trusted.</summary>
    public bool IsAlive(DateTimeOffset now, TimeSpan maxAge) => LastAnswer != default && now - LastAnswer < maxAge;

    /// <inheritdoc/>
    public override string ToString() =>
        Kind == PeerPathKind.Relay
            ? $"relay (mtu {Mtu})"
            : $"direct {Remote} ({Rtt?.TotalMilliseconds:F0} ms, mtu {Mtu})";
}

/// <summary>
/// Keeps a live link to one peer, over whichever path currently works best.
/// </summary>
/// <remarks>
/// <para>
/// A link always has the relay path: both sides connect outbound to a DERP
/// relay, so it works even between two networks that cannot see each other at
/// all. In parallel the link probes the peer's candidate addresses over UDP.
/// Those probes are the hole punch: each one opens a NAT mapping on the way
/// out, so the peer's probes arriving from the other direction find a way in.
/// When a direct path answers, datagrams move to it; when it goes quiet, they
/// fall back to the relay.
/// </para>
/// <para>
/// Every probe is a sealed control message, so a relay cannot forge one and
/// steer the link at an address of its choosing.
/// </para>
/// </remarks>
public sealed class PeerLink : IAsyncDisposable
{
    /// <summary>
    /// The datagram size every path is assumed to carry without probing. It
    /// matches QUIC's own floor, so a session works before any MTU probe has
    /// answered.
    /// </summary>
    public const int BaseMtu = 1200;

    /// <summary>
    /// The largest direct-path datagram worth probing for: a normal Ethernet
    /// MTU less room for IPv6 and UDP headers.
    /// </summary>
    public const int MaxDirectMtu = 1400;

    /// <summary>
    /// What a relay path can carry. A relay accepts far larger packets than
    /// any direct path, so an oversized datagram can always go that way.
    /// </summary>
    public const int RelayMtu = DerpProtocol.MaxPacketSize - 512;

    // How long a path may go unanswered before it is no longer preferred.
    private static readonly TimeSpan PathTimeout = TimeSpan.FromSeconds(6);

    // How often to probe. Unconfirmed candidates are probed hard (to punch
    // through NATs quickly); a working path is probed just often enough to
    // notice it breaking and to keep the NAT mapping alive.
    private static readonly TimeSpan PunchInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(2);

    // How long to keep punching at a candidate that never answers.
    private static readonly TimeSpan PunchDuration = TimeSpan.FromSeconds(5);

    // How long a candidate nobody has heard from is kept. Candidates only ever
    // arrived: a peer roaming between networks announces a fresh set each time
    // it moves, and without an expiry the old ones stay for the life of the
    // link, lengthening every sweep of the probe loop.
    private static readonly TimeSpan DeadPathLifetime = TimeSpan.FromMinutes(1);

    // How much better a rival path must be before traffic moves to it. Two
    // paths to the same peer often measure within noise of each other (a LAN
    // address and a tunnel address, say), and without a margin the link
    // alternates between them every probe — churning NAT mappings and burying
    // real path changes in noise.
    private static readonly TimeSpan SwitchMargin = TimeSpan.FromMilliseconds(10);
    private const double SwitchFactor = 0.8;

    private readonly NodePrivate _self;
    private readonly IRelay _relayTransport;
    private readonly Socket _udp;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<IPEndPoint, PathState> _direct = new();
    private readonly ConcurrentDictionary<ulong, PendingProbe> _pending = new();
    private readonly PathState _relay;
    private readonly Lock _mu = new();

    private Task? _probeLoop;
    private DateTimeOffset _candidatesAddedAt;
    private PathState? _chosen;
    private PeerPath? _reportedPath;
    private bool _disposed;

    private DateTimeOffset CandidatesAddedAt
    {
        get
        {
            lock (_mu)
            {
                return _candidatesAddedAt;
            }
        }
    }

    internal PeerLink(NodePrivate self, NodePublic peer, ulong sessionId, IRelay relay, Socket udp, TimeProvider? time = null)
    {
        _self = self;
        Peer = peer;
        SessionId = sessionId;
        _relayTransport = relay;
        _udp = udp;
        _time = time ?? TimeProvider.System;
        _relay = new PathState(PeerPathKind.Relay, null, RelayMtu, _time.GetUtcNow());
    }

    /// <summary>The peer this link reaches.</summary>
    public NodePublic Peer { get; }

    /// <summary>The session this link belongs to.</summary>
    public ulong SessionId { get; }

    /// <summary>Raised for each datagram the peer sends.</summary>
    /// <remarks>
    /// The memory is only valid for the duration of the call: the receive loop
    /// reuses one buffer for every packet. A handler that keeps the datagram —
    /// or hands it to an operation that outlives the call — must copy it first.
    /// </remarks>
    public event Action<ReadOnlyMemory<byte>>? DatagramReceived;

    /// <summary>Raised when the link starts using a different path.</summary>
    public event Action<PeerPath>? PathChanged;

    /// <summary>The path datagrams are currently taking.</summary>
    public PeerPath CurrentPath => Best().Snapshot();

    /// <summary>Every candidate path and what is known about it.</summary>
    public IReadOnlyList<PeerPath> Paths => [_relay.Snapshot(), .. _direct.Values.Select(p => p.Snapshot())];

    /// <summary>Starts probing paths in the background.</summary>
    public void Start() => _probeLoop ??= Task.Run(() => ProbeLoopAsync(_cts.Token));

    /// <summary>
    /// Returns the one form of an address this link stores paths under.
    /// </summary>
    /// <remarks>
    /// A dual-stack socket reports an IPv4 peer as an IPv4-mapped IPv6
    /// address, so the same peer arrives under two different names. Left
    /// alone, a candidate and the answer that proves it works would land on
    /// two separate paths, and neither would accumulate the evidence needed
    /// to be chosen.
    /// </remarks>
    public static IPEndPoint Normalize(IPEndPoint endPoint)
    {
        ArgumentNullException.ThrowIfNull(endPoint);
        return endPoint.Address.IsIPv4MappedToIPv6
            ? new IPEndPoint(endPoint.Address.MapToIPv4(), endPoint.Port)
            : endPoint;
    }

    /// <summary>
    /// Adds addresses the peer says it might be reachable at, and starts
    /// punching towards them.
    /// </summary>
    public void AddCandidates(IEnumerable<IPEndPoint> endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        foreach (IPEndPoint ep in endpoints)
        {
            if (ep.Address.Equals(IPAddress.Any) || ep.Address.Equals(IPAddress.IPv6Any) || ep.Port == 0)
            {
                continue;
            }
            IPEndPoint canonical = Normalize(ep);
            _direct.TryAdd(canonical, new PathState(PeerPathKind.Direct, canonical, BaseMtu, _time.GetUtcNow()));
        }
        lock (_mu)
        {
            _candidatesAddedAt = _time.GetUtcNow();
        }
    }

    /// <summary>Sends a datagram to the peer over the best available path.</summary>
    /// <remarks>
    /// A datagram too large for the direct path goes over the relay instead of
    /// being dropped: a relay carries far more than any punched path, and
    /// silently losing only the large packets is a failure mode that looks
    /// like a mysterious stall rather than a lost packet.
    /// </remarks>
    public async Task SendDatagramAsync(ReadOnlyMemory<byte> datagram, CancellationToken cancellationToken = default)
    {
        byte[] msg = PeerMessage.EncodeData(datagram.Span);
        await SendOverAsync(BestFor(msg.Length), msg, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Handles a packet that arrived for this link, from the relay or from a
    /// UDP address.
    /// </summary>
    /// <param name="packet">The raw packet.</param>
    /// <param name="from">The UDP address it came from, or null if it came via the relay.</param>
    internal async Task HandlePacketAsync(ReadOnlyMemory<byte> packet, IPEndPoint? from, CancellationToken cancellationToken)
    {
        if (!PeerMessage.IsPeerMessage(packet.Span))
        {
            return;
        }
        from = from is null ? null : Normalize(from);

        if (PeerMessage.TypeOf(packet.Span) == PeerMessageType.Data)
        {
            DatagramReceived?.Invoke(PeerMessage.DecodeData(packet));
            return;
        }

        // Control messages must be sealed by the peer; anything else is
        // dropped, whether it is noise or an attempt to steer us.
        if (!PeerMessage.TryOpen(packet.Span, _self, Peer, out PeerMessageType type, out byte[]? payload))
        {
            return;
        }

        switch (type)
        {
            case PeerMessageType.Ping when PeerPing.TryDecode(payload, out PeerPing ping):
                if (ping.SessionId != SessionId)
                {
                    return;
                }
                // An arriving probe also proves this address reaches us, so
                // remember it as a candidate: this is how the punched-open
                // side of a NAT gets discovered.
                PathState answerOver = from is null
                    ? _relay
                    : _direct.GetOrAdd(from, ep => new PathState(PeerPathKind.Direct, ep, BaseMtu, _time.GetUtcNow()));
                await SendOverAsync(
                    answerOver,
                    PeerMessage.Seal(PeerMessageType.Pong, ping.Encode(), _self, Peer),
                    cancellationToken).ConfigureAwait(false);
                break;

            case PeerMessageType.Pong when PeerPing.TryDecode(payload, out PeerPing pong):
                OnPong(pong, from);
                break;

            case PeerMessageType.EndpointUpdate when PeerHello.TryDecode(payload, out PeerHello? update):
                if (update.SessionId != SessionId)
                {
                    return;
                }
                // The peer moved networks. Its old addresses are probably dead,
                // but they are left in place to age out on their own: the new
                // ones simply get probed and win.
                AddCandidates(update.Endpoints);
                break;

            default:
                break;
        }
    }

    private void OnPong(PeerPing pong, IPEndPoint? from)
    {
        if (!_pending.TryRemove(pong.Id, out PendingProbe probe))
        {
            return;
        }

        PathState path = from is null
            ? _relay
            : _direct.GetOrAdd(Normalize(from), ep => new PathState(PeerPathKind.Direct, ep, BaseMtu, _time.GetUtcNow()));

        DateTimeOffset now = _time.GetUtcNow();
        path.RecordAnswer(now, now - probe.SentAt);

        // A padded probe that came back proves the path carries that size.
        if (probe.ProbedMtu > 0)
        {
            path.RecordMtu(probe.ProbedMtu);
        }

        ReportPathIfChanged();
    }

    // ReportPathIfChanged raises PathChanged when the chosen path is no longer
    // the one last reported, whichever way it moved.
    private void ReportPathIfChanged()
    {
        PeerPath current = Best().Snapshot();
        PeerPath? previous;
        lock (_mu)
        {
            if (_reportedPath is not null &&
                _reportedPath.Kind == current.Kind &&
                Equals(_reportedPath.Remote, current.Remote))
            {
                return;
            }
            previous = _reportedPath;
            _reportedPath = current;
        }
        if (previous is not null || current.Kind != PeerPathKind.Relay)
        {
            PathChanged?.Invoke(current);
        }
    }

    private async Task ProbeLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                DateTimeOffset now = _time.GetUtcNow();
                DateTimeOffset candidatesAddedAt = CandidatesAddedAt;
                bool haveDirect = _direct.Values.Any(p => p.IsAlive(now, PathTimeout));

                // A path is abandoned by falling silent, not by saying so, and
                // that is exactly when callers most need to hear about it: the
                // switch back to the relay produces no pong to react to.
                ReportPathIfChanged();

                // The relay is the floor: keep it verified so a failing direct
                // path has somewhere to fall back to.
                await ProbeAsync(_relay, 0, ct).ConfigureAwait(false);

                foreach (PathState path in _direct.Values)
                {
                    bool alive = path.IsAlive(now, PathTimeout);
                    bool punching = !alive && now - candidatesAddedAt < PunchDuration;
                    if (!alive && !punching)
                    {
                        continue;
                    }

                    await ProbeAsync(path, 0, ct).ConfigureAwait(false);

                    // Once a path works, find out whether it carries more than
                    // the conservative floor, so QUIC can use larger packets.
                    if (alive && path.Mtu < MaxDirectMtu)
                    {
                        await ProbeAsync(path, MaxDirectMtu, ct).ConfigureAwait(false);
                    }
                }

                // Forget candidates nobody has heard from. Nothing else ever
                // removed them, so a link to a roaming peer accumulated every
                // address that peer had ever had.
                foreach ((IPEndPoint endPoint, PathState path) in _direct)
                {
                    if (path.IsExpired(now, DeadPathLifetime))
                    {
                        _direct.TryRemove(new KeyValuePair<IPEndPoint, PathState>(endPoint, path));
                    }
                }

                // Sweep probes that were never answered, so their IDs don't pile up.
                foreach ((ulong id, PendingProbe probe) in _pending)
                {
                    if (now - probe.SentAt > PathTimeout)
                    {
                        _pending.TryRemove(id, out _);
                    }
                }

                TimeSpan wait = haveDirect || now - candidatesAddedAt > PunchDuration
                    ? KeepAliveInterval
                    : PunchInterval;
                await Task.Delay(wait, _time, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is SocketException or IOException or ObjectDisposedException)
            {
                // A path failing is expected; the loop keeps the others going.
                await Task.Delay(KeepAliveInterval, _time, ct).ConfigureAwait(false);
            }
        }
    }

    // ProbeAsync sends one probe. A non-zero probeMtu pads it to that total
    // size: if the answer comes back, the path carries datagrams that large.
    private async Task ProbeAsync(PathState path, int probeMtu, CancellationToken ct)
    {
        ulong id = BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8));
        byte[] payload = new PeerPing(id, SessionId).Encode();

        if (probeMtu > 0)
        {
            // The padding goes *inside* the sealed box. Appending it outside
            // would leave the box longer than the ciphertext it authenticates,
            // so it would fail to open and the probe would never be answered —
            // MTU discovery would silently never raise anything. A reader
            // takes the ping from the front and ignores the rest.
            int target = probeMtu - PeerMessage.SealOverhead;
            if (target > payload.Length)
            {
                byte[] padded = new byte[target];
                payload.CopyTo(padded, 0);
                payload = padded;
            }
        }

        byte[] msg = PeerMessage.Seal(PeerMessageType.Ping, payload, _self, Peer);
        _pending[id] = new PendingProbe(_time.GetUtcNow(), probeMtu > 0 ? msg.Length : 0);
        await SendOverAsync(path, msg, ct).ConfigureAwait(false);
    }

    private async Task SendOverAsync(PathState path, ReadOnlyMemory<byte> msg, CancellationToken ct)
    {
        if (path.Kind == PeerPathKind.Relay)
        {
            await _relayTransport.SendAsync(Peer, msg, ct).ConfigureAwait(false);
            return;
        }
        try
        {
            // A dual-stack socket needs an IPv4 destination expressed as an
            // IPv4-mapped IPv6 address.
            IPEndPoint remote = path.Remote!;
            if (_udp.AddressFamily == AddressFamily.InterNetworkV6 &&
                remote.Address.AddressFamily == AddressFamily.InterNetwork)
            {
                remote = new IPEndPoint(remote.Address.MapToIPv6(), remote.Port);
            }
            await _udp.SendToAsync(msg, SocketFlags.None, remote, ct).ConfigureAwait(false);
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.MessageSize)
        {
            // The path can't carry this size after all; remember that and let
            // the next send pick the relay.
            path.RecordMtuFailure(msg.Length);
        }
    }

    // Best chooses the path to use right now — the fastest live direct path,
    // or the relay when none is answering — and remembers the choice, so the
    // switch margin below has an incumbent to defend.
    private PathState Best()
    {
        DateTimeOffset now = _time.GetUtcNow();
        PathState? incumbent;
        lock (_mu)
        {
            incumbent = _chosen is not null && _chosen.IsAlive(now, PathTimeout) ? _chosen : null;
        }

        PathState? best = incumbent;
        foreach (PathState path in _direct.Values)
        {
            if (!path.IsAlive(now, PathTimeout) || ReferenceEquals(path, best))
            {
                continue;
            }
            if (best is null)
            {
                best = path;
                continue;
            }

            TimeSpan candidate = path.Rtt ?? TimeSpan.MaxValue;
            TimeSpan current = best.Rtt ?? TimeSpan.MaxValue;
            bool clearlyBetter = ReferenceEquals(best, incumbent)
                ? candidate < current - SwitchMargin && candidate < current * SwitchFactor
                : candidate < current;
            if (clearlyBetter)
            {
                best = path;
            }
        }

        PathState chosen = best ?? _relay;
        lock (_mu)
        {
            _chosen = chosen.Kind == PeerPathKind.Direct ? chosen : null;
        }
        return chosen;
    }

    // BestFor says where a datagram of this size should go. It deliberately
    // does not re-choose: one oversized datagram must not cost the link its
    // incumbent, or the switch margin is spent all over again on the next
    // small send and the link goes back to oscillating between two near-equal
    // paths. Answering the question is not the same as changing the answer.
    private PathState BestFor(int size)
    {
        PathState best = Best();
        if (size == 0 || size <= best.Mtu)
        {
            return best;
        }

        // The path in use cannot carry it, but another direct one may:
        // RecordMtuFailure can knock one back to the floor while a second has
        // measured its way up. Falling straight to the relay would give up a
        // working direct path for every packet over that floor.
        DateTimeOffset now = _time.GetUtcNow();
        PathState? fits = null;
        foreach (PathState path in _direct.Values)
        {
            if (!path.IsAlive(now, PathTimeout) || size > path.Mtu)
            {
                continue;
            }
            if (fits is null || (path.Rtt ?? TimeSpan.MaxValue) < (fits.Rtt ?? TimeSpan.MaxValue))
            {
                fits = path;
            }
        }
        return fits ?? _relay;
    }

    /// <summary>Stops probing and releases the link.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        await _cts.CancelAsync().ConfigureAwait(false);
        if (_probeLoop is not null)
        {
            try
            {
                await _probeLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
        _cts.Dispose();
    }

    private readonly record struct PendingProbe(DateTimeOffset SentAt, int ProbedMtu);

    // PathState is the mutable half of a candidate path; PeerPath is the
    // snapshot handed to callers.
    private sealed class PathState(PeerPathKind kind, IPEndPoint? remote, int mtu, DateTimeOffset addedAt)
    {
        private readonly Lock _mu = new();
        private TimeSpan? _rtt;
        private DateTimeOffset _lastAnswer;
        private int _mtu = mtu;

        public PeerPathKind Kind => kind;

        public IPEndPoint? Remote => remote;

        public TimeSpan? Rtt
        {
            get
            {
                lock (_mu)
                {
                    return _rtt;
                }
            }
        }

        public int Mtu
        {
            get
            {
                lock (_mu)
                {
                    return _mtu;
                }
            }
        }

        public void RecordAnswer(DateTimeOffset now, TimeSpan rtt)
        {
            lock (_mu)
            {
                _lastAnswer = now;
                // A smoothed estimate, so one slow sample doesn't flip the path.
                _rtt = _rtt is null ? rtt : (_rtt.Value * 7 + rtt) / 8;
            }
        }

        public void RecordMtu(int size)
        {
            lock (_mu)
            {
                if (size > _mtu)
                {
                    _mtu = size;
                }
            }
        }

        public void RecordMtuFailure(int size)
        {
            lock (_mu)
            {
                if (size <= _mtu)
                {
                    _mtu = Math.Max(BaseMtu, size - 1);
                }
            }
        }

        public bool IsAlive(DateTimeOffset now, TimeSpan maxAge)
        {
            lock (_mu)
            {
                return _lastAnswer != default && now - _lastAnswer < maxAge;
            }
        }

        // A path that never answered expires from when it was offered; one that
        // did, from when it last spoke.
        public bool IsExpired(DateTimeOffset now, TimeSpan maxAge)
        {
            lock (_mu)
            {
                return now - (_lastAnswer == default ? addedAt : _lastAnswer) > maxAge;
            }
        }

        public PeerPath Snapshot()
        {
            lock (_mu)
            {
                return new PeerPath(kind, remote, _rtt, _lastAnswer, _mtu);
            }
        }
    }
}

// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Diagnostics.Metrics;
using System.Net;
using Tailcat.Keys;

namespace Tailcat.Net;

/// <summary>
/// The things a node does that are worth knowing about from outside.
/// </summary>
/// <remarks>
/// <para>
/// A connection that ends up relayed instead of direct is not an error, and
/// nothing in the API distinguishes it from one that never tried. Without a
/// way to see the steps, "why is this slow?" has no answer short of a packet
/// capture — so the node reports what it did.
/// </para>
/// <para>
/// Handlers are called on the node's own loops. Keep them quick, and do not
/// throw from one: an observer must not be able to break the node it watches.
/// </para>
/// </remarks>
public interface ITailcatObserver
{
    /// <summary>The node connected to a relay region.</summary>
    void RelayConnected(int regionId);

    /// <summary>A relay connection dropped and was re-established.</summary>
    void RelayReconnected(int regionId, int attempt);

    /// <summary>A session handshake began with a peer.</summary>
    void HandshakeStarted(NodePublic peer, int peerRegionId);

    /// <summary>A session handshake completed.</summary>
    void HandshakeCompleted(NodePublic peer, TimeSpan elapsed);

    /// <summary>A session handshake gave up.</summary>
    void HandshakeFailed(NodePublic peer, string reason);

    /// <summary>Traffic to a peer moved to a different path.</summary>
    void PathChanged(NodePublic peer, PeerPath path);

    /// <summary>This node learned the addresses peers can reach it at.</summary>
    void EndpointsDiscovered(IReadOnlyList<IPEndPoint> endpoints);

    /// <summary>A probe went out on a candidate direct path.</summary>
    /// <remarks>
    /// Default-implemented, so an observer written before these existed keeps
    /// compiling. The pair is what makes a failed hole punch diagnosable:
    /// between two NATs the useful question is never whether a session came
    /// up but which probes were sent, which arrived, and from what address —
    /// a NAT that answers STUN on one port while sending from another makes
    /// the peer's advertised address differ from its real one, and nothing
    /// else here would show it.
    /// </remarks>
    void DirectProbeSent(NodePublic peer, IPEndPoint candidate)
    {
    }

    /// <summary>A datagram arrived on the node's UDP socket.</summary>
    void DatagramArrived(IPEndPoint from, int bytes, string kind)
    {
    }
}

/// <summary>An observer that ignores everything, the default.</summary>
public sealed class NullTailcatObserver : ITailcatObserver
{
    /// <summary>The shared instance.</summary>
    public static NullTailcatObserver Instance { get; } = new();

    /// <inheritdoc/>
    public void RelayConnected(int regionId)
    {
    }

    /// <inheritdoc/>
    public void RelayReconnected(int regionId, int attempt)
    {
    }

    /// <inheritdoc/>
    public void HandshakeStarted(NodePublic peer, int peerRegionId)
    {
    }

    /// <inheritdoc/>
    public void HandshakeCompleted(NodePublic peer, TimeSpan elapsed)
    {
    }

    /// <inheritdoc/>
    public void HandshakeFailed(NodePublic peer, string reason)
    {
    }

    /// <inheritdoc/>
    public void PathChanged(NodePublic peer, PeerPath path)
    {
    }

    /// <inheritdoc/>
    public void EndpointsDiscovered(IReadOnlyList<IPEndPoint> endpoints)
    {
    }
}

/// <summary>
/// An observer that writes each event as a line of text, for a CLI or a log.
/// </summary>
/// <param name="write">Where to write. <see cref="Console.Error"/> is a reasonable choice.</param>
public sealed class TextTailcatObserver(Action<string> write) : ITailcatObserver
{
    private readonly Action<string> _write = write ?? throw new ArgumentNullException(nameof(write));

    /// <inheritdoc/>
    public void RelayConnected(int regionId) => _write($"relay: connected to region {regionId}");

    /// <inheritdoc/>
    public void RelayReconnected(int regionId, int attempt) =>
        _write($"relay: region {regionId} reconnected (attempt {attempt})");

    /// <inheritdoc/>
    public void HandshakeStarted(NodePublic peer, int peerRegionId) =>
        _write($"session: handshaking with {Short(peer)} in region {peerRegionId}");

    /// <inheritdoc/>
    public void HandshakeCompleted(NodePublic peer, TimeSpan elapsed) =>
        _write($"session: {Short(peer)} up in {elapsed.TotalMilliseconds:F0} ms");

    /// <inheritdoc/>
    public void HandshakeFailed(NodePublic peer, string reason) =>
        _write($"session: {Short(peer)} failed: {reason}");

    /// <inheritdoc/>
    public void PathChanged(NodePublic peer, PeerPath path) =>
        _write($"path: {Short(peer)} now on {path}");

    /// <inheritdoc/>
    public void EndpointsDiscovered(IReadOnlyList<IPEndPoint> endpoints) =>
        _write($"endpoints: {string.Join(", ", endpoints)}");

    // A key is 64 hex characters; the first few identify it in a log well
    // enough, and the whole thing drowns the line.
    private static string Short(NodePublic key) => Convert.ToHexStringLower(key.Raw32())[..12];
    /// <inheritdoc/>
    public void DirectProbeSent(NodePublic peer, IPEndPoint candidate) => _write($"probe -> {candidate}");

    /// <inheritdoc/>
    public void DatagramArrived(IPEndPoint from, int bytes, string kind) =>
        _write($"udp <- {from} {bytes} B {kind}");

}

/// <summary>
/// The counters a node publishes, for whatever collects
/// <see cref="System.Diagnostics.Metrics"/>.
/// </summary>
public static class TailcatMetrics
{
    /// <summary>The meter name to subscribe to.</summary>
    public const string MeterName = "Tailcat.Net";

    internal static readonly Meter Meter = new(MeterName, "0.1.0");

    internal static readonly Counter<long> SessionsStarted =
        Meter.CreateCounter<long>("tailcat.sessions.started", description: "Session handshakes begun.");

    internal static readonly Counter<long> SessionsEstablished =
        Meter.CreateCounter<long>("tailcat.sessions.established", description: "Session handshakes completed.");

    internal static readonly Counter<long> SessionsFailed =
        Meter.CreateCounter<long>("tailcat.sessions.failed", description: "Session handshakes that gave up.");

    internal static readonly Counter<long> PathSwitches =
        Meter.CreateCounter<long>("tailcat.path.switches", description: "Times traffic moved between relay and direct.");

    internal static readonly Counter<long> RelayReconnects =
        Meter.CreateCounter<long>("tailcat.relay.reconnects", description: "Relay connections re-established.");
}

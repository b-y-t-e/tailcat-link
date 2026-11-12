// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Tailcat.Tailcfg;

namespace Tailcat.Net;

/// <summary>
/// Picks the DERP region with the lowest measured latency, by timing a STUN
/// round trip to each one.
/// </summary>
/// <remarks>
/// <para>
/// This is the port of Go's <c>PickBestRegion</c>, which tailcat runs when a
/// connection blob asks for automatic region selection. Every byte of a
/// relayed session crosses the relay twice, so picking a distant region taxes
/// the whole session; a single STUN round trip per region is enough to rank
/// them.
/// </para>
/// <para>
/// Regions are probed in parallel and each gets a short deadline, because the
/// measurement happens at startup and a region that is slow to answer is one
/// we did not want anyway.
/// </para>
/// <para>
/// When no region answers a STUN probe the ranking falls back to timing a TCP
/// connection to each relay instead. That is not a nicety: a relay is not
/// obliged to run STUN, and none of the four in tailcat's own DERP map do, so
/// without the fallback every node everywhere measures nothing and takes the
/// lowest-numbered region — New York, whether it is in Warsaw or Tokyo. A TCP
/// handshake is a worse clock than a STUN round trip, but it reaches the port
/// the relay is certain to be listening on.
/// </para>
/// </remarks>
public sealed class StunRegionPicker : IRegionPicker
{
    private readonly TimeSpan _timeout;
    private readonly int _probesPerRegion;

    /// <summary>Creates a picker.</summary>
    /// <param name="timeout">How long to wait for a region to answer.</param>
    /// <param name="probesPerRegion">
    /// How many probes to send per region. The lowest round trip of the set is
    /// used, so one slow sample doesn't misrank a region.
    /// </param>
    public StunRegionPicker(TimeSpan? timeout = null, int probesPerRegion = 2)
    {
        _timeout = timeout ?? TimeSpan.FromSeconds(2);
        _probesPerRegion = Math.Max(1, probesPerRegion);
    }

    /// <summary>
    /// The latency measured for each region in the last call to
    /// <see cref="PickBestRegionAsync"/>, for diagnostics.
    /// </summary>
    public IReadOnlyDictionary<int, TimeSpan> LastLatencies { get; private set; } =
        new Dictionary<int, TimeSpan>();

    /// <inheritdoc/>
    public async Task<int> PickBestRegionAsync(DerpMap derpMap, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(derpMap);

        // Both clocks at once. Asked one after the other, a map whose relays
        // run no STUN — tailcat's own — cost every node two probes' timeouts,
        // four seconds of its start, before the TCP handshakes that do answer
        // were even begun.
        Dictionary<int, Task<TimeSpan?>> stun = [];
        Dictionary<int, Task<TimeSpan?>> tcp = [];
        foreach (DerpRegion region in derpMap.Regions.Values)
        {
            if (StunEndpointOf(region) is IPEndPoint stunServer)
            {
                stun[region.RegionID] = MeasureAsync(stunServer, cancellationToken);
            }
            if (RelayEndpointOf(region) is IPEndPoint relay)
            {
                tcp[region.RegionID] = MeasureTcpAsync(relay, cancellationToken);
            }
        }

        // STUN is the better clock and is preferred wherever it answered.
        // Whichever set is in first decides: STUN all in with answers is the
        // ranking; TCP all in with answers is, unless some relay has answered
        // STUN by then — one that answers STUN has, by the time the slowest
        // handshake is in, and one still silent is not waited for. Only a set
        // that ranked nothing makes the other one waited out.
        Task stunDone = Task.WhenAll(stun.Values);
        Task tcpDone = Task.WhenAll(tcp.Values);
        await Task.WhenAny(stunDone, tcpDone).ConfigureAwait(false);

        Dictionary<int, TimeSpan> latencies = AnsweredSoFar(stun);
        if (stunDone.IsCompleted)
        {
            if (latencies.Count == 0)
            {
                await tcpDone.ConfigureAwait(false);
                latencies = AnsweredSoFar(tcp);
            }
        }
        else
        {
            if (latencies.Count == 0)
            {
                latencies = AnsweredSoFar(tcp);
            }
            if (latencies.Count == 0)
            {
                await stunDone.ConfigureAwait(false);
                latencies = AnsweredSoFar(stun);
            }
        }

        LastLatencies = latencies;

        int best = 0;
        TimeSpan bestLatency = TimeSpan.MaxValue;
        foreach ((int regionId, TimeSpan rtt) in latencies)
        {
            if (rtt < bestLatency)
            {
                (best, bestLatency) = (regionId, rtt);
            }
        }

        // Zero means "no usable measurement", which tells the caller to fall
        // back to picking a region some other way, exactly as Go does.
        return best;
    }

    private static Dictionary<int, TimeSpan> AnsweredSoFar(Dictionary<int, Task<TimeSpan?>> probes)
    {
        Dictionary<int, TimeSpan> latencies = [];
        foreach ((int regionId, Task<TimeSpan?> probe) in probes)
        {
            if (probe.IsCompletedSuccessfully && probe.Result is TimeSpan rtt)
            {
                latencies[regionId] = rtt;
            }
        }
        return latencies;
    }

    /// <summary>The address to open a TCP connection to, for a region.</summary>
    private static IPEndPoint? RelayEndpointOf(DerpRegion region)
    {
        foreach (DerpNode node in region.Nodes)
        {
            if (!node.STUNOnly && IPAddress.TryParse(node.IPv4, out IPAddress? ip))
            {
                return new IPEndPoint(ip, node.DERPPort == 0 ? 443 : node.DERPPort);
            }
        }
        return null;
    }

    // Times the TCP handshake only: the connection is closed the moment it is
    // established, without TLS, which would measure the relay's CPU as much as
    // the distance to it.
    private async Task<TimeSpan?> MeasureTcpAsync(IPEndPoint relay, CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        using Socket socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        long start = Stopwatch.GetTimestamp();
        try
        {
            await socket.ConnectAsync(relay, timeout.Token).ConfigureAwait(false);
            return Stopwatch.GetElapsedTime(start);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Returns the STUN address to probe a region at: its first node offering
    /// STUN with a usable IP.
    /// </summary>
    public static IPEndPoint? StunEndpointOf(DerpRegion region)
    {
        ArgumentNullException.ThrowIfNull(region);
        foreach (DerpNode node in region.Nodes)
        {
            if (node.STUNPort < 0)
            {
                continue; // -1 means the node offers no STUN.
            }
            if (IPAddress.TryParse(node.IPv4, out IPAddress? ip))
            {
                return new IPEndPoint(ip, node.STUNPort == 0 ? Stun.DefaultPort : node.STUNPort);
            }
        }
        return null;
    }

    private async Task<TimeSpan?> MeasureAsync(IPEndPoint stunServer, CancellationToken cancellationToken)
    {
        // A socket per region, since these probes are only measuring latency
        // and are unrelated to any NAT mapping a session will use.
        using Socket socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Any, 0));

        TimeSpan? best = null;
        for (int i = 0; i < _probesPerRegion; i++)
        {
            TimeSpan? rtt = await OneProbeAsync(socket, stunServer, cancellationToken).ConfigureAwait(false);
            if (rtt is not null && (best is null || rtt < best))
            {
                best = rtt;
            }
        }
        return best;
    }

    private async Task<TimeSpan?> OneProbeAsync(Socket socket, IPEndPoint stunServer, CancellationToken cancellationToken)
    {
        byte[] request = Stun.BuildBindingRequest(out byte[] transactionId);
        byte[] buffer = new byte[1500];

        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_timeout);
        try
        {
            long start = Stopwatch.GetTimestamp();
            await socket.SendToAsync(request, SocketFlags.None, stunServer, cts.Token).ConfigureAwait(false);

            while (true)
            {
                SocketReceiveFromResult res = await socket
                    .ReceiveFromAsync(buffer, SocketFlags.None, stunServer, cts.Token).ConfigureAwait(false);
                if (Stun.TryParseBindingResponse(buffer.AsSpan(0, res.ReceivedBytes), transactionId, out _))
                {
                    return Stopwatch.GetElapsedTime(start);
                }
                // Someone else's answer; keep waiting for ours.
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException)
        {
            // No answer in time, or the region is unreachable: unranked.
            return null;
        }
    }
}

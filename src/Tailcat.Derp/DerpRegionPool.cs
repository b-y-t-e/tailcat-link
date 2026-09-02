// Copyright (c) Tailscale Inc & contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Collections.Concurrent;
using System.Threading.Channels;
using Tailcat.Keys;
using Tailcat.Tailcfg;

namespace Tailcat.Derp;

/// <summary>
/// Holds a node's relay connections: the one it listens on, plus one per
/// region it needs to reach a peer in.
/// </summary>
/// <remarks>
/// <para>
/// A node picks the region closest to itself and listens there — its home
/// region. That is what makes a single-region design break down: two nodes
/// far apart each pick their own nearest region, and neither is listening
/// where the other is talking, so they never meet.
/// </para>
/// <para>
/// The fix is the one Tailscale uses: to reach a peer, connect to
/// <em>that peer's</em> home region and send there. A node therefore holds
/// its own home connection for receiving, and opens further connections on
/// demand for sending. Packets from all of them arrive on one channel, so
/// callers never care which connection a packet came in on.
/// </para>
/// </remarks>
public sealed class DerpRegionPool : IAsyncDisposable
{
    private readonly DerpMap _map;
    private readonly NodePrivate _privateKey;
    private readonly int _maxConnections;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<int, Lazy<Task<DerpConnection>>> _connections = new();
    private readonly ConcurrentDictionary<int, DateTimeOffset> _lastUsed = new();
    private readonly Channel<DerpReceivedPacket> _packets =
        Channel.CreateBounded<DerpReceivedPacket>(new BoundedChannelOptions(4096)
        {
            // A slow consumer must not grow memory without bound. Dropping the
            // oldest matches what a relay itself does under pressure, and
            // everything above copes with loss already.
            FullMode = BoundedChannelFullMode.DropOldest,
        });
    private readonly TimeProvider _time;
    private readonly Func<int, CancellationToken, Task<DerpClient>> _connect;
    private bool _disposed;

    private DerpRegionPool(
        DerpMap map,
        NodePrivate privateKey,
        int homeRegionId,
        int maxConnections,
        TimeProvider time,
        Func<int, CancellationToken, Task<DerpClient>>? connect)
    {
        _map = map;
        _privateKey = privateKey;
        PublicKey = privateKey.Public();
        HomeRegionId = homeRegionId;
        _maxConnections = maxConnections;
        _time = time;
        _connect = connect ?? DialRegionAsync;
    }

    /// <summary>The region this node listens in, and tells peers to reach it at.</summary>
    public int HomeRegionId { get; }

    /// <summary>The node's key, which is its address in every region.</summary>
    // Derived once: Public() is an X25519 scalar multiplication, and this is
    // read on every send.
    public NodePublic PublicKey { get; }

    /// <summary>Packets arriving from any connected region.</summary>
    public ChannelReader<DerpReceivedPacket> Packets => _packets.Reader;

    /// <summary>The regions currently connected.</summary>
    public IReadOnlyCollection<int> ConnectedRegions => [.. _connections.Keys];

    /// <summary>Raised when a region connection is re-established, with its attempt count.</summary>
    public event Action<int, int>? RegionReconnected;

    /// <summary>
    /// Connects to <paramref name="homeRegionId"/> and returns a pool that can
    /// reach the other regions in <paramref name="map"/> on demand.
    /// </summary>
    /// <param name="map">The DERP map naming every region and its relays.</param>
    /// <param name="privateKey">The node's identity, used in every region.</param>
    /// <param name="homeRegionId">The region to listen in.</param>
    /// <param name="maxConnections">
    /// How many relay connections to keep at once, at least two: the home
    /// connection plus one peer region. Beyond this, the least recently used
    /// connection that is neither home nor in use is closed.
    /// </param>
    /// <param name="timeProvider">The clock, for eviction decisions.</param>
    /// <param name="connect">
    /// How to dial one region, called again for every reconnection. Defaults
    /// to dialing the relay the map names; tests substitute an in-memory one.
    /// </param>
    /// <param name="cancellationToken">Cancels the initial connection.</param>
    public static async Task<DerpRegionPool> CreateAsync(
        DerpMap map,
        NodePrivate privateKey,
        int homeRegionId,
        int maxConnections = 4,
        TimeProvider? timeProvider = null,
        Func<int, CancellationToken, Task<DerpClient>>? connect = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(map);
        // Two is the floor: the home connection, plus one region to reach a
        // peer in. Allowing one would make every cross-region send evict the
        // connection it just opened.
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConnections, 2);

        DerpRegionPool pool = new(
            map, privateKey, homeRegionId, maxConnections, timeProvider ?? TimeProvider.System, connect);

        // The home connection must exist before the node is usable: it is
        // where peers will reach it.
        await pool.ForRegionAsync(homeRegionId, cancellationToken).ConfigureAwait(false);
        return pool;
    }

    /// <summary>The connection this node listens on.</summary>
    public Task<DerpConnection> HomeAsync(CancellationToken cancellationToken = default) =>
        ForRegionAsync(HomeRegionId, cancellationToken);

    /// <summary>
    /// Returns a connection to <paramref name="regionId"/>, opening one if
    /// needed. Use it to send to a peer whose home region that is.
    /// </summary>
    /// <exception cref="TailcatException">If the map has no usable relay there.</exception>
    public async Task<DerpConnection> ForRegionAsync(int regionId, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _lastUsed[regionId] = _time.GetUtcNow();
        Lazy<Task<DerpConnection>> entry = _connections.GetOrAdd(
            regionId,
            id => new Lazy<Task<DerpConnection>>(() => ConnectRegionAsync(id), LazyThreadSafetyMode.ExecutionAndPublication));

        DerpConnection connection;
        try
        {
            connection = await entry.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Only the wait was cancelled: the connection attempt runs on the
            // pool's own token and will still finish. Dropping the entry here
            // would orphan it — nobody left to hand it out or to close it,
            // while it keeps pushing packets into the pool's channel. So leave
            // it in place and let the next caller use it.
            throw;
        }
        catch
        {
            // A genuinely failed attempt must not stay cached, or the region
            // is broken for the life of the pool. Remove this exact entry:
            // removing by key alone could evict a newer, working one.
            _connections.TryRemove(new KeyValuePair<int, Lazy<Task<DerpConnection>>>(regionId, entry));
            throw;
        }

        // Eviction runs after the connection is in hand and never considers
        // the region just asked for: closing the connection we are about to
        // return would hand the caller a disposed object.
        await EvictIfCrowdedAsync(keep: regionId).ConfigureAwait(false);
        return connection;
    }

    private async Task<DerpConnection> ConnectRegionAsync(int regionId)
    {
        // The map is the pool's contract, so a region outside it is rejected
        // here rather than in the dialer: a caller-supplied dialer must not be
        // able to smuggle in a region the pool does not know.
        if (!_map.Regions.ContainsKey(regionId))
        {
            throw new TailcatException($"the DERP map has no region {regionId}");
        }

        DerpConnection connection = await DerpConnection
            .ConnectAsync(token => _connect(regionId, token), _time, _cts.Token)
            .ConfigureAwait(false);
        connection.Reconnected += () => RegionReconnected?.Invoke(regionId, connection.ReconnectCount);

        _ = Task.Run(() => ForwardPacketsAsync(connection, _cts.Token), CancellationToken.None);
        return connection;
    }

    private async Task<DerpClient> DialRegionAsync(int regionId, CancellationToken cancellationToken)
    {
        DerpRegion region = _map.Regions[regionId];
        DerpNode? relay = region.Nodes.FirstOrDefault(n => !n.STUNOnly);
        if (relay is null)
        {
            throw new TailcatException($"DERP region {regionId} contains no usable relay");
        }
        return await DerpClient.ConnectAsync(relay, _privateKey, cancellationToken).ConfigureAwait(false);
    }

    private async Task ForwardPacketsAsync(DerpConnection connection, CancellationToken ct)
    {
        try
        {
            await foreach (DerpReceivedPacket packet in connection.Packets.ReadAllAsync(ct).ConfigureAwait(false))
            {
                await _packets.Writer.WriteAsync(packet, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ChannelClosedException)
        {
        }
    }

    // EvictIfCrowdedAsync closes the least recently used region connection when
    // there are too many. The home connection is never evicted (it is where
    // peers reach this node), nor is the one a caller is waiting on.
    private async Task EvictIfCrowdedAsync(int keep)
    {
        while (_connections.Count > _maxConnections)
        {
            int? victim = null;
            DateTimeOffset oldest = DateTimeOffset.MaxValue;
            foreach (int regionId in _connections.Keys)
            {
                if (regionId == HomeRegionId || regionId == keep)
                {
                    continue;
                }
                DateTimeOffset used = _lastUsed.GetValueOrDefault(regionId);
                if (used < oldest)
                {
                    (victim, oldest) = (regionId, used);
                }
            }
            if (victim is not int evicted || !_connections.TryRemove(evicted, out Lazy<Task<DerpConnection>>? entry))
            {
                return;
            }
            _lastUsed.TryRemove(evicted, out _);
            try
            {
                await (await entry.Value.ConfigureAwait(false)).DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TailcatException or IOException or ObjectDisposedException or OperationCanceledException)
            {
            }
        }
    }

    /// <summary>Closes every relay connection.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        await _cts.CancelAsync().ConfigureAwait(false);
        _packets.Writer.TryComplete();

        foreach (Lazy<Task<DerpConnection>> entry in _connections.Values)
        {
            try
            {
                await (await entry.Value.ConfigureAwait(false)).DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TailcatException or IOException or ObjectDisposedException or OperationCanceledException)
            {
            }
        }
        _connections.Clear();
        _cts.Dispose();
    }
}

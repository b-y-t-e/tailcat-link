// Copyright (c) Tailscale Inc & contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Text;
using Tailcat.Keys;
using Tailcat.Tailcfg;

namespace Tailcat.Derp.Tests;

/// <summary>
/// Covers the pool of relay connections: the home connection a node listens
/// on, plus the ones it opens to reach peers listening elsewhere.
/// </summary>
public class DerpRegionPoolTests
{
    // Each fake relay stands in for one region, so a pool can be pointed at
    // several without touching the network.
    private sealed class Region : IAsyncDisposable
    {
        public Region(int id)
        {
            Id = id;
            Relay = new FakeDerpRelay();
        }

        public int Id { get; }

        public FakeDerpRelay Relay { get; }

        public ValueTask DisposeAsync() => Relay.DisposeAsync();
    }

    private static DerpMap MapOf(params Region[] regions) => new()
    {
        Regions = regions.ToDictionary(
            r => r.Id,
            r => new DerpRegion
            {
                RegionID = r.Id,
                Nodes = [new DerpNode { Name = $"{r.Id}a", HostName = $"derp{r.Id}.test" }],
            }),
    };

    // The pool dials through the map, so the test supplies a connect function
    // that maps a region to its in-memory relay.
    private static async Task<DerpRegionPool> PoolAsync(
        DerpMap map,
        NodePrivate key,
        int homeRegionId,
        Dictionary<int, FakeDerpRelay> relays,
        int maxConnections,
        CancellationToken ct) =>
        await DerpRegionPool.CreateAsync(
            map,
            key,
            homeRegionId,
            maxConnections,
            connect: async (regionId, token) =>
                await DerpClient.ConnectOverStreamAsync(
                    await relays[regionId].DialAsync(token), key, relays[regionId].PublicKey, token),
            cancellationToken: ct);

    [Fact]
    public async Task HomeRegionIsConnectedOnCreation()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using Region home = new(1);
        await using Region other = new(2);

        await using DerpRegionPool pool = await PoolAsync(
            MapOf(home, other), NodePrivate.NewKey(), 1,
            new Dictionary<int, FakeDerpRelay> { [1] = home.Relay, [2] = other.Relay }, 4, ct);

        Assert.Equal(1, pool.HomeRegionId);
        Assert.Equal([1], pool.ConnectedRegions);
    }

    /// <summary>Reaching a peer elsewhere opens that region, keeping home.</summary>
    [Fact]
    public async Task ReachingAnotherRegionOpensIt()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using Region home = new(1);
        await using Region other = new(2);

        await using DerpRegionPool pool = await PoolAsync(
            MapOf(home, other), NodePrivate.NewKey(), 1,
            new Dictionary<int, FakeDerpRelay> { [1] = home.Relay, [2] = other.Relay }, 4, ct);

        await pool.ForRegionAsync(2, ct);

        Assert.Contains(1, pool.ConnectedRegions);
        Assert.Contains(2, pool.ConnectedRegions);
    }

    /// <summary>Asking twice reuses the connection rather than dialing again.</summary>
    [Fact]
    public async Task RegionConnectionsAreReused()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using Region home = new(1);

        await using DerpRegionPool pool = await PoolAsync(
            MapOf(home), NodePrivate.NewKey(), 1,
            new Dictionary<int, FakeDerpRelay> { [1] = home.Relay }, 4, ct);

        DerpConnection first = await pool.ForRegionAsync(1, ct);
        DerpConnection second = await pool.ForRegionAsync(1, ct);

        Assert.Same(first, second);
    }

    /// <summary>
    /// Eviction must never close the connection it is about to return. With
    /// the minimum limit, the newly opened region is the only eviction
    /// candidate, so a naive sweep hands the caller a disposed connection.
    /// </summary>
    [Fact]
    public async Task EvictionNeverClosesTheConnectionBeingReturned()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using Region home = new(1);
        await using Region other = new(2);
        await using Region third = new(3);

        await using DerpRegionPool pool = await PoolAsync(
            MapOf(home, other, third), NodePrivate.NewKey(), 1,
            new Dictionary<int, FakeDerpRelay> { [1] = home.Relay, [2] = other.Relay, [3] = third.Relay },
            maxConnections: 2, ct);

        DerpConnection connection = await pool.ForRegionAsync(2, ct);

        // Usable means not disposed: a disposed connection throws here.
        await connection.SendAsync(NodePrivate.NewKey().Public(), "still open"u8.ToArray(), ct);
        Assert.Contains(2, pool.ConnectedRegions);
    }

    /// <summary>Past the limit, an idle region is dropped — never the home one.</summary>
    [Fact]
    public async Task HomeRegionSurvivesEviction()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using Region home = new(1);
        await using Region other = new(2);
        await using Region third = new(3);

        await using DerpRegionPool pool = await PoolAsync(
            MapOf(home, other, third), NodePrivate.NewKey(), 1,
            new Dictionary<int, FakeDerpRelay> { [1] = home.Relay, [2] = other.Relay, [3] = third.Relay },
            maxConnections: 2, ct);

        await pool.ForRegionAsync(2, ct);
        await pool.ForRegionAsync(3, ct);

        Assert.Contains(1, pool.ConnectedRegions);
        Assert.Contains(3, pool.ConnectedRegions);
        Assert.True(pool.ConnectedRegions.Count <= 2, $"held {pool.ConnectedRegions.Count} connections; limit was 2");
    }

    /// <summary>A limit of one leaves no room for a peer's region.</summary>
    [Fact]
    public async Task MaxConnectionsBelowTwoIsRejected()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using Region home = new(1);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => PoolAsync(
            MapOf(home), NodePrivate.NewKey(), 1,
            new Dictionary<int, FakeDerpRelay> { [1] = home.Relay }, maxConnections: 1, ct));
    }

    /// <summary>A region the map doesn't name cannot be reached.</summary>
    [Fact]
    public async Task UnknownRegionIsRejected()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using Region home = new(1);

        await using DerpRegionPool pool = await PoolAsync(
            MapOf(home), NodePrivate.NewKey(), 1,
            new Dictionary<int, FakeDerpRelay> { [1] = home.Relay }, 4, ct);

        await Assert.ThrowsAsync<TailcatException>(() => pool.ForRegionAsync(99, ct));
    }

    /// <summary>
    /// A failed dial must not be cached, or the region stays broken for the
    /// life of the pool even once the relay comes back.
    /// </summary>
    [Fact]
    public async Task AFailedDialIsNotCached()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using Region home = new(1);
        await using Region other = new(2);

        NodePrivate key = NodePrivate.NewKey();
        int attempts = 0;
        await using DerpRegionPool pool = await DerpRegionPool.CreateAsync(
            MapOf(home, other), key, 1, 4,
            connect: async (regionId, token) =>
            {
                FakeDerpRelay relay = regionId == 1 ? home.Relay : other.Relay;
                if (regionId == 2 && Interlocked.Increment(ref attempts) == 1)
                {
                    throw new DerpProtocolException("relay refused the first attempt");
                }
                return await DerpClient.ConnectOverStreamAsync(
                    await relay.DialAsync(token), key, relay.PublicKey, token);
            },
            cancellationToken: ct);

        await Assert.ThrowsAsync<DerpProtocolException>(() => pool.ForRegionAsync(2, ct));

        DerpConnection retried = await pool.ForRegionAsync(2, ct);
        Assert.NotNull(retried);
        Assert.Equal(2, attempts);
    }

    /// <summary>Packets from every connected region arrive on one channel.</summary>
    [Fact]
    public async Task PacketsFromAllRegionsShareOneChannel()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using Region home = new(1);
        await using Region other = new(2);
        Dictionary<int, FakeDerpRelay> relays = new() { [1] = home.Relay, [2] = other.Relay };

        NodePrivate listenerKey = NodePrivate.NewKey();
        await using DerpRegionPool pool = await PoolAsync(MapOf(home, other), listenerKey, 1, relays, 4, ct);
        await pool.ForRegionAsync(2, ct);

        // A sender in region 2 reaches the pool's node there.
        NodePrivate senderKey = NodePrivate.NewKey();
        await using DerpClient sender = await DerpClient.ConnectOverStreamAsync(
            await other.Relay.DialAsync(ct), senderKey, other.Relay.PublicKey, ct);
        await other.Relay.WaitForClientAsync(listenerKey.Public(), ct);

        await sender.SendAsync(listenerKey.Public(), "from region two"u8.ToArray(), ct);

        DerpReceivedPacket got = await pool.Packets.ReadAsync(ct);
        Assert.Equal(senderKey.Public(), got.Source);
        Assert.Equal("from region two", Encoding.UTF8.GetString(got.Payload.Span));
    }

    [Fact]
    public async Task DisposingTwiceIsHarmless()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using Region home = new(1);

        DerpRegionPool pool = await PoolAsync(
            MapOf(home), NodePrivate.NewKey(), 1,
            new Dictionary<int, FakeDerpRelay> { [1] = home.Relay }, 4, ct);

        await pool.DisposeAsync();
        await pool.DisposeAsync();
    }
}

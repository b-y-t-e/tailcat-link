// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Text;
using Tailcat.Tailcfg;

namespace Tailcat.Net.Tests;

/// <summary>
/// Covers reaching a peer that listens in a different relay region — the case
/// that a single-region design silently fails: two nodes far apart each pick
/// their own nearest region, and neither is listening where the other talks.
/// </summary>
[Trait("Category", "Live")]
public class MultiRegionTests
{
    private static void RequireLiveTests()
    {
        if (Environment.GetEnvironmentVariable("TAILCAT_LIVE_TESTS") != "1")
        {
            Assert.Skip("set TAILCAT_LIVE_TESTS=1 to run tests against public DERP relays");
        }
    }

    private static readonly TailcatNodeOptions BaseOptions = new()
    {
        HandshakeTimeout = TimeSpan.FromSeconds(60),
    };

    /// <summary>A node's address names the region it listens in, not just its key.</summary>
    [Fact]
    public async Task AddressCarriesTheHomeRegion()
    {
        RequireLiveTests();
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromMinutes(2));

        await using TailcatNode node = await TailcatNode.CreateAsync(BaseOptions, cts.Token);

        ConnInfo parsed = node.Address.Parse();

        Assert.Equal(node.PublicKey, parsed.ServerPublic);
        Assert.Equal(node.HomeRegionId, parsed.RegionID);
        Assert.NotEqual(0, parsed.RegionID);
    }

    /// <summary>
    /// Two nodes pinned to different regions still establish a session: the
    /// dialer connects into the listener's region rather than its own.
    /// </summary>
    [Fact]
    public async Task NodesInDifferentRegionsCanConnect()
    {
        RequireLiveTests();
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromMinutes(3));
        CancellationToken ct = cts.Token;

        DerpMap map = await DerpMapFetcher.FetchAsync(new ExpandOptions(), ct);
        List<int> regions = [.. map.Regions.Keys.Order()];
        if (regions.Count < 2)
        {
            Assert.Skip($"the DERP map has only {regions.Count} region(s); this test needs two");
        }

        // Pin each node to a different region, the way geography would.
        await using TailcatNode listener = await NodeInRegionAsync(map, regions[0], ct);
        await using TailcatNode dialer = await NodeInRegionAsync(map, regions[^1], ct);

        Assert.NotEqual(listener.HomeRegionId, dialer.HomeRegionId);

        Task<string> served = Task.Run(async () =>
        {
            await using TailcatConnection conn = await listener.AcceptConnectionAsync(ct);
            await using Stream stream = await conn.AcceptStreamAsync(ct);
            byte[] buf = new byte[256];
            int n = await stream.ReadAsync(buf, ct);
            string got = Encoding.UTF8.GetString(buf, 0, n);
            await stream.WriteAsync(Encoding.UTF8.GetBytes(got.ToUpperInvariant()), ct);
            await stream.FlushAsync(ct);
            return got;
        }, ct);

        // The address says where the listener listens; that is what makes this work.
        await using TailcatConnection client = await dialer.ConnectAsync(listener.Address, ct);
        await using Stream s = await client.OpenStreamAsync(ct);
        await s.WriteAsync("across regions"u8.ToArray(), ct);
        await s.FlushAsync(ct);

        byte[] reply = new byte[256];
        int read = await s.ReadAsync(reply, ct);

        Assert.Equal("ACROSS REGIONS", Encoding.UTF8.GetString(reply, 0, read));
        Assert.Equal("across regions", await served);

        // The dialer had to open a second relay connection to reach it.
        Assert.Contains(listener.HomeRegionId, dialer.ConnectedRegions);
        Assert.Contains(dialer.HomeRegionId, dialer.ConnectedRegions);
    }

    /// <summary>
    /// The listener answers into the dialer's region, which it learns from the
    /// handshake rather than assuming.
    /// </summary>
    [Fact]
    public async Task ListenerAnswersIntoTheDialerRegion()
    {
        RequireLiveTests();
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromMinutes(3));
        CancellationToken ct = cts.Token;

        DerpMap map = await DerpMapFetcher.FetchAsync(new ExpandOptions(), ct);
        List<int> regions = [.. map.Regions.Keys.Order()];
        if (regions.Count < 2)
        {
            Assert.Skip($"the DERP map has only {regions.Count} region(s); this test needs two");
        }

        await using TailcatNode listener = await NodeInRegionAsync(map, regions[0], ct);
        await using TailcatNode dialer = await NodeInRegionAsync(map, regions[^1], ct);

        Task accept = Task.Run(async () =>
        {
            await using TailcatConnection conn = await listener.AcceptConnectionAsync(ct);
            await using Stream stream = await conn.AcceptStreamAsync(ct);
            byte[] buf = new byte[2];
            await stream.ReadExactlyAsync(buf, ct);
        }, ct);

        await using TailcatConnection client = await dialer.ConnectAsync(listener.Address, ct);
        await using Stream s = await client.OpenStreamAsync(ct);
        await s.WriteAsync("hi"u8.ToArray(), ct);
        await s.FlushAsync(ct);
        await accept;

        // Answering meant connecting into the region the dialer named.
        Assert.Contains(dialer.HomeRegionId, listener.ConnectedRegions);
    }

    // The node gets the whole map but is pinned to one region, so it can still
    // reach a peer that listens elsewhere.
    private static Task<TailcatNode> NodeInRegionAsync(DerpMap map, int regionId, CancellationToken ct) =>
        TailcatNode.CreateAsync(
            new TailcatNodeOptions
            {
                DerpMap = map,
                HomeRegionId = regionId,
                HandshakeTimeout = BaseOptions.HandshakeTimeout,
            },
            ct);
}

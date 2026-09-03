// Copyright (c) Tailscale Inc & contributors
// Copyright (c) Andrzej Ból and contributors (.NET port)
// SPDX-License-Identifier: BSD-3-Clause

using Tailcat.Keys;
using Tailcat.Tailcfg;

namespace Tailcat.Tests;

/// <summary>
/// Covers ConnInfo.ExpandAsync and ConnBlob.ResolveAsync. Go tests these
/// only through the integration test; these pin down the documented rules
/// directly, using an in-memory DERP map so nothing touches the network.
/// </summary>
public class ConnInfoExpandTests
{
    private static readonly int[] MappedRegionIds = [1, 4];

    private static NodePublic SomeKey() => NodePublic.FromRaw32(Enumerable.Repeat((byte)7, 32).ToArray());

    private static DerpRegion Region(int id, params string[] hostNames) => new()
    {
        RegionID = id,
        RegionCode = $"r{id}",
        Nodes = [.. hostNames.Select((h, i) => new DerpNode { Name = $"{id}{(char)('a' + i)}", HostName = h, RegionID = id })],
    };

    private static DerpMap MapOf(params DerpRegion[] regions) => new()
    {
        Regions = regions.ToDictionary(r => r.RegionID),
    };

    /// <summary>An already-embedded region is left alone, network untouched.</summary>
    [Fact]
    public async Task ExpandIsNoOpWhenRegionPresent()
    {
        ConnInfo ci = new()
        {
            ServerPublic = SomeKey(),
            Region = [Region(5, "derp5a.example")],
        };

        await ci.ExpandAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(5, Assert.Single(ci.Region).RegionID);
    }

    /// <summary>Expand fills in the region IDs a blob leaves implicit.</summary>
    [Fact]
    public async Task ExpandFillsImplicitRegionIDs()
    {
        DerpRegion r = new() { Nodes = [new DerpNode { HostName = "derp.example" }] };
        ConnInfo ci = new() { ServerPublic = SomeKey(), Region = [r] };

        await ci.ExpandAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, r.RegionID);
        Assert.Equal(1, Assert.Single(r.Nodes).RegionID);
    }

    /// <summary>A RegionID is looked up in the provided DERP map.</summary>
    [Fact]
    public async Task ExpandLooksUpRegionIDInProvidedMap()
    {
        ConnInfo ci = new() { ServerPublic = SomeKey(), RegionID = 2 };
        ExpandOptions opts = new() { DerpMap = MapOf(Region(1, "a.example"), Region(2, "b.example")) };

        await ci.ExpandAsync(opts, TestContext.Current.CancellationToken);

        Assert.Equal(2, Assert.Single(ci.Region).RegionID);
    }

    /// <summary>An unknown RegionID is an error naming the map source.</summary>
    [Fact]
    public async Task ExpandRejectsUnknownRegionID()
    {
        ConnInfo ci = new() { ServerPublic = SomeKey(), RegionID = 99 };
        ExpandOptions opts = new() { DerpMap = MapOf(Region(1, "a.example")) };

        TailcatException ex = await Assert.ThrowsAsync<TailcatException>(
            () => ci.ExpandAsync(opts, TestContext.Current.CancellationToken));

        Assert.Contains("99", ex.Message, StringComparison.Ordinal);
        Assert.Contains("provided DERP map", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>RegionID -1 asks the picker, and takes the region it names.</summary>
    [Fact]
    public async Task ExpandUsesRegionPickerForAutoSelect()
    {
        ConnInfo ci = new() { ServerPublic = SomeKey(), RegionID = -1 };
        ExpandOptions opts = new()
        {
            DerpMap = MapOf(Region(1, "a.example"), Region(4, "b.example")),
            RegionPicker = new FixedRegionPicker(4),
        };

        await ci.ExpandAsync(opts, TestContext.Current.CancellationToken);

        Assert.Equal(0, ci.RegionID);
        Assert.Equal(4, Assert.Single(ci.Region).RegionID);
    }

    /// <summary>
    /// When the picker measures nothing, a region is chosen at random rather
    /// than failing.
    /// </summary>
    [Fact]
    public async Task ExpandFallsBackToRandomRegion()
    {
        ConnInfo ci = new() { ServerPublic = SomeKey(), RegionID = -1 };
        ExpandOptions opts = new()
        {
            DerpMap = MapOf(Region(1, "a.example"), Region(4, "b.example")),
            RegionPicker = NoRegionPicker.Instance,
        };

        await ci.ExpandAsync(opts, TestContext.Current.CancellationToken);

        Assert.Equal(0, ci.RegionID);
        Assert.Contains(Assert.Single(ci.Region).RegionID, MappedRegionIds);
    }

    /// <summary>An empty map can't be auto-detected from.</summary>
    [Fact]
    public async Task ExpandFailsWhenNoRegionsToPick()
    {
        ConnInfo ci = new() { ServerPublic = SomeKey(), RegionID = -1 };
        ExpandOptions opts = new() { DerpMap = MapOf(), RegionPicker = NoRegionPicker.Instance };

        TailcatException ex = await Assert.ThrowsAsync<TailcatException>(
            () => ci.ExpandAsync(opts, TestContext.Current.CancellationToken));

        Assert.Contains("auto-detect", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>A blob that already embeds its relay is returned unchanged.</summary>
    [Fact]
    public async Task ResolveIsIdentityForEmbeddedBlob()
    {
        ConnBlob blob = new ConnInfo
        {
            ServerPublic = SomeKey(),
            Region = [Region(1, "derp.example")],
        }.ToConnBlob();

        Assert.Equal(blob, await blob.ResolveAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Resolving embeds the relay details and keeps the blob short by
    /// dropping all but two nodes.
    /// </summary>
    [Fact]
    public async Task ResolveEmbedsAtMostTwoNodes()
    {
        ConnBlob blob = new ConnInfo { ServerPublic = SomeKey(), RegionID = 3 }.ToConnBlob();
        ExpandOptions opts = new()
        {
            DerpMap = MapOf(Region(3, "a.example", "b.example", "c.example")),
        };

        ConnBlob resolved = await blob.ResolveAsync(opts, TestContext.Current.CancellationToken);
        ConnInfo ci = resolved.Parse();

        Assert.Equal(0, ci.RegionID);
        DerpRegion r = Assert.Single(ci.Region);
        Assert.Equal(["a.example", "b.example"], r.Nodes.Select(n => n.HostName));
        Assert.Equal(SomeKey(), ci.ServerPublic);
    }

    private sealed class FixedRegionPicker(int regionID) : IRegionPicker
    {
        public Task<int> PickBestRegionAsync(DerpMap derpMap, CancellationToken cancellationToken = default) =>
            Task.FromResult(regionID);
    }
}

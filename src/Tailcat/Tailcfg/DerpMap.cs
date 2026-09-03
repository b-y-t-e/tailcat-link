// Copyright (c) Tailscale Inc & contributors
// Copyright (c) Andrzej Ból and contributors (.NET port)
// SPDX-License-Identifier: BSD-3-Clause

namespace Tailcat.Tailcfg;

/// <summary>
/// A DERP map: all the relay regions a node may use, keyed by region ID.
/// Port of Go's <c>tailcfg.DERPMap</c>, with the fields tailcat uses.
/// </summary>
public sealed class DerpMap
{
    /// <summary>The regions, keyed by <see cref="DerpRegion.RegionID"/>.</summary>
    public Dictionary<int, DerpRegion> Regions { get; set; } = [];

    /// <summary>
    /// Whether the client should not use its baked-in default regions when
    /// this map is in effect.
    /// </summary>
    public bool OmitDefaultRegions { get; set; }
}

// Copyright (c) Tailscale Inc & contributors
// Copyright (c) Andrzej Ból and contributors (.NET port)
// SPDX-License-Identifier: BSD-3-Clause

namespace Tailcat.Tailcfg;

/// <summary>
/// A DERP region: a set of interchangeable relay nodes in one location.
/// Port of Go's <c>tailcfg.DERPRegion</c>.
/// </summary>
public sealed class DerpRegion : IEquatable<DerpRegion>
{
    /// <summary>The region's numeric ID, unique within a DERP map.</summary>
    public int RegionID { get; set; }

    /// <summary>A short, human-readable region code, such as "sea".</summary>
    public string RegionCode { get; set; } = "";

    /// <summary>A long, human-readable region name, such as "Seattle".</summary>
    public string RegionName { get; set; } = "";

    /// <summary>The region's approximate latitude, used only for display.</summary>
    public double Latitude { get; set; }

    /// <summary>The region's approximate longitude, used only for display.</summary>
    public double Longitude { get; set; }

    /// <summary>Whether clients should avoid this region unless they have no other choice.</summary>
    public bool Avoid { get; set; }

    /// <summary>The region's nodes, in priority order.</summary>
    public List<DerpNode> Nodes { get; set; } = [];

    public bool Equals(DerpRegion? other) =>
        other is not null &&
        RegionID == other.RegionID &&
        RegionCode == other.RegionCode &&
        RegionName == other.RegionName &&
        Latitude.Equals(other.Latitude) &&
        Longitude.Equals(other.Longitude) &&
        Avoid == other.Avoid &&
        Nodes.SequenceEqual(other.Nodes);

    public override bool Equals(object? obj) => Equals(obj as DerpRegion);

    public override int GetHashCode()
    {
        HashCode h = new();
        h.Add(RegionID);
        h.Add(RegionCode);
        h.Add(RegionName);
        h.Add(Latitude);
        h.Add(Longitude);
        h.Add(Avoid);
        foreach (DerpNode n in Nodes)
        {
            h.Add(n);
        }
        return h.ToHashCode();
    }

    public override string ToString() =>
        $"DerpRegion{{RegionID={RegionID}, RegionCode={RegionCode}, RegionName={RegionName}, " +
        $"Nodes=[{string.Join(", ", Nodes)}]}}";
}

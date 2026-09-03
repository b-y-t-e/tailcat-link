// Copyright (c) Tailscale Inc & contributors
// Copyright (c) Andrzej Ból and contributors (.NET port)
// SPDX-License-Identifier: BSD-3-Clause

using System.Text.Json.Serialization;

namespace Tailcat.Tailcfg;

/// <summary>
/// A DERP node (a single relay server), as it appears in a DERP map.
/// Port of Go's <c>tailcfg.DERPNode</c>, carrying the fields tailcat
/// reads plus the ones it must recognize in order to drop them.
/// </summary>
public sealed class DerpNode : IEquatable<DerpNode>
{
    /// <summary>
    /// Name is a unique node name (across all regions). It is not a host name;
    /// netcheck identifies nodes by this name, so every node needs one.
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>The ID of the region this node is a member of.</summary>
    public int RegionID { get; set; }

    /// <summary>The node's hostname, used for both dialing and TLS SNI.</summary>
    public string HostName { get; set; } = "";

    /// <summary>
    /// The expected TLS cert name when it differs from <see cref="HostName"/>.
    /// Empty means the cert is expected to match HostName.
    /// </summary>
    public string CertName { get; set; } = "";

    /// <summary>
    /// An optional IPv4 address to use instead of resolving HostName.
    /// "none" means the node has no IPv4.
    /// </summary>
    public string IPv4 { get; set; } = "";

    /// <summary>
    /// An optional IPv6 address to use instead of resolving HostName.
    /// "none" means the node has no IPv6.
    /// </summary>
    public string IPv6 { get; set; } = "";

    /// <summary>The STUN port; 0 means the default (3478), -1 means no STUN.</summary>
    public int STUNPort { get; set; }

    /// <summary>Whether this node offers STUN only, and can't relay DERP traffic.</summary>
    public bool STUNOnly { get; set; }

    /// <summary>The DERP port; 0 means the default (443).</summary>
    public int DERPPort { get; set; }

    /// <summary>Whether the node also serves plain HTTP on port 80.</summary>
    public bool CanPort80 { get; set; }

    /// <summary>Whether to disable TLS verification. For tests only.</summary>
    public bool InsecureForTests { get; set; }

    public bool Equals(DerpNode? other) =>
        other is not null &&
        Name == other.Name &&
        RegionID == other.RegionID &&
        HostName == other.HostName &&
        CertName == other.CertName &&
        IPv4 == other.IPv4 &&
        IPv6 == other.IPv6 &&
        STUNPort == other.STUNPort &&
        STUNOnly == other.STUNOnly &&
        DERPPort == other.DERPPort &&
        CanPort80 == other.CanPort80 &&
        InsecureForTests == other.InsecureForTests;

    public override bool Equals(object? obj) => Equals(obj as DerpNode);

    public override int GetHashCode()
    {
        HashCode h = new();
        h.Add(Name);
        h.Add(RegionID);
        h.Add(HostName);
        h.Add(CertName);
        h.Add(IPv4);
        h.Add(IPv6);
        h.Add(STUNPort);
        h.Add(STUNOnly);
        h.Add(DERPPort);
        h.Add(CanPort80);
        h.Add(InsecureForTests);
        return h.ToHashCode();
    }

    public override string ToString() =>
        $"DerpNode{{Name={Name}, RegionID={RegionID}, HostName={HostName}, CertName={CertName}, " +
        $"IPv4={IPv4}, IPv6={IPv6}, STUNPort={STUNPort}, STUNOnly={STUNOnly}, DERPPort={DERPPort}, " +
        $"CanPort80={CanPort80}, InsecureForTests={InsecureForTests}}}";
}

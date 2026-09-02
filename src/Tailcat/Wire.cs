// Copyright (c) Tailscale Inc & contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Text.Json.Serialization;
using Tailcat.Cbor;
using Tailcat.Keys;
using Tailcat.Tailcfg;

namespace Tailcat;

// This file defines the CBOR wire types behind ConnBlob. They mirror the
// subset of ConnInfo and the tailcfg DERP types that tailcat actually
// uses, with single-character CBOR field names and OmitEmpty so that blobs
// with embedded DERP regions stay short. Having our own types also keeps
// the wire format independent of upstream tailcfg changes.
//
// The short CBOR field names are the wire format: do not change or reuse
// them. Each is globally unique across all the wire types here (one short
// name per property name and vice versa), which WireFieldNamesTest locks
// in.

// The JSON names are only for display (see ConnBlob.ParseRaw); they
// mirror the property names but omit empty fields, so the JSON shows
// just what the CBOR actually carries.

/// <summary>The wire form of <see cref="ConnInfo"/>.</summary>
public sealed class WireConnInfo
{
    /// <summary>The server's node public key.</summary>
    [CborProperty("p", 0)]
    [JsonPropertyName("ServerPublic")]
    public NodePublic ServerPublic { get; set; }

    /// <summary>
    /// The server's disco public key. Absent from blobs generated before
    /// tailcat gave a node separate node and disco keys.
    /// </summary>
    [CborProperty("k", 1, OmitEmpty = true)]
    [JsonPropertyName("ServerDiscoPublic")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public DiscoPublic ServerDiscoPublic { get; set; }

    /// <summary>
    /// The embedded DERP regions, if any. An element is null when the blob
    /// carried a CBOR null there; <see cref="ConnBlob.Parse"/> rejects those,
    /// while <see cref="ConnBlob.ParseRaw"/> keeps them for display.
    /// </summary>
    [CborProperty("r", 2, OmitEmpty = true)]
    [JsonPropertyName("Region")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public List<WireRegion?>? Region { get; set; }

    /// <summary>The DERP region ID, when no region is embedded.</summary>
    [CborProperty("i", 3, OmitEmpty = true)]
    [JsonPropertyName("RegionID")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int RegionID { get; set; }
}

/// <summary>The wire form of <see cref="DerpRegion"/>.</summary>
public sealed class WireRegion
{
    /// <summary>The region's numeric ID.</summary>
    [CborProperty("i", 0, OmitEmpty = true)]
    [JsonPropertyName("RegionID")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int RegionID { get; set; }

    /// <summary>The region's short code, such as "sea".</summary>
    [CborProperty("c", 1, OmitEmpty = true)]
    [JsonPropertyName("RegionCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string RegionCode { get; set; } = "";

    /// <summary>The region's long name, such as "Seattle".</summary>
    [CborProperty("m", 2, OmitEmpty = true)]
    [JsonPropertyName("RegionName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string RegionName { get; set; } = "";

    /// <summary>
    /// The region's relay nodes. As with <see cref="WireConnInfo.Region"/>, an
    /// element is null when the blob carried a CBOR null there.
    /// </summary>
    [CborProperty("N", 3, OmitEmpty = true)]
    [JsonPropertyName("Nodes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public List<WireNode?>? Nodes { get; set; }

    /// <summary>
    /// Converts a <see cref="DerpRegion"/> (such as one from the control
    /// plane's DERP map) to its wire form. Fields tailcat doesn't use
    /// (Latitude, Longitude, CanPort80, ...) are dropped, as are STUN-only
    /// nodes: they can't relay DERP traffic, which is all an embedded region
    /// is for.
    /// </summary>
    public static WireRegion Of(DerpRegion r)
    {
        ArgumentNullException.ThrowIfNull(r);
        WireRegion w = new()
        {
            RegionID = r.RegionID,
            RegionCode = r.RegionCode,
            RegionName = r.RegionName,
        };
        foreach (DerpNode n in r.Nodes)
        {
            if (n.STUNOnly)
            {
                continue;
            }
            (w.Nodes ??= []).Add(new WireNode
            {
                Name = n.Name,
                RegionID = n.RegionID,
                HostName = n.HostName,
                CertName = n.CertName,
                IPv4 = n.IPv4,
                IPv6 = n.IPv6,
                STUNPort = n.STUNPort,
                DERPPort = n.DERPPort,
                InsecureForTests = n.InsecureForTests,
            });
        }
        return w;
    }

    /// <summary>Converts this wire region back to a <see cref="DerpRegion"/>.</summary>
    public DerpRegion ToDerpRegion()
    {
        DerpRegion r = new()
        {
            RegionID = RegionID,
            RegionCode = RegionCode,
            RegionName = RegionName,
        };
        // Null nodes only reach here through ParseRaw, which is permissive on
        // purpose; ConnBlob.Parse rejects them before calling this.
        foreach (WireNode n in Nodes?.OfType<WireNode>() ?? [])
        {
            r.Nodes.Add(new DerpNode
            {
                Name = n.Name,
                RegionID = n.RegionID,
                HostName = n.HostName,
                CertName = n.CertName,
                IPv4 = n.IPv4,
                IPv6 = n.IPv6,
                STUNPort = n.STUNPort,
                DERPPort = n.DERPPort,
                InsecureForTests = n.InsecureForTests,
            });
        }
        return r;
    }
}

/// <summary>The wire form of <see cref="DerpNode"/>.</summary>
public sealed class WireNode
{
    /// <summary>The node's unique name.</summary>
    [CborProperty("n", 0, OmitEmpty = true)]
    [JsonPropertyName("Name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string Name { get; set; } = "";

    /// <summary>The ID of the region the node belongs to.</summary>
    [CborProperty("i", 1, OmitEmpty = true)]
    [JsonPropertyName("RegionID")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int RegionID { get; set; }

    /// <summary>The node's host name, used for dialing and SNI.</summary>
    [CborProperty("h", 2, OmitEmpty = true)]
    [JsonPropertyName("HostName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string HostName { get; set; } = "";

    /// <summary>
    /// The expected TLS cert name when it differs from <see cref="HostName"/>
    /// (which is used for the SNI). Empty means the cert is expected to match
    /// HostName, as with <see cref="DerpNode.CertName"/>; the production DERP
    /// map sets it on no nodes today, so this is usually absent.
    /// </summary>
    [CborProperty("t", 3, OmitEmpty = true)]
    [JsonPropertyName("CertName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string CertName { get; set; } = "";

    /// <summary>The node's IPv4 address, or "none".</summary>
    [CborProperty("4", 4, OmitEmpty = true)]
    [JsonPropertyName("IPv4")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string IPv4 { get; set; } = "";

    /// <summary>The node's IPv6 address, or "none".</summary>
    [CborProperty("6", 5, OmitEmpty = true)]
    [JsonPropertyName("IPv6")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string IPv6 { get; set; } = "";

    /// <summary>The node's STUN port; 0 means the default, -1 means none.</summary>
    [CborProperty("s", 6, OmitEmpty = true)]
    [JsonPropertyName("STUNPort")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int STUNPort { get; set; }

    /// <summary>The node's DERP port; 0 means the default.</summary>
    [CborProperty("d", 7, OmitEmpty = true)]
    [JsonPropertyName("DERPPort")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int DERPPort { get; set; }

    /// <summary>Whether to skip TLS verification. For tests only.</summary>
    [CborProperty("x", 8, OmitEmpty = true)]
    [JsonPropertyName("InsecureForTests")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool InsecureForTests { get; set; }
}

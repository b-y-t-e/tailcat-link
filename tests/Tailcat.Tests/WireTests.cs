// Copyright (c) Tailscale Inc & contributors
// SPDX-License-Identifier: BSD-3-Clause

using Tailcat;
using Tailcat.Cbor;
using Tailcat.Tailcfg;

namespace Tailcat.Tests;

/// <summary>Port of wire_test.go.</summary>
public class WireTests
{
    /// <summary>
    /// Maps the short CBOR field names used by the wire types in Wire.cs to
    /// their property names. Every short name is globally unique across all
    /// wire types, and every property name has exactly one short name.
    /// </summary>
    /// <remarks>
    /// Do not change or reuse existing entries: the short names are the
    /// ConnBlob wire format.
    /// </remarks>
    private static readonly Dictionary<string, string> WireFieldNames = new(StringComparer.Ordinal)
    {
        ["p"] = "ServerPublic",
        ["k"] = "ServerDiscoPublic",
        ["r"] = "Region",
        ["i"] = "RegionID",
        ["c"] = "RegionCode",
        ["m"] = "RegionName",
        ["N"] = "Nodes",
        ["n"] = "Name",
        ["h"] = "HostName",
        ["t"] = "CertName",
        ["4"] = "IPv4",
        ["6"] = "IPv6",
        ["s"] = "STUNPort",
        ["d"] = "DERPPort",
        ["x"] = "InsecureForTests",
    };

    /// <summary>
    /// Verifies that every property of every wire type has a CBOR field name
    /// matching <see cref="WireFieldNames"/>, so that the same property name
    /// always gets the same short name (and no short name ever means two
    /// different things).
    /// </summary>
    [Fact]
    public void WireFieldNamesMatchTheWireTypes()
    {
        Dictionary<string, string> longToShort = new(StringComparer.Ordinal);
        foreach ((string @short, string @long) in WireFieldNames)
        {
            Assert.False(
                longToShort.TryGetValue(@long, out string? prev),
                $"WireFieldNames maps both {prev} and {@short} to {@long}");
            longToShort[@long] = @short;
        }

        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (Type typ in new[] { typeof(WireConnInfo), typeof(WireRegion), typeof(WireNode) })
        {
            foreach (CborField f in CborMapper.FieldsOf(typ))
            {
                string @short = f.Attribute.Name;
                Assert.False(string.IsNullOrEmpty(@short), $"{typ}.{f.Property.Name}: missing cbor field name");
                Assert.True(
                    longToShort.TryGetValue(f.Property.Name, out string? want),
                    $"{typ}.{f.Property.Name}: field not in WireFieldNames");
                Assert.True(
                    @short == want,
                    $"{typ}.{f.Property.Name}: cbor field name {@short}; want {want}");
                seen.Add(@short);
            }
        }
        foreach ((string @short, string @long) in WireFieldNames)
        {
            Assert.True(seen.Contains(@short), $"WireFieldNames entry {@short} => {@long} matches no wire type field");
        }
    }

    /// <summary>
    /// Tests the mapping between the upstream tailcfg DERP types (as fetched
    /// from the control plane's DERP map) and tailcat's wire types: fields
    /// tailcat uses survive the round trip, unused fields are dropped, and
    /// STUN-only nodes disappear.
    /// </summary>
    [Fact]
    public void WireRegionRoundTrip()
    {
        DerpRegion input = new()
        {
            RegionID = 10,
            RegionCode = "sea",
            RegionName = "Seattle",
            Latitude = 47.609722,   // dropped
            Longitude = -122.333056, // dropped
            Avoid = true,            // dropped
            Nodes =
            [
                new DerpNode
                {
                    Name = "10b",
                    RegionID = 10,
                    HostName = "derp10b.tailscale.com",
                    CertName = "cert.example.com", // differs from HostName; must survive
                    IPv4 = "192.73.240.161",
                    IPv6 = "2607:f740:f::a01",
                    STUNPort = 3478,
                    DERPPort = 8443,
                    CanPort80 = true, // dropped
                },
                new DerpNode
                {
                    Name = "10s",
                    RegionID = 10,
                    HostName = "stun-only.tailscale.com",
                    STUNOnly = true, // whole node dropped
                },
                new DerpNode
                {
                    Name = "custom",
                    HostName = "my-derp.example.com",
                    IPv6 = "none",
                    STUNPort = -1,
                    InsecureForTests = true,
                },
            ],
        };

        DerpRegion want = new()
        {
            RegionID = 10,
            RegionCode = "sea",
            RegionName = "Seattle",
            Nodes =
            [
                new DerpNode
                {
                    Name = "10b",
                    RegionID = 10,
                    HostName = "derp10b.tailscale.com",
                    CertName = "cert.example.com",
                    IPv4 = "192.73.240.161",
                    IPv6 = "2607:f740:f::a01",
                    STUNPort = 3478,
                    DERPPort = 8443,
                },
                new DerpNode
                {
                    Name = "custom",
                    HostName = "my-derp.example.com",
                    IPv6 = "none",
                    STUNPort = -1,
                    InsecureForTests = true,
                },
            ],
        };

        DerpRegion got = WireRegion.Of(input).ToDerpRegion();
        Assert.Equal(want, got);
    }
}

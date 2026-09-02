// Copyright (c) Tailscale Inc & contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Buffers.Text;
using System.Formats.Cbor;
using Tailcat.Keys;
using Tailcat.Tailcfg;

namespace Tailcat.Tests;

/// <summary>Port of TestConnBlob from tailcat_test.go.</summary>
public class ConnBlobTests
{
    // AKey builds a node public key from the bytes given as (index, value)
    // pairs, standing in for Go's composite-literal indexes.
    private static NodePublic AKey(params (int Index, byte Value)[] bytes)
    {
        byte[] a = new byte[NodePublic.RawLen];
        foreach ((int i, byte v) in bytes)
        {
            a[i] = v;
        }
        return NodePublic.FromRaw32(a);
    }

    private static NodePublic TestKey() => AKey((1, 1), (2, 2), (31, 31));

    // A disco key with a different shape than TestKey, so a blob that carried
    // one where the other belongs would be obvious.
    private static DiscoPublic TestDiscoKey()
    {
        byte[] a = new byte[DiscoPublic.RawLen];
        a[0] = 9;
        a[30] = 8;
        return DiscoPublic.FromRaw32(a);
    }

    public static TheoryData<string, ConnInfo, string, ConnInfo?> Cases() => new()
    {
        {
            "just_key",
            new ConnInfo { ServerPublic = TestKey() },
            // The exact encoding we must keep producing.
            "tcoWFwWCAAAQIAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAHw",
            null
        },
        {
            "key_with_full_custom_region",
            new ConnInfo
            {
                ServerPublic = TestKey(),
                Region =
                [
                    new DerpRegion
                    {
                        Nodes =
                        [
                            new DerpNode
                            {
                                Name = "1a",
                                IPv4 = "400.400.400.400",
                                HostName = "my-derp.custom.example",
                            },
                            new DerpNode
                            {
                                Name = "1b",
                                IPv4 = "400.400.400.400",
                                HostName = "my-derp2.custom.example",
                            },
                        ],
                    },
                ],
            },
            "",
            new ConnInfo
            {
                ServerPublic = TestKey(),
                Region =
                [
                    new DerpRegion
                    {
                        RegionID = 1,
                        RegionCode = "1",
                        Nodes =
                        [
                            new DerpNode
                            {
                                RegionID = 1,
                                Name = "my-derp.custom.example",
                                IPv4 = "400.400.400.400",
                                HostName = "my-derp.custom.example",
                            },
                            new DerpNode
                            {
                                RegionID = 1,
                                Name = "my-derp2.custom.example",
                                IPv4 = "400.400.400.400",
                                HostName = "my-derp2.custom.example",
                            },
                        ],
                    },
                ],
            }
        },
        {
            "remove_implicit_fields_on_marshal",
            new ConnInfo
            {
                ServerPublic = TestKey(),
                Region =
                [
                    new DerpRegion
                    {
                        RegionID = 123,
                        RegionName = "Seattle",
                        Nodes =
                        [
                            new DerpNode { RegionID = 123, Name = "1a", HostName = "tc1a.ipn.dev" },
                            new DerpNode { RegionID = 123, Name = "1b", HostName = "derp1b.tailscale.com" },
                        ],
                    },
                ],
            },
            "",
            new ConnInfo
            {
                ServerPublic = TestKey(),
                Region =
                [
                    new DerpRegion
                    {
                        RegionID = 1,
                        RegionCode = "1",
                        Nodes =
                        [
                            new DerpNode { RegionID = 1, Name = "tc1a.ipn.dev", HostName = "tc1a.ipn.dev" },
                            new DerpNode { RegionID = 1, Name = "derp1b.tailscale.com", HostName = "derp1b.tailscale.com" },
                        ],
                    },
                ],
            }
        },
        {
            "region_id",
            new ConnInfo { ServerPublic = TestKey(), RegionID = 10 },
            "tcomFwWCAAAQIAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAH2FpCg",
            null
        },
        {
            // Go has no golden blob for the disco key, which it added after
            // release; this one is built from the format: the "k" field is a
            // byte string, and it sits between "p" and "i".
            "key_with_disco_key",
            new ConnInfo
            {
                ServerPublic = TestKey(),
                ServerDiscoPublic = TestDiscoKey(),
                RegionID = 10,
            },
            "tco2FwWCAAAQIAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAH2FrWCAJAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAIAGFpCg",
            null
        },
    };

    /// <param name="name">The case name, for test output.</param>
    /// <param name="ci">The ConnInfo to encode.</param>
    /// <param name="want">If non-empty, the exact encoding to check.</param>
    /// <param name="back">If non-null, the round-tripped form we want.</param>
    [Theory]
    [MemberData(nameof(Cases))]
    public void ConnBlobRoundTrips(string name, ConnInfo ci, string want, ConnInfo? back)
    {
        ConnBlob got = ci.ToConnBlob();
        if (want.Length != 0)
        {
            Assert.True(
                got.Value == want,
                $"{name}: ConnInfo.ToConnBlob marshal wrong.\n got: {got}\nwant: {want}");
        }

        ConnInfo gotCI = got.Parse();
        Assert.Equal(back ?? ci, gotCI);
    }

    // Blob encodes a hand-built CBOR map as a blob, which is how a malformed
    // one is produced: the wire types can't express a null element.
    private static ConnBlob Blob(Action<CborWriter> write)
    {
        CborWriter w = new(CborConformanceMode.Lax);
        write(w);
        return new ConnBlob(ConnBlob.Prefix + Base64Url.EncodeToString(w.Encode()));
    }

    private static ConnBlob NullRegionBlob() => Blob(w =>
    {
        w.WriteStartMap(2);
        w.WriteTextString("p");
        w.WriteByteString(TestKey().Raw32());
        w.WriteTextString("r");
        w.WriteStartArray(1);
        w.WriteNull();
        w.WriteEndArray();
        w.WriteEndMap();
    });

    private static ConnBlob NullNodeBlob() => Blob(w =>
    {
        w.WriteStartMap(2);
        w.WriteTextString("p");
        w.WriteByteString(TestKey().Raw32());
        w.WriteTextString("r");
        w.WriteStartArray(1);
        w.WriteStartMap(2);
        w.WriteTextString("i");
        w.WriteInt32(1);
        w.WriteTextString("N");
        w.WriteStartArray(1);
        w.WriteNull();
        w.WriteEndArray();
        w.WriteEndMap();
        w.WriteEndArray();
        w.WriteEndMap();
    });

    /// <summary>
    /// Port of TestParseConnBlobNullInArrays: a CBOR null in the region or
    /// node array must be an error, not a crash. Blobs come from untrusted
    /// places, so dereferencing one took the process down in Go.
    /// </summary>
    [Fact]
    public void ParseRejectsNullRegionsAndNodes()
    {
        Assert.Throws<TailcatException>(() => NullRegionBlob().Parse());
        Assert.Throws<TailcatException>(() => NullNodeBlob().Parse());

        Assert.False(NullRegionBlob().TryParse(out _));
        Assert.False(NullNodeBlob().TryParse(out _));
    }

    /// <summary>
    /// Port of TestParseConnBlobRawKeepsNulls: the raw form stays permissive,
    /// because it exists to show what a broken blob actually contains.
    /// </summary>
    [Fact]
    public void ParseRawKeepsNulls()
    {
        WireConnInfo w = NullRegionBlob().ParseRaw();

        Assert.NotNull(w.Region);
        Assert.Null(Assert.Single(w.Region));
    }
}

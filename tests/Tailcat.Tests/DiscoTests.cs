// Copyright (c) Tailscale Inc & contributors
// SPDX-License-Identifier: BSD-3-Clause

using Tailcat.Keys;

namespace Tailcat.Tests;

/// <summary>
/// Covers disco.cs, the meow packet framing. Go has no unit test for it
/// (it's exercised only by the DERP integration test), so these pin down
/// the wire layout the Go code produces.
/// </summary>
public class DiscoTests
{
    private static NodePublic NodeKey(byte fill) => NodePublic.FromRaw32(Enumerable.Repeat(fill, 32).ToArray());

    private static DiscoPublic DiscoKey(byte fill) => DiscoPublic.FromRaw32(Enumerable.Repeat(fill, 32).ToArray());

    private static byte[] ValidPing() => Disco.EncodeMeowPing(NodeKey(0x55), DiscoKey(0x66));

    [Fact]
    public void MeowPingLayout()
    {
        byte[] pkt = Disco.EncodeMeowPing(NodeKey(0xaa), DiscoKey(0xbb));

        Assert.Equal(4 + 1 + 32 + 32, pkt.Length);
        Assert.Equal("meow"u8.ToArray(), pkt[..4]);
        Assert.Equal(Disco.MeowTypePing, pkt[4]);
        Assert.All(pkt[5..37], b => Assert.Equal(0xaa, b));
        Assert.All(pkt[37..69], b => Assert.Equal(0xbb, b));
    }

    [Fact]
    public void MeowPingRoundTrips()
    {
        NodePublic node = NodeKey(0x11);
        DiscoPublic disco = DiscoKey(0x22);

        byte[] pkt = Disco.EncodeMeowPing(node, disco);

        Assert.True(Disco.IsMeowPacket(pkt));
        Assert.False(Disco.IsMeowedPacket(pkt));
        Assert.True(Disco.TryParseMeowPing(pkt, out NodePublic gotNode, out DiscoPublic gotDisco));
        Assert.Equal(node, gotNode);
        Assert.Equal(disco, gotDisco);
    }

    [Fact]
    public void MeowedLayout()
    {
        byte[] pkt = Disco.EncodeMeowed();

        Assert.Equal(5, pkt.Length);
        Assert.Equal("meow"u8.ToArray(), pkt[..4]);
        Assert.Equal(Disco.MeowTypePong, pkt[4]);
        Assert.True(Disco.IsMeowPacket(pkt));
        Assert.True(Disco.IsMeowedPacket(pkt));

        // A meowed packet is not a ping, and must not parse as one.
        Assert.False(Disco.TryParseMeowPing(pkt, out _, out _));
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { (byte)'m', (byte)'e', (byte)'o' })]
    [InlineData(new byte[] { 1, 2, 3, 4, 5 })]
    public void NonMeowPacketsAreRejected(byte[] pkt)
    {
        Assert.False(Disco.IsMeowPacket(pkt));
        Assert.False(Disco.IsMeowedPacket(pkt));
        Assert.False(Disco.TryParseMeowPing(pkt, out _, out _));
    }

    [Fact]
    public void TruncatedPingIsRejected()
    {
        byte[] pkt = Disco.EncodeMeowPing(NodeKey(1), DiscoKey(2));

        // One byte short of a full ping: the keys can't both be there.
        Assert.False(Disco.TryParseMeowPing(pkt.AsSpan(0, pkt.Length - 1), out _, out _));
    }

    /// <summary>
    /// Port of TestParseMeowPingMalformed. A meow arrives from an
    /// unauthenticated sender over DERP, so every malformed shape must be
    /// rejected without handing back a half-parsed sender.
    /// </summary>
    [Theory]
    [MemberData(nameof(MalformedPings))]
    public void MalformedPingsAreRejectedWithoutKeys(string name, byte[] pkt)
    {
        Assert.False(
            Disco.TryParseMeowPing(pkt, out NodePublic node, out DiscoPublic disco),
            $"{name}: parsed a malformed ping");
        Assert.True(node.IsZero, $"{name}: returned a node key");
        Assert.True(disco.IsZero, $"{name}: returned a disco key");
    }

    public static TheoryData<string, byte[]> MalformedPings()
    {
        byte[] full = ValidPing();
        return new TheoryData<string, byte[]>
        {
            { "empty", [] },
            { "magic_only", "meow"u8.ToArray() },
            { "type_only", [.. "meow"u8, Disco.MeowTypePing] },
            { "meowed", Disco.EncodeMeowed() },
            { "unknown_type", [.. "meow"u8, 0x7f, .. full[5..]] },
            { "truncated_node_key", full[..(5 + NodePublic.RawLen - 1)] },
            { "truncated_disco_key", full[..^1] },
        };
    }

    /// <summary>
    /// No prefix of a valid ping may parse: a short read would take the keys
    /// from whatever followed in the receive buffer.
    /// </summary>
    [Fact]
    public void EveryPrefixOfAPingIsRejected()
    {
        byte[] full = ValidPing();

        for (int n = 0; n < full.Length; n++)
        {
            Assert.False(
                Disco.TryParseMeowPing(full.AsSpan(0, n), out _, out _),
                $"parsed a {n}-byte prefix of a {full.Length}-byte ping");
        }
        Assert.True(Disco.TryParseMeowPing(full, out _, out _));
    }

    /// <summary>
    /// Trailing bytes are ignored, because the keys are read from fixed
    /// offsets. That is the documented behaviour, not an accident.
    /// </summary>
    [Fact]
    public void TrailingBytesAfterAPingAreIgnored()
    {
        NodePublic node = NodeKey(0x33);
        DiscoPublic disco = DiscoKey(0x44);
        byte[] pkt = [.. Disco.EncodeMeowPing(node, disco), (byte)'x', (byte)'y', (byte)'z'];

        Assert.True(Disco.TryParseMeowPing(pkt, out NodePublic gotNode, out DiscoPublic gotDisco));
        Assert.Equal(node, gotNode);
        Assert.Equal(disco, gotDisco);
    }

    /// <summary>
    /// Port of TestIsMeowedPacket: only the pong type is a meowed packet, and
    /// a trailer doesn't stop it from being one.
    /// </summary>
    [Theory]
    [InlineData("meowed", new byte[] { (byte)'m', (byte)'e', (byte)'o', (byte)'w', Disco.MeowTypePong }, true)]
    [InlineData("meowed_with_trailer", new byte[] { (byte)'m', (byte)'e', (byte)'o', (byte)'w', Disco.MeowTypePong, 0xff }, true)]
    [InlineData("magic_only", new byte[] { (byte)'m', (byte)'e', (byte)'o', (byte)'w' }, false)]
    [InlineData("ping_type", new byte[] { (byte)'m', (byte)'e', (byte)'o', (byte)'w', Disco.MeowTypePing }, false)]
    [InlineData("unknown_type", new byte[] { (byte)'m', (byte)'e', (byte)'o', (byte)'w', 0x03 }, false)]
    [InlineData("wrong_magic", new byte[] { (byte)'w', (byte)'o', (byte)'e', (byte)'m', Disco.MeowTypePong }, false)]
    public void IsMeowedPacketRecognisesOnlyThePongType(string name, byte[] pkt, bool want) =>
        Assert.True(Disco.IsMeowedPacket(pkt) == want, $"{name}: IsMeowedPacket != {want}");

    /// <summary>
    /// Port of TestIsMeowPacket: the magic alone decides, since it is what
    /// separates meow traffic from everything else on the DERP stream.
    /// </summary>
    [Fact]
    public void IsMeowPacketLooksOnlyAtTheMagic()
    {
        Assert.True(Disco.IsMeowPacket("meow"u8.ToArray()));
        Assert.True(Disco.IsMeowPacket(ValidPing()));
        Assert.True(Disco.IsMeowPacket(Disco.EncodeMeowed()));
        Assert.False(Disco.IsMeowPacket("woem\x01"u8.ToArray()));
    }

    /// <summary>
    /// A meow arrives on an unauthenticated DERP packet, and a zero disco key
    /// would reach endpoint advertisement, where deriving the shared secret
    /// fails. Go rejects it while parsing; so must this.
    /// </summary>
    [Fact]
    public void MeowPingWithAZeroDiscoKeyIsRejected()
    {
        byte[] pkt = Disco.EncodeMeowPing(NodeKey(0x11), DiscoKey(0x00));

        Assert.True(Disco.IsMeowPacket(pkt));
        Assert.False(Disco.TryParseMeowPing(pkt, out NodePublic node, out DiscoPublic disco));

        // A rejected packet must not leak a half-parsed sender either.
        Assert.True(node.IsZero);
        Assert.True(disco.IsZero);
    }

    /// <summary>
    /// The magic must stay distinct from WireGuard message types (1-4) and
    /// disco's own magic, since all three share the DERP packet stream.
    /// </summary>
    [Fact]
    public void MagicIsDistinctFromWireGuardMessageTypes()
    {
        foreach (byte wgType in new byte[] { 1, 2, 3, 4 })
        {
            byte[] wgPacket = [wgType, 0, 0, 0, 0];
            Assert.False(Disco.IsMeowPacket(wgPacket));
        }
        Assert.False(Disco.IsMeowPacket("TS💬"u8.ToArray()));
    }
}

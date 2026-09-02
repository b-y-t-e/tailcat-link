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

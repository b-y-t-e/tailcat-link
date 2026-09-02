// Copyright (c) Tailscale Inc & contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Net;
using Tailcat.Keys;

namespace Tailcat.Tests;

/// <summary>
/// Covers the key types and the key-derived tunnel address. Go gets these
/// from tailscale.com/types/key, which has its own tests upstream; here the
/// port needs its own.
/// </summary>
public class KeyTests
{
    [Fact]
    public void ZeroKeysAreZero()
    {
        Assert.True(default(NodePublic).IsZero);
        Assert.True(default(DiscoPublic).IsZero);
        Assert.True(default(NodePrivate).IsZero);
        Assert.False(NodePrivate.NewKey().Public().IsZero);
    }

    [Fact]
    public void PublicKeyDerivationIsStable()
    {
        NodePrivate priv = NodePrivate.NewKey();

        Assert.Equal(priv.Public(), priv.Public());
        Assert.Equal(priv.Public(), NodePrivate.FromRaw32(priv.Raw32()).Public());
        Assert.NotEqual(priv.Public(), NodePrivate.NewKey().Public());
    }

    /// <summary>
    /// A node's disco key is derived from its node key, so persisting the node
    /// key is enough, but the two must not be recoverable from one another:
    /// the disco public key travels in the clear on a direct path, while the
    /// node public key is what grants access to a server.
    /// </summary>
    [Fact]
    public void DiscoKeyIsDerivedFromButUnlinkableToTheNodeKey()
    {
        NodePrivate priv = NodePrivate.NewKey();
        DiscoPrivate disco = DiscoPrivate.ForNode(priv);

        Assert.Equal(disco, DiscoPrivate.ForNode(NodePrivate.FromRaw32(priv.Raw32())));
        Assert.NotEqual(priv.Raw32(), disco.Raw32());
        Assert.NotEqual(priv.Public().Raw32(), disco.Public().Raw32());
        Assert.NotEqual(disco, DiscoPrivate.ForNode(NodePrivate.NewKey()));
    }

    /// <summary>
    /// The derivation is a fixed HMAC of the node key, so it must keep
    /// producing the same disco key that Go's <c>discoPrivateForNode</c> does.
    /// </summary>
    [Fact]
    public void DiscoKeyDerivationMatchesGo()
    {
        NodePrivate priv = NodePrivate.FromRaw32(Enumerable.Repeat((byte)0x01, 32).ToArray());

        Assert.Equal(
            "38d7227f03e47c7ff64de4a8f8a949e1918555abdd3b4a7067d9512b7f1d665f",
            Convert.ToHexStringLower(DiscoPrivate.ForNode(priv).Raw32()));
    }

    [Fact]
    public void DerivingADiscoKeyFromTheZeroNodeKeyThrows() =>
        Assert.Throws<InvalidOperationException>(() => DiscoPrivate.ForNode(default));

    [Fact]
    public void RawBytesRoundTripAndAreCopied()
    {
        byte[] raw = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        NodePublic k = NodePublic.FromRaw32(raw);

        Assert.Equal(raw, k.Raw32());

        // Mutating the caller's array or the returned copy must not change the key.
        raw[0] = 0xff;
        k.Raw32()[1] = 0xff;
        Assert.Equal(0, k.Raw32()[0]);
        Assert.Equal(1, k.Raw32()[1]);
    }

    [Theory]
    [InlineData(31)]
    [InlineData(33)]
    public void WrongLengthIsRejected(int len)
    {
        Assert.Throws<ArgumentException>(() => NodePublic.FromRaw32(new byte[len]));
        Assert.Throws<ArgumentException>(() => DiscoPublic.FromRaw32(new byte[len]));
        Assert.Throws<ArgumentException>(() => NodePrivate.FromRaw32(new byte[len]));
    }

    [Fact]
    public void PrivateKeysNeverPrintTheirBytes()
    {
        NodePrivate priv = NodePrivate.NewKey();

        Assert.DoesNotContain(Convert.ToHexStringLower(priv.Raw32()), priv.ToString(), StringComparison.Ordinal);
        Assert.Contains("redacted", priv.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void PublicKeysPrintWithTheirPrefix()
    {
        NodePublic node = NodePublic.FromRaw32(new byte[32]);

        Assert.StartsWith("nodekey:", node.ToString(), StringComparison.Ordinal);
        Assert.StartsWith("discokey:", DiscoPublic.FromRaw32(new byte[32]).ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The tunnel address lives in Tailscale's ULA range with the low 80 bits
    /// taken from the node key, so it is a pure function of the key.
    /// </summary>
    [Fact]
    public void TunnelAddressIsDerivedFromTheKey()
    {
        byte[] raw = Enumerable.Range(0, 32).Select(i => (byte)(i + 1)).ToArray();
        NodePublic k = NodePublic.FromRaw32(raw);

        IPAddress addr = TcAddr.ForKey(k);

        Assert.Equal(IPAddress.Parse("fd7a:115c:a1e0:0102:0304:0506:0708:090a"), addr);
        Assert.Equal(addr, TcAddr.ForKey(k));
        Assert.NotEqual(addr, TcAddr.ForKey(NodePrivate.NewKey().Public()));
    }
}

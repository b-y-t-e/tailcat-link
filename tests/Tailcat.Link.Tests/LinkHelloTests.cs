// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Text;
using Tailcat.Link.Protocol;

namespace Tailcat.Link.Tests;

/// <summary>
/// Covers what the machine that dialled says about itself, and — the part
/// that matters — that the older shape of it is still read.
/// </summary>
public class LinkHelloTests
{
    /// <summary>A hello with a name comes back as it went in.</summary>
    [Fact]
    public void AHelloSurvivesARoundTrip()
    {
        LinkHello original = new("s3cret-token", "kitchen phone");

        Assert.Equal(original, LinkHello.Decode(original.Encode()));
    }

    /// <summary>A machine that says nothing about itself is not named.</summary>
    [Fact]
    public void AHelloWithoutANameDecodesToNoName()
    {
        LinkHello original = new("s3cret-token", null);

        LinkHello decoded = LinkHello.Decode(original.Encode());

        Assert.Equal("s3cret-token", decoded.PairingToken);
        Assert.Null(decoded.DisplayName);
    }

    /// <summary>
    /// A bare token — everything written before this envelope existed, the
    /// browser client included — still pairs. It simply cannot name itself.
    /// </summary>
    [Fact]
    public void ABareTokenIsReadAsTheOlderShape()
    {
        LinkHello decoded = LinkHello.Decode(Encoding.UTF8.GetBytes("s3cret-token"));

        Assert.Equal("s3cret-token", decoded.PairingToken);
        Assert.Null(decoded.DisplayName);
    }

    /// <summary>
    /// A name is bounded, because a peer must not be able to make a host write
    /// megabytes to disk by naming itself.
    /// </summary>
    [Fact]
    public void ANameOverTheLimitIsRefused()
    {
        LinkHello tooLoud = new("s3cret-token", new string('n', LinkHello.MaxDisplayNameBytes + 1));

        Assert.Throws<LinkException>(() => tooLoud.Encode());
    }

    /// <summary>
    /// The bytes themselves, not a round trip: this envelope is what the
    /// browser client sends on every pairing, and a round trip stays green
    /// through a change of prefix width or field order that would have every
    /// browser refused. The same vector is asserted in
    /// clients/browser/test/unit/link-hello.test.mjs.
    /// </summary>
    [Fact]
    public void AHelloIsExactlyTheBytesTheBrowserClientSends()
    {
        byte[] encoded = new LinkHello("tok", "phone").Encode();

        Assert.Equal("020003746F6B000570686F6E65", Convert.ToHexString(encoded));
    }

    /// <summary>
    /// And read back the other way, so the vector pins both halves rather
    /// than only what this side writes.
    /// </summary>
    [Fact]
    public void TheBytesTheBrowserClientSendsAreReadAsAHello()
    {
        LinkHello decoded = LinkHello.Decode(Convert.FromHexString("020003746F6B000570686F6E65"));

        Assert.Equal(new LinkHello("tok", "phone"), decoded);
    }

    /// <summary>A hello that stops mid-field is not read as half a hello.</summary>
    [Fact]
    public void ATruncatedHelloIsRefused()
    {
        byte[] encoded = new LinkHello("s3cret-token", "kitchen phone").Encode();

        Assert.Throws<LinkException>(() => LinkHello.Decode(encoded.AsSpan(0, encoded.Length - 4)));
    }
}

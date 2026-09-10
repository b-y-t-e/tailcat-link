// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Text;
using Tailcat.Link.Protocol;

namespace Tailcat.Link.Tests;

/// <summary>
/// Covers what the machine that dialled says about itself, and — the part
/// that matters — that the older shape of it is still read.
/// </summary>
/// <remarks>
/// The bytes themselves are pinned in <see cref="LinkVectorTests"/>, against
/// the file the browser client reads. Round trips belong here; anything that
/// asserts a hex string belongs there, where the other implementation is
/// asserting the same one.
/// </remarks>
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

    /// <summary>A hello that stops mid-field is not read as half a hello.</summary>
    [Fact]
    public void ATruncatedHelloIsRefused()
    {
        byte[] encoded = new LinkHello("s3cret-token", "kitchen phone").Encode();

        Assert.Throws<LinkException>(() => LinkHello.Decode(encoded.AsSpan(0, encoded.Length - 4)));
    }
}

// Copyright (c) Tailscale Inc & contributors
// SPDX-License-Identifier: BSD-3-Clause

using Tailcat.Keys;

namespace Tailcat.Link.Tests;

/// <summary>
/// Covers the one thing a person has to carry between the two machines, and
/// therefore the one thing they can get wrong.
/// </summary>
public class InvitationCodeTests
{
    private static ConnBlob SomeAddress(int regionId = 7) =>
        new ConnInfo { ServerPublic = NodePrivate.NewKey().Public(), RegionID = regionId }.ToConnBlob();

    /// <summary>A code carries the host's key and region, unchanged.</summary>
    [Fact]
    public void ACodeRoundTripsToTheAddressItWasMadeFrom()
    {
        ConnBlob address = SomeAddress(regionId: 12);

        InvitationCode code = InvitationCode.ForAddress(address, "s3cret-token");
        ConnInfo parsed = code.Address.Parse();

        Assert.Equal(address, code.Address);
        Assert.Equal("s3cret-token", code.PairingToken);
        Assert.Equal(12, parsed.RegionID);
        Assert.Equal(address.Parse().ServerPublic, parsed.ServerPublic);
    }

    /// <summary>
    /// A code that has been through a chat window, an email, or a terminal
    /// still works: whitespace anywhere in it is not part of the code.
    /// </summary>
    [Theory]
    [InlineData("  {0}  ")]
    [InlineData("{0}\r\n")]
    [InlineData("\t{0}")]
    public void WhitespaceAroundACodeIsIgnored(string wrapper)
    {
        InvitationCode original = InvitationCode.ForAddress(SomeAddress(), "s3cret-token");

        InvitationCode parsed = InvitationCode.Parse(
            string.Format(System.Globalization.CultureInfo.InvariantCulture, wrapper, original.Value));

        Assert.Equal(original, parsed);
    }

    /// <summary>Anything that is not a code is refused, rather than half-accepted.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("hello")]
    [InlineData("tc")]
    [InlineData("tc!!!not-base64!!!")]
    [InlineData("tc!!!not-base64!!!.token")]
    public void SomethingThatIsNotACodeIsRefused(string text)
    {
        Assert.False(InvitationCode.TryParse(text, out _));
        Assert.Throws<LinkException>(() => InvitationCode.Parse(text));
    }

    /// <summary>Null is not a code either, and says so rather than throwing.</summary>
    [Fact]
    public void NullIsNotACode() => Assert.False(InvitationCode.TryParse(null, out _));

    /// <summary>An empty address has nothing to point at, so it is not a code.</summary>
    [Fact]
    public void AnEmptyAddressCannotBecomeACode() =>
        Assert.Throws<ArgumentException>(() => InvitationCode.ForAddress(new ConnBlob(""), "s3cret-token"));

    /// <summary>
    /// An address on its own is not a code, however well-formed: it is what
    /// the host tells every relay it connects to, so anything that accepted
    /// it would be accepting a code that has no secret in it.
    /// </summary>
    [Fact]
    public void AnAddressWithoutAPairingTokenIsNotACode()
    {
        string addressOnly = SomeAddress().Value;

        Assert.False(InvitationCode.TryParse(addressOnly, out _));
        Assert.False(InvitationCode.TryParse(addressOnly + ".", out _));
        Assert.Throws<ArgumentException>(() => InvitationCode.ForAddress(SomeAddress(), "  "));
    }
}

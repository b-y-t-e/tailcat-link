// Copyright (c) Tailscale Inc & contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Tailcat.Link.Storage;

/// <summary>
/// The secret half of the code a host is showing, and the moment it stops
/// being worth anything.
/// </summary>
/// <remarks>
/// The address in an invitation code is public by nature — the host hands it
/// to every relay it connects to. This is the part that is not, and the only
/// reason a stranger who learned the address cannot pair with an unclaimed
/// host first. It expires because an invitation nobody accepted is a door
/// left open: after the window the host pairs with nobody until its operator
/// starts it again and reads out a new code.
/// </remarks>
public sealed record PairingOffer(string Token, DateTimeOffset ExpiresAt)
{
    /// <summary>
    /// 128 bits, which is not guessable and still fits in a code somebody has
    /// to read aloud or photograph.
    /// </summary>
    private const int TokenBytes = 16;

    /// <summary>Mints an offer good for <paramref name="window"/> from now.</summary>
    public static PairingOffer New(TimeSpan window, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        return new PairingOffer(
            Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(TokenBytes)),
            time.GetUtcNow() + window);
    }

    /// <summary>Whether the window has closed.</summary>
    public bool HasExpired(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        return time.GetUtcNow() >= ExpiresAt;
    }

    /// <summary>
    /// Whether <paramref name="token"/> is the one that was shown, compared
    /// in a time that says nothing about how much of it was right.
    /// </summary>
    public bool Matches(string? token) =>
        token is not null
        && CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(Token));
}

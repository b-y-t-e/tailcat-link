// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Tailcat.Link.Storage;

/// <summary>
/// The secret half of a code a host is showing, and the moment it stops
/// being worth anything.
/// </summary>
/// <remarks>
/// The address in an invitation code is public by nature — the host hands it
/// to every relay it connects to. This is the part that is not, and the only
/// reason a stranger who learned the address cannot pair with an unclaimed
/// host first. It expires because an invitation nobody accepted is a door
/// left open: after the window the host pairs with nobody until its operator
/// starts it again and reads out a new code.
/// <para>
/// A host may have several of these live at once, one per device it is
/// waiting for, which is why each carries an <see cref="Id"/> that survives a
/// restart and can be revoked on its own.
/// </para>
/// </remarks>
public sealed record PairingOffer(string Token, DateTimeOffset ExpiresAt)
{
    /// <summary>
    /// 128 bits, which is not guessable and still fits in a code somebody has
    /// to read aloud or photograph.
    /// </summary>
    private const int TokenBytes = 16;

    /// <summary>Names this offer, so it can be revoked without naming its token.</summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>
    /// What the operator called this invitation. It never goes on the wire —
    /// it exists so that a list of pending invitations reads as something
    /// other than a column of identifiers.
    /// </summary>
    public string? Label { get; init; }

    /// <summary>Whether admitting one machine spends the offer.</summary>
    public bool SingleUse { get; init; }

    /// <summary>Mints an offer good for <paramref name="window"/> from now.</summary>
    public static PairingOffer New(TimeSpan window, TimeProvider time) =>
        New(window, time, label: null, singleUse: false);

    /// <summary>Mints a labelled offer good for <paramref name="window"/> from now.</summary>
    public static PairingOffer New(TimeSpan window, TimeProvider time, string? label, bool singleUse)
    {
        ArgumentNullException.ThrowIfNull(time);
        return new PairingOffer(
            Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(TokenBytes)),
            time.GetUtcNow() + window)
        {
            Label = label,
            SingleUse = singleUse,
        };
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

// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

namespace Tailcat.Link;

/// <summary>
/// What to mint an invitation for: how long it is good for, whether one
/// machine spends it, and what to call it in a list.
/// </summary>
/// <remarks>
/// Every field has a default, so <c>new InvitationRequest()</c> asks for the
/// same invitation a single-peer host publishes by itself.
/// </remarks>
public sealed record InvitationRequest
{
    /// <summary>
    /// What the operator calls this invitation — "kitchen phone", say.
    /// </summary>
    /// <remarks>
    /// It never goes on the wire. It exists so that the operator's list of
    /// pending invitations is readable, and so that an invitation can be
    /// found again after the code has been drawn and forgotten.
    /// </remarks>
    public string? Label { get; init; }

    /// <summary>
    /// How long the code may be used to pair, or null for
    /// <see cref="LinkOptions.PairingWindow"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The lifetime is not positive.</exception>
    public TimeSpan? Lifetime
    {
        get => _lifetime;
        init
        {
            if (value is { } lifetime)
            {
                ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lifetime, TimeSpan.Zero);
            }
            _lifetime = value;
        }
    }

    private readonly TimeSpan? _lifetime;

    /// <summary>
    /// Whether the first machine to pair with it spends it.
    /// </summary>
    /// <remarks>
    /// The right answer for a barcode shown to one device: a code that keeps
    /// working after the device it was for has joined is a code worth
    /// photographing over somebody's shoulder.
    /// </remarks>
    public bool SingleUse { get; init; }
}

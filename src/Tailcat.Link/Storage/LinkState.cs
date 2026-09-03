// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using Tailcat.Keys;

namespace Tailcat.Link.Storage;

/// <summary>
/// Everything a machine has to remember between runs for a link to survive a
/// reboot: who it is, where it listens, and who it is paired with.
/// </summary>
/// <remarks>
/// This is the whole reason a link can heal itself. A node that generated a
/// fresh key on every start would have a different address every time, and
/// the code the operator scanned once would point at a machine that no longer
/// exists.
/// </remarks>
public sealed record LinkState
{
    /// <summary>This machine's identity. Secret: it is the machine's name and its password at once.</summary>
    public required NodePrivate PrivateKey { get; init; }

    /// <summary>
    /// The relay region this machine listens in, once chosen.
    /// </summary>
    /// <remarks>
    /// Only a host records this. Its address is the key <em>and</em> the
    /// region, so a host that re-measured its closest region after moving
    /// would invalidate the code it already published. A joiner has no
    /// published address, so it measures afresh every start and gets the
    /// closest relay wherever it happens to be.
    /// </remarks>
    public int? HomeRegionId { get; init; }

    /// <summary>
    /// The pairing secret a host is currently offering, or null on a machine
    /// that has never hosted.
    /// </summary>
    /// <remarks>
    /// It is kept after pairing rather than cleared, so that the host can
    /// still show the code an operator wrote down — it names this machine
    /// for good, even though it will pair with nobody else now.
    /// </remarks>
    public PairingOffer? Pairing { get; init; }

    /// <summary>The code this machine joined with, so joining again needs no code.</summary>
    public InvitationCode? PeerCode { get; init; }

    /// <summary>
    /// The machine this one is paired with, or the zero key before the first
    /// pairing. A host accepts sessions from this key alone once it is set.
    /// </summary>
    public NodePublic PeerKey { get; init; }

    /// <summary>Whether a peer has been pinned yet.</summary>
    public bool IsPaired => !PeerKey.IsZero;
}

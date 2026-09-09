// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using Tailcat.Keys;

namespace Tailcat.Link.Storage;

/// <summary>
/// One machine this one has paired with, as it is remembered between runs.
/// </summary>
/// <remarks>
/// A host may hold several of these — see <see cref="LinkOptions.MaxPeers"/> —
/// so the list of them, rather than a single key, is what a paired machine
/// writes down.
/// </remarks>
/// <param name="Key">The peer's public key, which is what it is admitted by.</param>
/// <param name="Name">
/// What the peer called itself when it joined, or null if it said nothing.
/// It is a hint from the other machine and nothing authenticates it.
/// </param>
/// <param name="PairedAt">When the pairing was made.</param>
/// <param name="LastSeen">When a session with it was last established.</param>
public sealed record PairedPeer(NodePublic Key, string? Name, DateTimeOffset PairedAt, DateTimeOffset LastSeen)
{
    /// <summary>The invitation this machine was admitted by, if it was admitted by one.</summary>
    /// <remarks>
    /// Recorded so that unpairing can withdraw the invitation with the peer.
    /// An offer left live is a way straight back in: the machine just
    /// forgotten still holds the code, still reconnects on its own, and is
    /// admitted again as a stranger with a valid token. It is null for a peer
    /// stored before this was written down, and on the joining end, which
    /// pairs with a host rather than admitting one.
    /// </remarks>
    public Guid? InvitationId { get; init; }
}

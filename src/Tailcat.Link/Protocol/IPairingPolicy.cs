// Copyright (c) Tailscale Inc & contributors
// SPDX-License-Identifier: BSD-3-Clause

using Tailcat.Keys;

namespace Tailcat.Link.Protocol;

/// <summary>
/// What the hosting end asks before it lets a machine in: is this the peer,
/// or the one that was invited?
/// </summary>
/// <remarks>
/// It is an abstraction rather than the stored record itself so that the
/// handshake depends on the question and not on where the answer is kept — a
/// test can refuse everyone without a store, and the rule can change without
/// the protocol changing.
/// </remarks>
internal interface IPairingPolicy
{
    /// <summary>The machine already paired with, or the zero key.</summary>
    NodePublic Peer { get; }

    /// <summary>
    /// Decides whether <paramref name="candidate"/> may speak to this
    /// machine, pinning it as the peer if this is the pairing.
    /// </summary>
    /// <param name="candidate">The machine that has just connected.</param>
    /// <param name="pairingToken">The secret it presented.</param>
    /// <param name="cancellationToken">Cancels writing the pairing down.</param>
    /// <returns>
    /// True for the paired peer, or for a stranger holding the token that is
    /// currently on offer; false for everyone else, including a stranger
    /// holding a token whose window has closed.
    /// </returns>
    Task<bool> AdmitAsync(NodePublic candidate, string pairingToken, CancellationToken cancellationToken);
}

// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using Tailcat.Link.Storage;
using Tailcat.Keys;

namespace Tailcat.Link.Protocol;

/// <summary>
/// What the hosting end asks before it lets a machine in: is this one of its
/// peers, or a machine holding an invitation it is still offering?
/// </summary>
/// <remarks>
/// It is an abstraction rather than the stored record itself so that the
/// handshake depends on the question and not on where the answer is kept — a
/// test can refuse everyone without a store, and the rule can change without
/// the protocol changing.
/// </remarks>
internal interface IPairingPolicy
{
    /// <summary>The machines already paired with, in the order they joined.</summary>
    IReadOnlyList<PairedPeer> Peers { get; }

    /// <summary>
    /// Decides whether <paramref name="candidate"/> may speak to this
    /// machine, writing the pairing down if this is one.
    /// </summary>
    /// <param name="candidate">The machine that has just connected.</param>
    /// <param name="hello">What it said about itself, decoded.</param>
    /// <param name="cancellationToken">Cancels writing the pairing down.</param>
    /// <returns>
    /// The peer as it is now remembered, for a machine already paired or a
    /// stranger holding an invitation still on offer; null for everyone else,
    /// including one arriving at a host that is already full.
    /// </returns>
    /// <exception cref="InvitationExpiredException">
    /// If the invitation it holds is one this machine was offering and has
    /// stopped: the one refusal with an obvious cure, so it is worth telling
    /// this end's operator apart from the rest.
    /// </exception>
    Task<PairedPeer?> AdmitAsync(NodePublic candidate, LinkHello hello, CancellationToken cancellationToken);
}

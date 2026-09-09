// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using Tailcat.Link.Storage;

namespace Tailcat.Link.Protocol;

/// <summary>
/// The codes a machine is currently worth showing, and whether any of them
/// can still be used to pair.
/// </summary>
/// <remarks>
/// It is an abstraction for the same reason <see cref="ISessionSource"/> is:
/// the two ends of a link answer "what is my code?" differently, and nothing
/// above should have to know which end it is on.
/// </remarks>
internal interface IInvitationSource
{
    /// <summary>The code worth publishing now, which minting another may replace.</summary>
    InvitationCode Current { get; }

    /// <summary>
    /// When <see cref="Current"/> stops being able to pair, or null when
    /// nothing is waiting to be paired.
    /// </summary>
    DateTimeOffset? ExpiresAt { get; }

    /// <summary>Every invitation still live, oldest first.</summary>
    IReadOnlyList<LinkInvitation> All { get; }

    /// <summary>Returns the code worth showing now, minting one if the last has run out.</summary>
    Task<InvitationCode> RenewAsync(CancellationToken cancellationToken);

    /// <summary>Mints one more invitation and starts offering it.</summary>
    Task<LinkInvitation> InviteAsync(InvitationRequest request, CancellationToken cancellationToken);

    /// <summary>Withdraws one invitation.</summary>
    Task<bool> RevokeAsync(Guid invitationId, CancellationToken cancellationToken);
}

/// <summary>
/// The hosting end's codes: one address that never moves, and as many tokens
/// as there are devices still to arrive.
/// </summary>
/// <remarks>
/// A host that has been running longer than its pairing window has a code
/// that its own policy would refuse, and no operator standing next to it to
/// notice. This is what lets the application see that — through
/// <see cref="ExpiresAt"/> — and mint a code it can publish again, without
/// restarting the process.
/// </remarks>
/// <param name="pairing">Where offers are minted and remembered.</param>
/// <param name="address">This machine's pinned address, the public half of every code.</param>
/// <param name="window">How long a freshly minted offer is good for by default.</param>
internal sealed class InvitationBook(PairingRecord pairing, ConnBlob address, TimeSpan window) : IInvitationSource
{
    /// <inheritdoc/>
    public InvitationCode Current =>
        pairing.State.Pairing is { } offer ? InvitationCode.ForAddress(address, offer.Token) : default;

    /// <inheritdoc/>
    public DateTimeOffset? ExpiresAt
    {
        get
        {
            // A host with no room left admits its peers by key, so its codes
            // have no window left to run out: there is nothing more they can
            // buy.
            if (pairing.Peers.Count >= pairing.MaxPeers)
            {
                return null;
            }
            return pairing.State.Pairing?.ExpiresAt;
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Expired offers are left out here even though they are still stored:
    /// they are pruned only when the next one is minted, and an operator
    /// reading this list would otherwise be shown a code to hand out that
    /// pairing will refuse.
    /// </remarks>
    public IReadOnlyList<LinkInvitation> All =>
    [
        .. pairing.State.Pairings.Where(offer => !offer.HasExpired(pairing.Time)).Select(AsInvitation),
    ];

    /// <inheritdoc/>
    public async Task<InvitationCode> RenewAsync(CancellationToken cancellationToken)
    {
        // Minting only when the last has run out is what keeps an application
        // that renews on a timer from invalidating a code somebody is at that
        // moment reading off the screen.
        PairingOffer renewed = await pairing.OfferPairingAsync(window, cancellationToken).ConfigureAwait(false);
        return InvitationCode.ForAddress(address, renewed.Token);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// A full host refuses rather than minting: the code would be one nothing
    /// can pair with, since admission turns a stranger away on the peer limit
    /// before it ever looks at a token. The operator would be told so by
    /// neither end — the device just hears "this machine is not open to you" —
    /// so it is said here, where there is somebody to hear it and an obvious
    /// cure in <see cref="ILinkHost.ForgetPeerAsync"/>.
    /// </remarks>
    public async Task<LinkInvitation> InviteAsync(InvitationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (pairing.Peers.Count >= pairing.MaxPeers)
        {
            throw new LinkException(
                $"this machine is already paired with {pairing.Peers.Count} of {pairing.MaxPeers} machines; "
                + "forget one before inviting another");
        }
        PairingOffer offer = await pairing.AddOfferAsync(
                PairingOffer.New(request.Lifetime ?? window, pairing.Time, request.Label, request.SingleUse),
                cancellationToken)
            .ConfigureAwait(false);
        return AsInvitation(offer);
    }

    /// <inheritdoc/>
    public Task<bool> RevokeAsync(Guid invitationId, CancellationToken cancellationToken) =>
        pairing.RevokeOfferAsync(invitationId, cancellationToken);

    private LinkInvitation AsInvitation(PairingOffer offer) => new(
        offer.Id,
        InvitationCode.ForAddress(address, offer.Token),
        offer.ExpiresAt,
        offer.Label,
        offer.SingleUse);
}

/// <summary>The joining end's code: the host's, which this machine cannot mint.</summary>
/// <remarks>
/// It is kept so that an application can show what this machine is paired
/// with. Renewing it is a no-op rather than an error, because the caller has
/// asked for the code worth showing now and that is still this one — only the
/// host can mint another.
/// </remarks>
internal sealed class JoinedInvitation(InvitationCode code) : IInvitationSource
{
    /// <inheritdoc/>
    public InvitationCode Current => code;

    /// <inheritdoc/>
    public DateTimeOffset? ExpiresAt => null;

    /// <inheritdoc/>
    public IReadOnlyList<LinkInvitation> All => [];

    /// <inheritdoc/>
    public Task<InvitationCode> RenewAsync(CancellationToken cancellationToken) => Task.FromResult(code);

    /// <inheritdoc/>
    public Task<LinkInvitation> InviteAsync(InvitationRequest request, CancellationToken cancellationToken) =>
        throw new LinkException("this machine joined a host; only a host can invite");

    /// <inheritdoc/>
    public Task<bool> RevokeAsync(Guid invitationId, CancellationToken cancellationToken) =>
        Task.FromResult(false);
}

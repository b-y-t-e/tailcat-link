// Copyright (c) Tailscale Inc & contributors
// SPDX-License-Identifier: BSD-3-Clause

using Tailcat.Link.Storage;

namespace Tailcat.Link.Protocol;

/// <summary>
/// The code a link is currently worth showing, and whether it can still be
/// used to pair.
/// </summary>
/// <remarks>
/// It is an abstraction for the same reason <see cref="ISessionSource"/> is:
/// the two ends of a link answer "what is my code?" differently, and
/// <see cref="DurableLink"/> should not have to know which end it is on.
/// </remarks>
internal interface IInvitationSource
{
    /// <summary>The code as it stands, which a renewal may replace.</summary>
    InvitationCode Current { get; }

    /// <summary>
    /// When <see cref="Current"/> stops being able to pair, or null when
    /// nothing is waiting to be paired.
    /// </summary>
    DateTimeOffset? ExpiresAt { get; }

    /// <summary>Returns the code worth showing now, minting one if the last has run out.</summary>
    Task<InvitationCode> RenewAsync(CancellationToken cancellationToken);
}

/// <summary>The hosting end's code: an address that never moves, and a token that expires.</summary>
/// <remarks>
/// A host that has been running longer than its pairing window has a code
/// that its own policy would refuse, and no operator standing next to it to
/// notice. This is what lets the application see that — through
/// <see cref="ExpiresAt"/> — and mint a code it can publish again, without
/// restarting the process.
/// </remarks>
/// <param name="pairing">Where offers are minted and remembered.</param>
/// <param name="address">This machine's pinned address, the public half of the code.</param>
/// <param name="offer">The offer this host started with.</param>
/// <param name="window">How long a freshly minted offer is good for.</param>
internal sealed class HostInvitation(
    PairingRecord pairing,
    ConnBlob address,
    PairingOffer offer,
    TimeSpan window) : IInvitationSource
{
    private readonly Lock _mu = new();
    private PairingOffer _offer = offer;
    private InvitationCode _current = InvitationCode.ForAddress(address, offer.Token);

    /// <inheritdoc/>
    public InvitationCode Current
    {
        get
        {
            lock (_mu)
            {
                return _current;
            }
        }
    }

    /// <inheritdoc/>
    public DateTimeOffset? ExpiresAt
    {
        get
        {
            // A paired host admits its peer by key, so its code has no window
            // left to run out: there is nothing more it can buy.
            if (!pairing.Peer.IsZero)
            {
                return null;
            }
            lock (_mu)
            {
                return _offer.ExpiresAt;
            }
        }
    }

    /// <inheritdoc/>
    public async Task<InvitationCode> RenewAsync(CancellationToken cancellationToken)
    {
        // Minting only when the last has run out is what keeps an application
        // that renews on a timer from invalidating a code somebody is at that
        // moment reading off the screen.
        PairingOffer renewed = await pairing.OfferPairingAsync(window, cancellationToken).ConfigureAwait(false);
        lock (_mu)
        {
            _offer = renewed;
            _current = InvitationCode.ForAddress(address, renewed.Token);
            return _current;
        }
    }
}

/// <summary>The joining end's code: the host's, which this machine cannot renew.</summary>
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
    public Task<InvitationCode> RenewAsync(CancellationToken cancellationToken) => Task.FromResult(code);
}

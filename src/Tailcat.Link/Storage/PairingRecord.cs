// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using Tailcat.Keys;
using Tailcat.Link.Protocol;

namespace Tailcat.Link.Storage;

/// <summary>
/// The stored state while a link is running: the one place that knows what
/// this machine remembers, and the only place that writes it down.
/// </summary>
/// <remarks>
/// It exists so that neither the link nor the thing accepting sessions has to
/// hold a copy of the state and keep it in step with the disk. They ask this,
/// and this decides whether anything needs saving.
/// </remarks>
/// <param name="appName">The application these settings belong to.</param>
/// <param name="state">What was loaded from the store.</param>
/// <param name="store">Where changes are written.</param>
/// <param name="time">The clock expiry and "last seen" are measured on.</param>
/// <param name="maxPeers">
/// How many machines may be paired at once. It is a security bound rather
/// than a resource one: every admitted peer can reach the application's
/// handler.
/// </param>
internal sealed class PairingRecord(
    string appName,
    LinkState state,
    ILinkStore store,
    TimeProvider time,
    int maxPeers = 1)
    : IPairingPolicy
{
    private readonly Lock _mu = new();
    private LinkState _state = state;
    private Task _lastWrite = Task.CompletedTask;

    /// <summary>The application these settings belong to.</summary>
    public string AppName => appName;

    /// <summary>How many machines may be paired at once.</summary>
    public int MaxPeers => maxPeers;

    /// <summary>The clock expiry and "last seen" are measured on.</summary>
    public TimeProvider Time => time;

    /// <summary>The state as it currently stands.</summary>
    public LinkState State
    {
        get
        {
            lock (_mu)
            {
                return _state;
            }
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<PairedPeer> Peers => State.Peers;

    /// <summary>
    /// Remembers <paramref name="peer"/> as a machine at the other end,
    /// refreshing when it was last seen if it is already known.
    /// </summary>
    /// <remarks>
    /// This is what the joining end calls: it dialled a host it already
    /// trusts, so there is no invitation to check. A host reaches its peers
    /// through <see cref="AdmitAsync"/> instead.
    /// </remarks>
    public Task PairWithAsync(NodePublic peer, string? name = null, CancellationToken cancellationToken = default) =>
        UpdateAsync(current => WithPeer(current, peer, name), cancellationToken);

    /// <summary>
    /// Returns the offer this host should publish, minting a fresh one when
    /// the last has run out.
    /// </summary>
    /// <remarks>
    /// A host that is restarted inside the window keeps showing the same
    /// code, because the operator may already have written it down; one
    /// restarted after the window shows a new one, which is the only way to
    /// re-open pairing without a way to reach the machine. That holds while
    /// the host still has room for another machine; a full one goes on
    /// showing the code it published, spent as it is.
    /// </remarks>
    public async Task<PairingOffer> OfferPairingAsync(TimeSpan window, CancellationToken cancellationToken = default)
    {
        LinkState current = State;
        // A host with no room left has an offer that is spent, but it is
        // still what its published code says, so it is shown rather than
        // replaced. Room left is the other case: an expired offer there would
        // publish a code that pairing then refuses, which is exactly what the
        // machine has no way to correct.
        bool full = current.Peers.Count >= maxPeers;
        if (current.Pairing is { } offer && (full || !offer.HasExpired(time)))
        {
            return offer;
        }
        return await AddOfferAsync(PairingOffer.New(window, time), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Adds an offer to the ones this host is making, and remembers it.</summary>
    public async Task<PairingOffer> AddOfferAsync(PairingOffer offer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(offer);
        await UpdateAsync(
                current => current with { Pairings = [.. Live(current.Pairings), offer] }, cancellationToken)
            .ConfigureAwait(false);
        return offer;
    }

    /// <summary>Withdraws one offer, whether or not it had expired.</summary>
    /// <returns>Whether an offer with that id was still there.</returns>
    public async Task<bool> RevokeOfferAsync(Guid offerId, CancellationToken cancellationToken = default)
    {
        bool found = false;
        await UpdateAsync(
            current =>
            {
                found = current.Pairings.Any(offer => offer.Id == offerId);
                return found
                    ? current with { Pairings = [.. current.Pairings.Where(offer => offer.Id != offerId)] }
                    : null;
            },
            cancellationToken).ConfigureAwait(false);
        return found;
    }

    /// <summary>Unpairs one machine, leaving this machine's identity and its other peers alone.</summary>
    /// <remarks>
    /// The invitation it was admitted by goes with it. Forgetting a machine
    /// that still holds a live code would otherwise last only until its next
    /// reconnection, which is seconds: it arrives as a stranger with a valid
    /// token and is admitted again. A reusable offer is withdrawn for
    /// everyone it was shown to, because letting the forgotten machine back
    /// in is the worse of the two costs — the operator can invite again.
    /// </remarks>
    /// <returns>Whether that machine was paired.</returns>
    public async Task<bool> ForgetPeerAsync(NodePublic peer, CancellationToken cancellationToken = default)
    {
        bool found = false;
        await UpdateAsync(
            current =>
            {
                if (current.PeerWith(peer) is not { } known)
                {
                    found = false;
                    return null;
                }
                found = true;
                return current with
                {
                    Peers = [.. current.Peers.Where(other => other.Key != peer)],
                    Pairings = [.. current.Pairings.Where(offer => offer.Id != known.InvitationId)],
                };
            },
            cancellationToken).ConfigureAwait(false);
        return found;
    }

    /// <summary>
    /// Unpairs whatever a lowered <see cref="MaxPeers"/> no longer has room
    /// for, keeping the machines seen most recently.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The bound is a security one, so a store written under a larger one
    /// cannot simply be honoured: every peer in it comes back up able to
    /// reach the application's handler. Dropping the least recently seen is
    /// the choice that costs an operator least — the devices still in use are
    /// the ones that stay.
    /// </para>
    /// <para>
    /// The invitations the dropped machines were admitted by go with them,
    /// for the reason <see cref="ForgetPeerAsync"/> withdraws one: a code
    /// still in a dropped machine's hands is a way straight back in as soon
    /// as unpairing another device makes room.
    /// </para>
    /// </remarks>
    /// <returns>The machines that were unpaired, in the order they were stored.</returns>
    public async Task<IReadOnlyList<PairedPeer>> EnforcePeerLimitAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<PairedPeer> dropped = [];
        await UpdateAsync(
            current =>
            {
                if (current.Peers.Count <= maxPeers)
                {
                    return null;
                }

                HashSet<NodePublic> kept =
                [
                    .. current.Peers.OrderByDescending(peer => peer.LastSeen).Take(maxPeers).Select(peer => peer.Key),
                ];
                dropped = [.. current.Peers.Where(peer => !kept.Contains(peer.Key))];
                HashSet<Guid> withdrawn =
                [
                    .. dropped.Select(peer => peer.InvitationId).OfType<Guid>(),
                ];
                return current with
                {
                    Peers = [.. current.Peers.Where(peer => kept.Contains(peer.Key))],
                    Pairings = [.. current.Pairings.Where(offer => !withdrawn.Contains(offer.Id))],
                };
            },
            cancellationToken).ConfigureAwait(false);
        return dropped;
    }

    /// <inheritdoc/>
    public async Task<PairedPeer?> AdmitAsync(
        NodePublic candidate,
        LinkHello hello,
        CancellationToken cancellationToken)
    {
        PairedPeer? admitted = null;
        bool lapsed = false;
        // The decision is made inside the update so that it and the pinning
        // are one step: two strangers arriving at once must not both be told
        // yes, with only the first of them written down.
        await UpdateAsync(
            current =>
            {
                admitted = null;
                lapsed = false;
                bool known = current.PeerWith(candidate) is not null;
                if (!known
                    && (current.Peers.Count >= maxPeers || Invitation(current, hello.PairingToken) is null))
                {
                    lapsed = current.Peers.Count < maxPeers && Lapsed(current, hello.PairingToken);
                    return null;
                }

                PairingOffer? admittedBy = known ? null : Invitation(current, hello.PairingToken);
                LinkState paired =
                    WithPeer(current, candidate, hello.DisplayName, admittedBy?.Id) ?? current;
                admitted = paired.PeerWith(candidate);
                PairingOffer? spent = admittedBy is { SingleUse: true } ? admittedBy : null;
                if (spent is null)
                {
                    return ReferenceEquals(paired, current) ? null : paired;
                }
                return paired with { Pairings = [.. paired.Pairings.Where(offer => offer.Id != spent.Id)] };
            },
            cancellationToken).ConfigureAwait(false);

        // Thrown rather than returned, and only after the decision is
        // written: the machine outside is told nothing but "no", while this
        // machine's operator gets the one refusal that has an obvious cure —
        // a fresh invitation — instead of hunting for a wrong token or a host
        // that filled up.
        if (lapsed)
        {
            throw new InvitationExpiredException(
                $"{candidate} presented an invitation whose window has closed; invite it again");
        }
        return admitted;
    }

    /// <summary>Records the region a host settled in, fixing its address for good.</summary>
    public Task RememberHomeRegionAsync(int regionId, CancellationToken cancellationToken = default) =>
        UpdateAsync(
            current => current.HomeRegionId == regionId ? null : current with { HomeRegionId = regionId },
            cancellationToken);

    /// <summary>
    /// Records who this machine joined, and the code it used, so joining
    /// again needs no code.
    /// </summary>
    /// <remarks>
    /// A code that differs from the stored one replaces the pairing outright:
    /// being handed a new code is how someone says "pair with that machine
    /// instead", and refusing would leave them with no way to say it.
    /// </remarks>
    public Task JoinPeerAsync(InvitationCode code, NodePublic host, CancellationToken cancellationToken = default) =>
        UpdateAsync(
            current => current.PeerCode == code && current.PeerKey == host
                ? null
                : WithPeer(current with { PeerCode = code, Peers = [] }, host, null),
            cancellationToken);

    private PairingOffer? Invitation(LinkState current, string token) =>
        current.Pairings.FirstOrDefault(offer => !offer.HasExpired(time) && offer.Matches(token));

    // The invitation this token names, expired: the difference between a
    // machine that was invited and left it too long and one that was never
    // invited at all.
    private bool Lapsed(LinkState current, string token) =>
        current.Pairings.Any(offer => offer.HasExpired(time) && offer.Matches(token));

    // Offers that ran out are dropped, except the newest, which is what the
    // machine's published code is made of and is still worth showing.
    private IEnumerable<PairingOffer> Live(IReadOnlyList<PairingOffer> offers) =>
        offers.Where((offer, index) => index == offers.Count - 1 || !offer.HasExpired(time));

    private LinkState? WithPeer(LinkState current, NodePublic key, string? name, Guid? invitationId = null)
    {
        DateTimeOffset now = time.GetUtcNow();
        if (current.PeerWith(key) is not { } known)
        {
            return current with
            {
                Peers = [.. current.Peers, new PairedPeer(key, name, now, now) { InvitationId = invitationId }],
            };
        }

        PairedPeer refreshed = known with { Name = name ?? known.Name, LastSeen = now };
        return refreshed == known
            ? null
            : current with { Peers = [.. current.Peers.Select(peer => peer.Key == key ? refreshed : peer)] };
    }

    // Writing only on a real change keeps a link that reconnects every few
    // minutes for a week from rewriting the same file every time.
    private Task UpdateAsync(Func<LinkState, LinkState?> change, CancellationToken cancellationToken)
    {
        lock (_mu)
        {
            if (change(_state) is not LinkState next)
            {
                return Task.CompletedTask;
            }
            _state = next;
            // The write is queued behind the previous one while the change is
            // still made under the lock, so the file ends up in the order the
            // states were decided in. Two changes at once — a renewed
            // invitation and a peer arriving — must not race to the disk and
            // leave the machine remembering the older of the two after a
            // restart, which is the pairing lost.
            _lastWrite = SaveAfterAsync(_lastWrite, next, cancellationToken);
            return _lastWrite;
        }
    }

    private async Task SaveAfterAsync(Task earlier, LinkState updated, CancellationToken cancellationToken)
    {
        try
        {
            await earlier.ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Whoever asked for the earlier write is the one told it failed; this one still has to happen, or the newer state is lost with it.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
        await store.SaveAsync(appName, updated, cancellationToken).ConfigureAwait(false);
    }
}

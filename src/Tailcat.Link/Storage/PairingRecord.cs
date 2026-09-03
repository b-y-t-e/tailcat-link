// Copyright (c) Tailscale Inc & contributors
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
internal sealed class PairingRecord(string appName, LinkState state, ILinkStore store, TimeProvider time)
    : IPairingPolicy
{
    private readonly Lock _mu = new();
    private LinkState _state = state;
    private Task _lastWrite = Task.CompletedTask;

    /// <summary>The application these settings belong to.</summary>
    public string AppName => appName;

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

    /// <summary>The machine this one is paired with, or the zero key.</summary>
    public NodePublic Peer => State.PeerKey;

    /// <summary>
    /// Pins <paramref name="peer"/> as the machine at the other end, unless
    /// one is already pinned.
    /// </summary>
    public Task PairWithAsync(NodePublic peer, CancellationToken cancellationToken = default) =>
        UpdateAsync(current => current.IsPaired ? null : current with { PeerKey = peer }, cancellationToken);

    /// <summary>
    /// Returns the offer this host should publish, minting a fresh one when
    /// the last has run out.
    /// </summary>
    /// <remarks>
    /// A host that is restarted inside the window keeps showing the same
    /// code, because the operator may already have written it down; one
    /// restarted after the window shows a new one, which is the only way to
    /// re-open pairing without a way to reach the machine.
    /// </remarks>
    public async Task<PairingOffer> OfferPairingAsync(TimeSpan window, CancellationToken cancellationToken = default)
    {
        LinkState current = State;
        // A paired host's offer is spent, but it is still what its code says,
        // so it is shown rather than replaced.
        if (current.Pairing is { } offer && (current.IsPaired || !offer.HasExpired(time)))
        {
            return offer;
        }

        PairingOffer fresh = PairingOffer.New(window, time);
        await UpdateAsync(c => c with { Pairing = fresh }, cancellationToken).ConfigureAwait(false);
        return fresh;
    }

    /// <inheritdoc/>
    public async Task<bool> AdmitAsync(
        NodePublic candidate,
        string pairingToken,
        CancellationToken cancellationToken)
    {
        bool admitted = false;
        // The decision is made inside the update so that it and the pinning
        // are one step: two strangers arriving at once must not both be told
        // yes, with only the first of them written down.
        await UpdateAsync(
            current =>
            {
                admitted = Admits(current, candidate, pairingToken);
                return admitted && !current.IsPaired ? current with { PeerKey = candidate } : null;
            },
            cancellationToken).ConfigureAwait(false);
        return admitted;
    }

    private bool Admits(LinkState current, NodePublic candidate, string pairingToken) =>
        current.IsPaired
            ? current.PeerKey == candidate
            : current.Pairing is { } offer && !offer.HasExpired(time) && offer.Matches(pairingToken);

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
                : current with { PeerCode = code, PeerKey = host },
            cancellationToken);

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

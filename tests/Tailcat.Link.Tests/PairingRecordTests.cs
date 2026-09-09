// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using Tailcat.Keys;
using Tailcat.Link.Protocol;
using Tailcat.Link.Storage;

namespace Tailcat.Link.Tests;

/// <summary>
/// Covers who a host lets in, which is the whole of what stands between an
/// unclaimed machine and whoever learned its address — the operator of the
/// relay it is connected to, for one, who sees it as a matter of course.
/// </summary>
public class PairingRecordTests
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);

    private PairingRecord Host() => new(
        "demo",
        new LinkState { PrivateKey = NodePrivate.NewKey() },
        new InMemoryLinkStore(),
        _clock);

    /// <summary>
    /// A host restarted inside the window shows the code it showed before:
    /// the operator may already have written it down, and there is nobody at
    /// the machine to read out a new one.
    /// </summary>
    [Fact]
    public async Task AnOfferSurvivesAsLongAsItsWindow()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        PairingRecord host = Host();

        PairingOffer first = await host.OfferPairingAsync(Window, ct);
        _clock.Advance(Window / 2);
        PairingOffer again = await host.OfferPairingAsync(Window, ct);

        Assert.Equal(first, again);
    }

    /// <summary>
    /// Once the window has closed the invitation is replaced rather than
    /// renewed, so an unclaimed machine is not left open indefinitely.
    /// </summary>
    [Fact]
    public async Task AnExpiredOfferIsReplacedByAFreshOne()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        PairingRecord host = Host();

        PairingOffer first = await host.OfferPairingAsync(Window, ct);
        _clock.Advance(Window + TimeSpan.FromSeconds(1));
        PairingOffer second = await host.OfferPairingAsync(Window, ct);

        Assert.NotEqual(first.Token, second.Token);
        await Assert.ThrowsAsync<InvitationExpiredException>(
            () => host.AdmitAsync(NodePrivate.NewKey().Public(), new LinkHello(first.Token, null), ct));
    }

    /// <summary>The machine holding the invitation pairs; anyone else does not.</summary>
    [Fact]
    public async Task OnlyTheMachineWithTheTokenPairs()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        PairingRecord host = Host();
        PairingOffer offer = await host.OfferPairingAsync(Window, ct);
        NodePublic stranger = NodePrivate.NewKey().Public();
        NodePublic invited = NodePrivate.NewKey().Public();

        Assert.Null(await host.AdmitAsync(stranger, new LinkHello("guessed", null), ct));
        Assert.False(host.State.IsPaired);

        Assert.NotNull(await host.AdmitAsync(invited, new LinkHello(offer.Token, "the invited one"), ct));
        Assert.Equal(invited, host.State.PeerKey);
        // What the machine called itself is remembered, unauthenticated as it is.
        Assert.Equal("the invited one", Assert.Single(host.Peers).Name);
    }

    /// <summary>
    /// A code that leaks after the pairing buys nothing: the host is looking
    /// for one machine now, and the token is no longer part of the question.
    /// </summary>
    [Fact]
    public async Task OnceAHostIsPairedTheCodeIsSpent()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        PairingRecord host = Host();
        PairingOffer offer = await host.OfferPairingAsync(Window, ct);
        NodePublic peer = NodePrivate.NewKey().Public();
        Assert.NotNull(await host.AdmitAsync(peer, new LinkHello(offer.Token, null), ct));

        Assert.Null(await host.AdmitAsync(NodePrivate.NewKey().Public(), new LinkHello(offer.Token, null), ct));
        Assert.NotNull(await host.AdmitAsync(peer, new LinkHello("the peer no longer needs it", null), ct));
        Assert.Equal(peer, host.State.PeerKey);
    }

    /// <summary>
    /// An invitation that lapsed while the host was up pairs with nobody:
    /// expiry is checked when a machine arrives, not only when it is minted.
    /// It is the one refusal the operator is told apart from the rest, because
    /// it is the one with an obvious cure.
    /// </summary>
    [Fact]
    public async Task AnInvitationThatLapsedWhileWaitingPairsWithNobody()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        PairingRecord host = Host();
        PairingOffer offer = await host.OfferPairingAsync(Window, ct);

        _clock.Advance(Window + TimeSpan.FromSeconds(1));

        await Assert.ThrowsAsync<InvitationExpiredException>(
            () => host.AdmitAsync(NodePrivate.NewKey().Public(), new LinkHello(offer.Token, null), ct));
        Assert.False(host.State.IsPaired);
    }

    /// <summary>
    /// A token that was never on offer is refused without a word about why:
    /// only a machine that really was invited is told apart, and only to this
    /// host's own operator.
    /// </summary>
    [Fact]
    public async Task ATokenThatWasNeverOnOfferIsRefusedWithoutBeingExplained()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        PairingRecord host = Host();
        await host.OfferPairingAsync(Window, ct);

        _clock.Advance(Window + TimeSpan.FromSeconds(1));

        Assert.Null(await host.AdmitAsync(NodePrivate.NewKey().Public(), new LinkHello("guessed", null), ct));
    }

    /// <summary>
    /// A host with room for more machines mints a fresh invitation once the
    /// last one lapsed, even with a machine already paired. Showing the
    /// expired one would publish a code that pairing then refuses, and the
    /// operator holding it has no way to tell.
    /// </summary>
    [Fact]
    public async Task AHostWithRoomLeftReplacesAnOfferThatLapsedWhilePaired()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        PairingRecord host = new(
            "demo", new LinkState { PrivateKey = NodePrivate.NewKey() }, new InMemoryLinkStore(), _clock, maxPeers: 4);
        PairingOffer first = await host.OfferPairingAsync(Window, ct);
        Assert.NotNull(await host.AdmitAsync(NodePrivate.NewKey().Public(), new LinkHello(first.Token, null), ct));

        _clock.Advance(Window + TimeSpan.FromSeconds(1));
        PairingOffer second = await host.OfferPairingAsync(Window, ct);

        Assert.NotEqual(first.Token, second.Token);
        Assert.NotNull(await host.AdmitAsync(NodePrivate.NewKey().Public(), new LinkHello(second.Token, null), ct));
    }

    /// <summary>
    /// A host with no room left goes on showing the code it published, spent
    /// as that code is: it is what the operator wrote down, and there is
    /// nothing at the machine to read a new one out.
    /// </summary>
    [Fact]
    public async Task AFullHostKeepsShowingTheCodeItPublished()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        PairingRecord host = Host();
        PairingOffer offer = await host.OfferPairingAsync(Window, ct);
        Assert.NotNull(await host.AdmitAsync(NodePrivate.NewKey().Public(), new LinkHello(offer.Token, null), ct));

        _clock.Advance(Window + TimeSpan.FromSeconds(1));

        Assert.Equal(offer, await host.OfferPairingAsync(Window, ct));
    }

    /// <summary>
    /// A bound that was lowered reaches the machines already written down.
    /// Left there, each of them would come back up able to reach the
    /// application's handler under a bound the application believes it has
    /// narrowed — and the ones still in use are the ones worth keeping.
    /// </summary>
    [Fact]
    public async Task LoweringTheBoundUnpairsTheMachinesSeenLongestAgo()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        InMemoryLinkStore store = new();
        PairingRecord roomForThree = new(
            "demo", new LinkState { PrivateKey = NodePrivate.NewKey() }, store, _clock, maxPeers: 3);
        PairingOffer offer = await roomForThree.OfferPairingAsync(Window, ct);

        List<NodePublic> arrivals = [];
        foreach (int _ in Enumerable.Range(0, 3))
        {
            NodePublic peer = NodePrivate.NewKey().Public();
            Assert.NotNull(await roomForThree.AdmitAsync(peer, new LinkHello(offer.Token, null), ct));
            arrivals.Add(peer);
            _clock.Advance(TimeSpan.FromMinutes(1));
        }

        PairingRecord roomForOne = new(
            "demo",
            await store.LoadAsync("demo", ct) ?? throw new InvalidOperationException("nothing was stored"),
            store,
            _clock,
            maxPeers: 1);
        IReadOnlyList<PairedPeer> dropped = await roomForOne.EnforcePeerLimitAsync(ct);

        Assert.Equal(arrivals[..2], [.. dropped.Select(peer => peer.Key)]);
        Assert.Equal(arrivals[2], Assert.Single(roomForOne.Peers).Key);
        // Written down rather than merely forgotten in memory: the next start
        // reads the file, and would let them all back in.
        LinkState stored = await store.LoadAsync("demo", ct) ?? throw new InvalidOperationException("nothing was stored");
        Assert.Equal(arrivals[2], Assert.Single(stored.Peers).Key);
    }

    /// <summary>A store the bound still has room for is left exactly as it is.</summary>
    [Fact]
    public async Task ABoundWithRoomToSpareUnpairsNobody()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        PairingRecord host = Host();
        PairingOffer offer = await host.OfferPairingAsync(Window, ct);
        NodePublic peer = NodePrivate.NewKey().Public();
        Assert.NotNull(await host.AdmitAsync(peer, new LinkHello(offer.Token, null), ct));

        Assert.Empty(await host.EnforcePeerLimitAsync(ct));
        Assert.Equal(peer, Assert.Single(host.Peers).Key);
    }

    /// <summary>
    /// Unpairing a machine withdraws the invitation it came in on, so it is
    /// refused when it reconnects a second later with the code it still
    /// holds. Without that, an operator removing a stolen phone gets it back
    /// on the list until the window closes.
    /// </summary>
    [Fact]
    public async Task AForgottenMachineCannotWalkBackInOnTheSameInvitation()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        PairingRecord host = new(
            "demo", new LinkState { PrivateKey = NodePrivate.NewKey() }, new InMemoryLinkStore(), _clock, maxPeers: 2);
        PairingOffer offer = await host.OfferPairingAsync(Window, ct);
        NodePublic phone = NodePrivate.NewKey().Public();
        Assert.NotNull(await host.AdmitAsync(phone, new LinkHello(offer.Token, "stolen phone"), ct));

        Assert.True(await host.ForgetPeerAsync(phone, ct));

        Assert.Null(await host.AdmitAsync(phone, new LinkHello(offer.Token, "stolen phone"), ct));
        Assert.Empty(host.State.Peers);
        // Withdrawn rather than merely unmatched: nothing else may pair on it
        // either, since a reusable code the operator has lost faith in is
        // exactly the one to stop showing.
        Assert.Null(await host.AdmitAsync(NodePrivate.NewKey().Public(), new LinkHello(offer.Token, null), ct));
    }

    /// <summary>
    /// Unpairing one machine leaves the invitation another came in on alone,
    /// which is what makes a device list usable: removing one phone must not
    /// turn away the code a second was invited with.
    /// </summary>
    [Fact]
    public async Task ForgettingOneMachineLeavesAnotherInvitationLive()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        PairingRecord host = new(
            "demo", new LinkState { PrivateKey = NodePrivate.NewKey() }, new InMemoryLinkStore(), _clock, maxPeers: 3);
        PairingOffer kitchen = await host.AddOfferAsync(PairingOffer.New(Window, _clock, "kitchen", false), ct);
        PairingOffer hallway = await host.AddOfferAsync(PairingOffer.New(Window, _clock, "hallway", false), ct);
        NodePublic phone = NodePrivate.NewKey().Public();
        Assert.NotNull(await host.AdmitAsync(phone, new LinkHello(kitchen.Token, "kitchen phone"), ct));

        Assert.True(await host.ForgetPeerAsync(phone, ct));

        Assert.NotNull(
            await host.AdmitAsync(NodePrivate.NewKey().Public(), new LinkHello(hallway.Token, "hallway phone"), ct));
    }

    /// <summary>
    /// A machine a lowered bound unpairs loses its invitation with it. Left
    /// live, the code it still holds walks it straight back in as soon as
    /// unpairing another device makes room — an unpairing undone by an
    /// unrelated one.
    /// </summary>
    [Fact]
    public async Task LoweringTheBoundWithdrawsTheDroppedMachinesInvitations()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        InMemoryLinkStore store = new();
        PairingRecord roomForTwo = new(
            "demo", new LinkState { PrivateKey = NodePrivate.NewKey() }, store, _clock, maxPeers: 2);
        PairingOffer kitchen = await roomForTwo.AddOfferAsync(PairingOffer.New(Window, _clock, "kitchen", false), ct);
        PairingOffer hallway = await roomForTwo.AddOfferAsync(PairingOffer.New(Window, _clock, "hallway", false), ct);
        NodePublic kitchenPhone = NodePrivate.NewKey().Public();
        Assert.NotNull(await roomForTwo.AdmitAsync(kitchenPhone, new LinkHello(kitchen.Token, "kitchen"), ct));
        _clock.Advance(TimeSpan.FromMinutes(1));
        NodePublic hallwayPhone = NodePrivate.NewKey().Public();
        Assert.NotNull(await roomForTwo.AdmitAsync(hallwayPhone, new LinkHello(hallway.Token, "hallway"), ct));

        PairingRecord roomForOne = new(
            "demo",
            await store.LoadAsync("demo", ct) ?? throw new InvalidOperationException("nothing was stored"),
            store,
            _clock,
            maxPeers: 1);
        Assert.Equal(kitchenPhone, Assert.Single(await roomForOne.EnforcePeerLimitAsync(ct)).Key);

        // The bound has room again, and the dropped machine still has its
        // code: this is the moment the invitation would have paid for.
        Assert.True(await roomForOne.ForgetPeerAsync(hallwayPhone, ct));
        Assert.Null(await roomForOne.AdmitAsync(kitchenPhone, new LinkHello(kitchen.Token, "kitchen"), ct));
        Assert.Empty(roomForOne.Peers);
    }

    /// <summary>
    /// An invitation whose window has closed is not listed as one that can
    /// still be used. It is only pruned when the next is minted, so a host
    /// that has minted none since would otherwise offer an operator a code
    /// its own pairing refuses.
    /// </summary>
    [Fact]
    public async Task AnExpiredInvitationIsNotListedAsALiveOne()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        PairingRecord host = Host();
        InvitationBook invitations =
            new(host, new ConnBlob("tcomFwWCCcjS5nKNqAod034nWoJZW0LZqDhhC8U_dKdnDRYQ8uNGFpGQEu"), Window);

        LinkInvitation minted = await invitations.InviteAsync(new InvitationRequest { Label = "kitchen" }, ct);
        Assert.Equal(minted.Id, Assert.Single(invitations.All).Id);

        _clock.Advance(Window + TimeSpan.FromSeconds(1));

        Assert.Empty(invitations.All);
    }

    /// <summary>
    /// Two changes made at once reach the store in the order they were
    /// decided in, one after the other. Left to race, a renewed invitation
    /// and a peer arriving could land on the disk the wrong way round and a
    /// restart would bring back the machine that has no pairing — the one
    /// failure this layer exists to prevent.
    /// </summary>
    [Fact]
    public async Task ChangesAreWrittenOneAtATimeInTheOrderTheyWereMade()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        StallingLinkStore store = new();
        PairingRecord host = new("demo", new LinkState { PrivateKey = NodePrivate.NewKey() }, store, _clock);
        NodePublic peer = NodePrivate.NewKey().Public();

        Task offering = host.OfferPairingAsync(Window, ct);
        await store.FirstWriteStarted;
        Task pairing = host.PairWithAsync(peer, cancellationToken: ct);

        // The second change is already made in memory, and must be waiting
        // its turn rather than overtaking the first on the way to the disk.
        Assert.Equal(peer, host.State.PeerKey);
        Assert.Empty(store.Written);

        store.LetGo();
        await Task.WhenAll(offering, pairing);

        Assert.Collection(
            store.Written,
            first => Assert.False(first.IsPaired),
            second => Assert.Equal(peer, second.PeerKey));
    }

    /// <summary>
    /// A store whose first write hangs until it is let go, which is what a
    /// slow disk looks like from here.
    /// </summary>
    private sealed class StallingLinkStore : ILinkStore
    {
        private readonly TaskCompletionSource _started = new();
        private readonly TaskCompletionSource _release = new();
        private readonly List<LinkState> _written = [];

        /// <summary>Completes once the store has been asked to write anything.</summary>
        public Task FirstWriteStarted => _started.Task;

        /// <summary>What has actually been written, in the order it was written.</summary>
        public IReadOnlyList<LinkState> Written
        {
            get
            {
                lock (_written)
                {
                    return [.. _written];
                }
            }
        }

        /// <summary>Lets the stalled write finish.</summary>
        public void LetGo() => _release.TrySetResult();

        public Task<LinkState?> LoadAsync(string appName, CancellationToken cancellationToken = default) =>
            Task.FromResult<LinkState?>(null);

        public async Task SaveAsync(string appName, LinkState state, CancellationToken cancellationToken = default)
        {
            _started.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            lock (_written)
            {
                _written.Add(state);
            }
        }

        public Task DeleteAsync(string appName, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}

// Copyright (c) Tailscale Inc & contributors
// SPDX-License-Identifier: BSD-3-Clause

using Tailcat.Keys;
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
        Assert.False(await host.AdmitAsync(NodePrivate.NewKey().Public(), first.Token, ct));
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

        Assert.False(await host.AdmitAsync(stranger, "guessed", ct));
        Assert.False(host.State.IsPaired);

        Assert.True(await host.AdmitAsync(invited, offer.Token, ct));
        Assert.Equal(invited, host.Peer);
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
        Assert.True(await host.AdmitAsync(peer, offer.Token, ct));

        Assert.False(await host.AdmitAsync(NodePrivate.NewKey().Public(), offer.Token, ct));
        Assert.True(await host.AdmitAsync(peer, "the peer no longer needs it", ct));
        Assert.Equal(peer, host.Peer);
    }

    /// <summary>
    /// An invitation that lapsed while the host was up pairs with nobody:
    /// expiry is checked when a machine arrives, not only when it is minted.
    /// </summary>
    [Fact]
    public async Task AnInvitationThatLapsedWhileWaitingPairsWithNobody()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        PairingRecord host = Host();
        PairingOffer offer = await host.OfferPairingAsync(Window, ct);

        _clock.Advance(Window + TimeSpan.FromSeconds(1));

        Assert.False(await host.AdmitAsync(NodePrivate.NewKey().Public(), offer.Token, ct));
        Assert.False(host.State.IsPaired);
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
        Task pairing = host.PairWithAsync(peer, ct);

        // The second change is already made in memory, and must be waiting
        // its turn rather than overtaking the first on the way to the disk.
        Assert.Equal(peer, host.Peer);
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

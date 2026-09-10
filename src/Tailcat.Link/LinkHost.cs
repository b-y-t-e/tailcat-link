// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Tailcat.Keys;
using Tailcat.Link.Diagnostics;
using Tailcat.Link.Protocol;
using Tailcat.Link.Storage;
using Tailcat.Link.Transport;
using Tailcat.Net;

namespace Tailcat.Link;

/// <summary>
/// One machine's identity, one node, one accept loop — and as many peers as
/// its bound allows.
/// </summary>
/// <remarks>
/// <para>
/// Everything that is per-machine lives here: the stored identity, the pinned
/// region, the node, the invitations, and the handlers the application
/// registered. Everything that is per-peer lives in <see cref="LinkPeer"/>.
/// That split is what makes a bridge for four phones one identity and four
/// supervision loops, rather than four of everything.
/// </para>
/// <para>
/// The end that joined a host is the same object with no accept loop and one
/// peer that dials, so the two ends do not drift apart — least of all in the
/// pairing rules.
/// </para>
/// </remarks>
internal sealed class LinkHost : ILinkHost, ILinkHandlers, IDisposable
{
    // Candidates are heard side by side, because one at a time is a host that
    // a stranger can starve: knowing the address is enough to connect every
    // handshake window and say nothing, and the peer's connection would never
    // be looked at. The number is a cap rather than none so that a flood costs
    // bounded memory.
    private const int MaxHandshakesAtOnce = 8;

    private readonly PairingRecord _pairing;
    private readonly IInvitationSource _invitations;
    private readonly NodeHolder _node;
    private readonly LinkOptions _options;
    private readonly LinkLog _log;
    private readonly bool _listens;
    private readonly SemaphoreSlim _handshakes = new(MaxHandshakesAtOnce, MaxHandshakesAtOnce);
    private readonly ConcurrentDictionary<NodePublic, PeerEntry> _peers = new();

    // Every handshake that is still running, so that shutdown can wait for it.
    // One finishing after the peers were cleared would insert a fresh peer
    // and start its supervision loop with nothing left to dispose it, and
    // would release a semaphore that is already gone.
    private readonly ConcurrentDictionary<Guid, Task> _admitting = new();

    private readonly Lock _mu = new();
    private readonly CancellationTokenSource _cts = new();

    private LinkPeerRequestHandler? _request;
    private LinkPeerTransferHandler? _transfer;
    private readonly ConcurrentDictionary<string, LinkChannelHandler> _channels = new(StringComparer.Ordinal);

    private TaskCompletionSource<ILinkPeer> _peerAppeared = NewPeerAppeared();
    private TaskCompletionSource<ILinkPeer> _peerConnected = NewPeerAppeared();
    private Task? _accepting;
    private bool _disposed;

    /// <param name="pairing">The stored identity, peers and invitations.</param>
    /// <param name="invitations">What this machine publishes, if anything.</param>
    /// <param name="node">The one node it listens and dials with.</param>
    /// <param name="options">The knobs.</param>
    /// <param name="listens">
    /// Whether this end waits to be dialled. False on the end that joined,
    /// which has one peer and dials it.
    /// </param>
    public LinkHost(
        PairingRecord pairing,
        IInvitationSource invitations,
        NodeHolder node,
        LinkOptions options,
        bool listens)
    {
        _pairing = pairing;
        _invitations = invitations;
        _node = node;
        _options = options;
        _listens = listens;
        _log = new LinkLog(options.LoggerFactory?.CreateLogger<ILinkHost>(), options.Log);
    }

    /// <inheritdoc/>
    public InvitationCode InvitationCode => _invitations.Current;

    /// <inheritdoc/>
    public DateTimeOffset? InvitationExpiresAt => _invitations.ExpiresAt;

    /// <inheritdoc/>
    public IReadOnlyList<LinkInvitation> Invitations => _invitations.All;

    /// <inheritdoc/>
    public IReadOnlyList<ILinkPeer> Peers => [.. Ordered().Select(entry => entry.Peer)];

    /// <inheritdoc/>
    public int MaxPeers => _pairing.MaxPeers;

    /// <inheritdoc/>
    public event EventHandler<PeerEventArgs>? PeerJoined;

    /// <inheritdoc/>
    public event EventHandler<PeerLeftEventArgs>? PeerLeft;

    /// <summary>
    /// A peer exists, connected or not — which <see cref="PeerJoined"/> is
    /// not: that one waits for a session.
    /// </summary>
    /// <remarks>
    /// Not on <see cref="ILinkHost"/>, because the one-peer facade is its
    /// only reader: it has to follow a peer's state from before the first
    /// session, which is exactly the stretch <see cref="PeerJoined"/> misses.
    /// </remarks>
    public event EventHandler<PeerEventArgs>? PeerAppeared;

    /// <inheritdoc/>
    LinkPeerRequestHandler? ILinkHandlers.Request
    {
        get
        {
            lock (_mu)
            {
                return _request;
            }
        }
    }

    /// <inheritdoc/>
    LinkPeerTransferHandler? ILinkHandlers.Transfer
    {
        get
        {
            lock (_mu)
            {
                return _transfer;
            }
        }
    }

    /// <inheritdoc/>
    LinkChannelHandler? ILinkHandlers.Channel(string name) => _channels.GetValueOrDefault(name);

    /// <summary>The stored state, for the facade that shows one peer's worth of it.</summary>
    public PairingRecord Pairing => _pairing;

    /// <summary>Brings up the peers this machine already knows, and starts listening.</summary>
    public void Start()
    {
        foreach (PairedPeer remembered in _pairing.Peers)
        {
            // A peer that is switched off is still a peer: it shows in the
            // list, disconnected, until it is forgotten. On the end that
            // joined this is also what starts the dialling.
            Entry(remembered).Peer.Start();
        }
        if (_listens)
        {
            _accepting ??= Task.Run(() => AcceptLoopAsync(_cts.Token), CancellationToken.None);
        }
    }

    /// <summary>Adds the one peer the joining end dials, before anything is started.</summary>
    public LinkPeer AddDialingPeer(PairedPeer remembered, ISessionSource source)
    {
        LinkPeer peer = NewPeer(remembered, source, dials: true);
        _peers[remembered.Key] = new PeerEntry(peer, Offered: null);
        return peer;
    }

    /// <inheritdoc/>
    public void SetRequestHandler(LinkPeerRequestHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_mu)
        {
            _request = handler;
        }
    }

    /// <inheritdoc/>
    public void SetTransferHandler(LinkPeerTransferHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_mu)
        {
            _transfer = handler;
        }
    }

    /// <inheritdoc/>
    public void OnChannel(string name, LinkChannelHandler handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(handler);
        _channels[name] = handler;
    }

    /// <inheritdoc/>
    public async Task<LinkInvitation> InviteAsync(
        InvitationRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
        return await _invitations.InviteAsync(request ?? new InvitationRequest(), linked.Token)
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<bool> RevokeInvitationAsync(Guid invitationId, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
        return await _invitations.RevokeAsync(invitationId, linked.Token).ConfigureAwait(false);
    }

    /// <summary>Returns the code worth publishing now, minting one if the last has run out.</summary>
    public async Task<InvitationCode> RenewInvitationAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
        return await _invitations.RenewAsync(linked.Token).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task ForgetPeerAsync(ILinkPeer peer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(peer);
        await _pairing.ForgetPeerAsync(peer.Key, cancellationToken).ConfigureAwait(false);
        if (_peers.TryRemove(peer.Key, out PeerEntry? entry))
        {
            // Dropped as well as forgotten: a machine that is unpaired while
            // it is connected must stop reaching the handler at once, not at
            // the next reconnection.
            await entry.DisposeAsync().ConfigureAwait(false);
        }
        _log.Say($"forgot {peer.Key}");
    }

    /// <inheritdoc/>
    public Task<ILinkPeer> WaitForPeerAsync(CancellationToken cancellationToken = default) =>
        WaitForPeerAsync(mustBeConnected: true, cancellationToken);

    /// <summary>
    /// Waits for a peer to exist, and optionally for it to have a session up.
    /// </summary>
    /// <remarks>
    /// The single-peer facade waits only for the peer to exist: from there the
    /// peer's own retry loop is what waits for a session, which is where the
    /// request deadline is measured.
    /// </remarks>
    public async Task<ILinkPeer> WaitForPeerAsync(bool mustBeConnected, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
        while (true)
        {
            Task<ILinkPeer> appears;
            lock (_mu)
            {
                if (Ordered().FirstOrDefault(entry => !mustBeConnected || entry.Peer.IsConnected) is { } ready)
                {
                    return ready.Peer;
                }
                appears = mustBeConnected ? _peerConnected.Task : _peerAppeared.Task;
            }
            await appears.WaitAsync(linked.Token).ConfigureAwait(false);
        }
    }

    private IEnumerable<PeerEntry> Ordered() => _peers.Values.OrderBy(entry => entry.Peer.PairedAt);

    // Read after the entry is in rather than before, which is what makes the
    // two orders exclusive: either the store still knows the machine and
    // nobody was unpairing it, or ForgetPeerAsync's own removal ran after
    // this insert and took the entry with it.
    private async Task<bool> StillPairedAsync(NodePublic key)
    {
        if (_pairing.State.PeerWith(key) is not null)
        {
            return true;
        }
        if (_peers.TryRemove(key, out PeerEntry? forgotten))
        {
            await forgotten.DisposeAsync().ConfigureAwait(false);
        }
        return false;
    }

    // Under the lock rather than through GetOrAdd's factory: two connections
    // from the same new machine at once must not each build a peer, one of
    // which is then dropped with its supervision loop already running.
    private PeerEntry Entry(PairedPeer remembered)
    {
        PeerEntry entry;
        lock (_mu)
        {
            if (!_peers.TryGetValue(remembered.Key, out PeerEntry? known))
            {
                OfferedSessionSource offered = new(_options.TimeProvider);
                known = new PeerEntry(NewPeer(remembered, offered, dials: false), offered);
                _peers[remembered.Key] = known;
            }
            entry = known;
        }
        entry.Peer.Remember(remembered);
        return entry;
    }

    private LinkPeer NewPeer(PairedPeer remembered, ISessionSource source, bool dials)
    {
        LinkPeer peer = new(remembered, _pairing, _node, source, this, _options, _log, dials);
        peer.Connected += OnPeerConnected;
        peer.Disconnected += OnPeerDisconnected;
        PeerAppeared?.Invoke(this, new PeerEventArgs(peer));
        Announce(peer, connected: false);
        return peer;
    }

    private void OnPeerConnected(object? sender, EventArgs e)
    {
        if (sender is not ILinkPeer peer)
        {
            return;
        }
        Announce(peer, connected: true);
        PeerJoined?.Invoke(this, new PeerEventArgs(peer));
    }

    private void OnPeerDisconnected(object? sender, DisconnectedEventArgs e)
    {
        if (sender is ILinkPeer peer)
        {
            PeerLeft?.Invoke(this, new PeerLeftEventArgs(peer, e.Reason, e.Detail));
        }
    }

    private void Announce(ILinkPeer peer, bool connected)
    {
        TaskCompletionSource<ILinkPeer> waiting;
        lock (_mu)
        {
            waiting = connected ? _peerConnected : _peerAppeared;
            if (connected)
            {
                _peerConnected = NewPeerAppeared();
            }
            else
            {
                _peerAppeared = NewPeerAppeared();
            }
        }
        waiting.TrySetResult(peer);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        TimeSpan backoff = _options.MinReconnectDelay;
        int failures = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                INodeGateway gateway = await _node.EnsureAsync(ct).ConfigureAwait(false);
                ITailcatConnection knocked =
                    await AcceptBeforeGivingUpOnTheNodeAsync(gateway, ct).ConfigureAwait(false);
                await _handshakes.WaitAsync(ct).ConfigureAwait(false);
                AdmitInBackground(knocked, ct);

                // A machine that reached this host — even a stranger that is
                // about to be refused — is proof the node still works.
                failures = 0;
                backoff = _options.MinReconnectDelay;
                continue;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (LinkPeer.IsRecoverable(ex))
            {
                failures++;
                _log.Say($"listening failed: {ex.Message}");
            }
            catch (Exception ex)
            {
                // Not something waiting will fix — no QUIC on this platform,
                // a store that cannot be written. Every peer is told, rather
                // than left waiting for a session that will never come.
                _log.Warn($"the host stopped listening: {ex.Message}", ex);
                foreach (PeerEntry entry in _peers.Values)
                {
                    entry.Peer.Fault(ex);
                }
                return;
            }

            if (failures >= _options.RebuildNodeAfterFailures)
            {
                failures = 0;
                await _node.DiscardAsync().ConfigureAwait(false);
            }

            try
            {
                await Task.Delay(backoff, _options.TimeProvider, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            backoff = backoff >= _options.MaxReconnectDelay
                ? _options.MaxReconnectDelay
                : TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, _options.MaxReconnectDelay.Ticks));
        }
    }

    /// <summary>
    /// Accepts, but gives up on the node if nothing at all reaches it.
    /// </summary>
    /// <remarks>
    /// Waiting for a peer looks exactly like a node whose relay socket died
    /// without saying so — a laptop resumed from sleep behind a different NAT
    /// — and a host has nobody to restart it. So the wait is bounded and the
    /// node is rebuilt from the stored identity instead. The address does not
    /// change, so a peer that was merely away is unaffected. Bounded only
    /// while no peer is connected: see the accept below for why.
    /// </remarks>
    private async Task<ITailcatConnection> AcceptBeforeGivingUpOnTheNodeAsync(
        INodeGateway gateway,
        CancellationToken ct)
    {
        while (true)
        {
            using CancellationTokenSource silence = new(_options.ListenSilenceTimeout, _options.TimeProvider);
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct, silence.Token);
            try
            {
                return await gateway.AcceptAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (silence.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                // A session that is up says as much about the node as a new
                // connection does, and a host whose peers are all paired and
                // talking accepts nothing for hours on end. Rebuilding the
                // node here would drop every one of those sessions, so the
                // silence is only evidence when there is nothing else.
                if (_peers.Values.Any(entry => entry.Peer.IsConnected))
                {
                    continue;
                }
                throw new LinkException($"nothing has reached this machine in {_options.ListenSilenceTimeout}");
            }
        }
    }

    /// <summary>
    /// Runs a handshake off the accept loop, and keeps hold of it until it is
    /// done so that <see cref="DisposeAsync"/> can wait for it.
    /// </summary>
    private void AdmitInBackground(ITailcatConnection connection, CancellationToken ct)
    {
        Guid handshake = Guid.NewGuid();
        Task admitting = Task.Run(() => AdmitThenReleaseAsync(connection, ct), CancellationToken.None);
        _admitting[handshake] = admitting;
        // Forgotten only once it is remembered: a handshake that finished
        // before the line above would otherwise leave its entry behind for
        // the rest of the process.
        _ = admitting.ContinueWith(
            _ => _admitting.TryRemove(handshake, out Task? _),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task AdmitThenReleaseAsync(ITailcatConnection connection, CancellationToken ct)
    {
        try
        {
            await AdmitAsync(connection, ct).ConfigureAwait(false);
        }
        finally
        {
            _handshakes.Release();
        }
    }

    /// <summary>
    /// Decides whether the machine that has just connected may stay, and if
    /// it may, hands the session to the peer it belongs to.
    /// </summary>
    /// <remarks>
    /// Pairing is trust on first use, but not on any use: a machine arriving
    /// with an invitation's <em>token</em> becomes a peer, and everyone else —
    /// including whoever learned the host's address by watching the relay it
    /// is connected to — is turned away.
    /// </remarks>
    private async Task AdmitAsync(ITailcatConnection connection, CancellationToken ct)
    {
        PairedPeer? admitted = null;
        try
        {
            using IdleTimeout idle = new(_options.RequestTimeout, _options.TimeProvider);
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct, idle.Token);
            admitted = await PairingHandshake.AcceptAsync(connection, _pairing, idle, cts.Token)
                .ConfigureAwait(false);
        }
        catch (InvitationExpiredException ex)
        {
            // Told apart from every other failed handshake, and at a level
            // somebody reads. The machine outside heard the same "no" as a
            // wrong token and a full host, deliberately — but this is the one
            // refusal with a cure, and inviting again is something only
            // somebody at this end can do. A log line that reads like all the
            // others leaves them with nothing to act on.
            _log.Warn($"refused {connection.Peer}: {ex.Message}");
            await connection.DisposeAsync().ConfigureAwait(false);
            return;
        }
#pragma warning disable CA1031 // Nothing a stranger can do to its own handshake may reach the loop that is waiting for the peers.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            // One stranger's bad handshake is not this host's problem: it is
            // dropped and the next machine is heard, which is exactly what
            // must happen when the next one is a peer.
            if (!ct.IsCancellationRequested)
            {
                _log.Say($"handshake with {connection.Peer} failed: {ex.Message}", ex);
            }
        }

        if (admitted is null)
        {
            _log.Say($"refused {connection.Peer}");
            await connection.DisposeAsync().ConfigureAwait(false);
            return;
        }

        PeerEntry entry = Entry(admitted);
        if (!await StillPairedAsync(admitted.Key).ConfigureAwait(false))
        {
            // Unpaired while this handshake was running. ForgetPeerAsync
            // writes the store and only then drops the entry, so it found
            // nothing to drop and this one would otherwise be a fresh peer,
            // with a supervision loop and a live session, that the store no
            // longer knows about — and so nothing would unpair again.
            _log.Say($"refused {connection.Peer}: unpaired mid-handshake");
            await connection.DisposeAsync().ConfigureAwait(false);
            return;
        }

        if (entry.Offered is null)
        {
            // Only the end that joined has a peer without a slot to hand
            // sessions to, and that end does not listen.
            await connection.DisposeAsync().ConfigureAwait(false);
            return;
        }

        _log.Say($"admitted {admitted.Key}");
        entry.Peer.Start();
        entry.Peer.AcceptSession(connection, entry.Offered);
    }

    private static TaskCompletionSource<ILinkPeer> NewPeerAppeared() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Takes the host down for a caller that has no way to await.</summary>
    /// <remarks>
    /// A service container's own <c>Dispose</c> refuses an object that is
    /// only <see cref="IAsyncDisposable"/>, so a consumer writing the
    /// ordinary <c>using IHost host = builder.Build()</c> would crash at
    /// shutdown without this. Blocking is safe because everything the
    /// asynchronous path awaits does so with <c>ConfigureAwait(false)</c> and
    /// so returns to no context of the caller's; prefer
    /// <see cref="DisposeAsync"/> wherever there is a way to await.
    /// </remarks>
    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
        GC.SuppressFinalize(this);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        await _cts.CancelAsync().ConfigureAwait(false);
        if (_accepting is not null)
        {
            try
            {
                await _accepting.ConfigureAwait(false);
            }
            catch (Exception ex) when (LinkPeer.IsRecoverable(ex))
            {
            }
        }

        // Before the peers go, and before the semaphore they release does: a
        // handshake still running would otherwise add a peer to the
        // dictionary that has just been cleared, leaving a supervision loop
        // and a connection that nothing will ever dispose.
        foreach (Task admitting in _admitting.Values)
        {
            try
            {
                await admitting.ConfigureAwait(false);
            }
            catch (Exception ex) when (LinkPeer.IsRecoverable(ex))
            {
            }
        }

        foreach (PeerEntry entry in _peers.Values)
        {
            await entry.DisposeAsync().ConfigureAwait(false);
        }
        _peers.Clear();

        lock (_mu)
        {
            _peerAppeared.TrySetCanceled();
            _peerConnected.TrySetCanceled();
        }

        await _node.DisposeAsync().ConfigureAwait(false);
        _handshakes.Dispose();
        _cts.Dispose();
    }

    /// <summary>A peer and, on a listening host, the slot its sessions are handed to.</summary>
    private sealed record PeerEntry(LinkPeer Peer, OfferedSessionSource? Offered) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Peer.DisposeAsync();
    }
}

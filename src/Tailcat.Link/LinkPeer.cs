// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Diagnostics;
using System.Net.Quic;
using System.Net.Sockets;
using System.Security.Authentication;
using Tailcat.Keys;
using Tailcat.Link.Diagnostics;
using Tailcat.Link.Protocol;
using Tailcat.Link.Storage;
using Tailcat.Link.Transport;
using Tailcat.Net;

namespace Tailcat.Link;

/// <summary>
/// What answers a peer's requests, wherever the application registered it.
/// </summary>
/// <remarks>
/// A peer reads its handlers through this rather than holding them, so an
/// application that registers one after the link is up still answers, and so
/// that one piece of logic serves every peer.
/// </remarks>
internal interface ILinkHandlers
{
    /// <summary>What answers requests, or null if nothing does.</summary>
    LinkPeerRequestHandler? Request { get; }

    /// <summary>What takes transfers, or null if nothing does.</summary>
    LinkPeerTransferHandler? Transfer { get; }

    /// <summary>What takes a channel of that name, or null if nothing does.</summary>
    LinkChannelHandler? Channel(string name);
}

/// <summary>
/// One peer, and the supervision loop that always has a session to it or is
/// busy building the next one.
/// </summary>
/// <remarks>
/// <para>
/// Everything that can go wrong ends the current session, and every session
/// that ends starts the loop again. There is no separate handling for a relay
/// outage, a machine that moved network, a peer that rebooted, or a peer that
/// simply went away for a day: all of them look like a session that stopped
/// answering, and all of them are answered by connecting again.
/// </para>
/// <para>
/// Callers are kept away from all of it. A request made while the peer is
/// unreachable waits for the next session rather than failing, so an
/// application does not need its own retry loop on top of this one.
/// </para>
/// </remarks>
internal sealed class LinkPeer : ILinkPeer, IAsyncDisposable
{
    private readonly PairingRecord _pairing;
    private readonly NodeHolder _node;
    private readonly ISessionSource _source;
    private readonly ILinkHandlers _handlers;
    private readonly LinkOptions _options;
    private readonly LinkLog _log;
    private readonly bool _dials;
    private readonly ExchangeLedger _ledger;
    private readonly TransferRegistry _transfers;
    private readonly Lock _mu = new();
    private readonly CancellationTokenSource _cts = new();

    private PairedPeer _remembered;
    private LinkSession? _session;
    private bool _sessionIsUp;
    private LinkConnectionState _state = LinkConnectionState.Idle;
    private TaskCompletionSource<LinkSession> _ready = NewReady();
    private TaskCompletionSource _sessionEnded = NewSessionEnded();
    private Task? _supervisor;
    private bool _disposed;

    /// <param name="remembered">What the store knows about this machine.</param>
    /// <param name="pairing">Where a session with it is written down.</param>
    /// <param name="node">The one node this machine listens and dials with.</param>
    /// <param name="source">Where this peer's next session comes from.</param>
    /// <param name="handlers">What answers it, read afresh on every arrival.</param>
    /// <param name="options">The knobs the supervision loop is measured by.</param>
    /// <param name="log">Where the loop says what it is doing.</param>
    /// <param name="dials">
    /// Whether this end reaches out to the peer rather than being reached.
    /// Two things follow from it. Repeated failures here may throw the shared
    /// node away, which only the dialling end may do: on a host the node is
    /// what every peer is waiting on, and one peer that is switched off must
    /// not take it from the others — a host's accept loop rebuilds it
    /// instead, on its own silence. And a session coming up is what writes
    /// the pairing down, because on this end no handshake decided anything;
    /// on a host the handshake already did, and repeating it there would
    /// re-admit a machine that was forgotten while it was reconnecting.
    /// </param>
    public LinkPeer(
        PairedPeer remembered,
        PairingRecord pairing,
        NodeHolder node,
        ISessionSource source,
        ILinkHandlers handlers,
        LinkOptions options,
        LinkLog log,
        bool dials)
    {
        _remembered = remembered;
        _pairing = pairing;
        _node = node;
        _source = source;
        _handlers = handlers;
        _options = options;
        _log = log;
        _dials = dials;
        // The protocol's window, not this machine's: what has to be remembered
        // is how long the machine at the *other* end may keep retrying, and
        // nothing on the wire says. LinkOptions bounds every sender by the
        // same constant so that this is always long enough.
        _ledger = new ExchangeLedger(LinkProtocol.ExchangeRetention, options.TimeProvider);
        // For the same reason as the ledger, over the window a transfer needs:
        // what a resumed transfer joins is on this side of the session that
        // died, so it lives on the peer rather than on the session.
        _transfers = new TransferRegistry(
            CurrentTransferHandler, LinkProtocol.TransferRetention, options.TimeProvider, _cts.Token);
    }

    /// <inheritdoc/>
    public NodePublic Key => Remembered.Key;

    /// <inheritdoc/>
    public string? Name => Remembered.Name;

    /// <inheritdoc/>
    public DateTimeOffset PairedAt => Remembered.PairedAt;

    /// <inheritdoc/>
    public DateTimeOffset LastSeen => Remembered.LastSeen;

    /// <inheritdoc/>
    public bool IsConnected
    {
        get
        {
            lock (_mu)
            {
                return _session is not null;
            }
        }
    }

    /// <inheritdoc/>
    public LinkConnectionState State
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
    public event EventHandler<EventArgs>? Connected;

    /// <inheritdoc/>
    public event EventHandler<DisconnectedEventArgs>? Disconnected;

    /// <inheritdoc/>
    public event EventHandler<LinkStateChangedEventArgs>? StateChanged;

    private PairedPeer Remembered
    {
        get
        {
            lock (_mu)
            {
                return _remembered;
            }
        }
    }

    /// <summary>Begins keeping a session to this peer up.</summary>
    public void Start()
    {
        MoveTo(LinkConnectionState.Connecting);
        lock (_mu)
        {
            // Under the lock like the rest of this peer's state. The host
            // admits handshakes several at a time, so one machine re-dialling
            // while its first handshake is still landing reaches here twice at
            // once. Two supervision loops would read one OfferedSessionSource
            // built with SingleReader = true, and each would set _session and
            // _ready — leaving the peer pointing at a session the other loop
            // is already closing.
            _supervisor ??= Task.Run(() => SuperviseAsync(_cts.Token), CancellationToken.None);
        }
    }

    /// <summary>
    /// Hands this peer a session the host has just admitted, condemning
    /// whatever it had.
    /// </summary>
    /// <remarks>
    /// A fresh handshake from the same machine means it re-dialled, so the
    /// session this end still believes in is one nobody is reading. Failing
    /// it is what makes the supervision loop come round and take the new one.
    /// </remarks>
    public void AcceptSession(ITailcatConnection connection, OfferedSessionSource offered)
    {
        ArgumentNullException.ThrowIfNull(offered);
        offered.Offer(connection);
        LinkSession? stale;
        lock (_mu)
        {
            stale = _session;
        }
        stale?.Fail("the peer opened another session");
    }

    /// <summary>
    /// Retires this peer for good, because something the host ran into will
    /// not be fixed by waiting.
    /// </summary>
    public void Fault(Exception fatal) => Stop(fatal);

    /// <summary>Takes on what the store now remembers about this machine.</summary>
    public void Remember(PairedPeer remembered)
    {
        lock (_mu)
        {
            _remembered = remembered;
        }
    }

    /// <inheritdoc/>
    public async Task<byte[]> RequestAsync(ReadOnlyMemory<byte> request, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // Before the retry loop, because a payload over the cap is refused by
        // every session alike: inside the loop it would be retried until the
        // deadline and reported as silence rather than as the caller's own
        // mistake.
        LinkFrame.EnsureSendable(request);

        using CancellationTokenSource expiry = new(_options.RequestDeadline, _options.TimeProvider);
        using CancellationTokenSource deadline =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token, expiry.Token);

        // One id for the request, not for each attempt: it is what tells the
        // peer that a retry is the same request it may already have run.
        Guid exchange = Guid.NewGuid();
        Exception? last = null;
        // Around the retries rather than inside them: the id belongs to the
        // request, so a span per attempt would show one reconnection as
        // several unrelated requests.
        using Activity? span = LinkTelemetry.StartRequest();
        while (!deadline.IsCancellationRequested)
        {
            // Taken before the attempt: a session that dies during it is
            // reported by the supervisor afterwards, and this still catches it.
            Task ended = CurrentSessionEnded();
            LinkSession? attempted = null;
            try
            {
                attempted = await CurrentSessionAsync(deadline.Token).ConfigureAwait(false);
                LinkTelemetry.Sent("request", request.Length);
                byte[] answer =
                    await attempted.RequestAsync(exchange, request, deadline.Token).ConfigureAwait(false);
                LinkTelemetry.Received("request", answer.Length);
                LinkTelemetry.RequestEnded("answered");
                return answer;
            }
            catch (RemoteHandlerException)
            {
                // The peer answered; it just did not like the request. That is
                // an answer, and the caller gets it rather than a retry.
                LinkTelemetry.RequestEnded("refused");
                throw;
            }
            catch (LinkException ex) when (!cancellationToken.IsCancellationRequested)
            {
                last = ex;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (attempted is not null && !attempted.Ended.IsCompleted)
            {
                // The session is still up, so the attempt ran out of time
                // rather than fell over — a handler slower than one request
                // window. Asking again on the same session re-joins the run
                // already under way there, and that attempt spends another
                // whole window waiting, so this is patience rather than a spin.
                continue;
            }

            try
            {
                // The dying session is still the one this peer hands out until
                // the supervisor has finished tearing it down, so retrying at
                // once would spin on it for as long as QUIC takes to close.
                await ended.WaitAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }

        // After the two below, so that a caller who changed its mind is not
        // counted as a peer that went silent.
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        LinkTelemetry.RequestEnded("unanswered");
        throw new LinkTimeoutException($"no answer within {_options.RequestDeadline}", last);
    }

    /// <inheritdoc/>
    public async Task NotifyAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // Said now rather than after waiting for a session it could never be
        // sent on anyway.
        LinkFrame.EnsureSendable(message);

        using CancellationTokenSource expiry = new(_options.RequestDeadline, _options.TimeProvider);
        using CancellationTokenSource deadline =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token, expiry.Token);

        LinkSession session = await CurrentSessionAsync(deadline.Token).ConfigureAwait(false);
        await session.NotifyAsync(message, deadline.Token).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task SendAsync(
        Stream content,
        TransferOffer offer,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(offer);
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureLengthMatches(content, offer);

        using CancellationTokenSource deadline =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);

        // The id, and where the content has got to, belong to the transfer
        // rather than to any one attempt at it: that is what lets the next
        // session carry on from where this one stopped.
        OutboundTransfer transfer = new(offer, content, progress, _options.TimeProvider);
        Exception? last = null;
        while (!deadline.IsCancellationRequested)
        {
            Task ended = CurrentSessionEnded();
            try
            {
                LinkSession session = await CurrentSessionAsync(deadline.Token).ConfigureAwait(false);
                await session.SendTransferAsync(transfer, deadline.Token).ConfigureAwait(false);
                return;
            }
            catch (RemoteHandlerException)
            {
                // The other machine has decided about this transfer. Sending
                // it again would reach the same decision.
                throw;
            }
            catch (LinkException ex) when (!cancellationToken.IsCancellationRequested)
            {
                last = ex;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (!content.CanSeek && transfer.Sent > 0)
            {
                // Nothing can be resumed from a stream that only goes
                // forwards, and the receiver will ask for an offset that is
                // now behind this one. Said plainly rather than retried into
                // the same wall until the stall timeout.
                throw new LinkException(
                    $"the transfer stopped after {transfer.Sent} bytes and its content cannot be rewound",
                    last);
            }

            // Deliberately not a deadline on the transfer: twenty gigabytes
            // through a relay is hours, and any total limit would be a limit
            // on how large a file this library can send. What is bounded is
            // silence — a transfer that is still moving is never given up on,
            // and one that has stopped is given up on in bounded time.
            if (transfer.Stalled >= _options.TransferStallTimeout)
            {
                break;
            }

            // Never straight round again. Usually the session is already
            // ending — a failed transfer condemns it — and waiting for that
            // is what stops the next attempt from spinning on a connection
            // that is still being torn down; the pause bounds the case where
            // the session survived whatever the attempt ran into.
            try
            {
                await ended.WaitAsync(_options.MinReconnectDelay, _options.TimeProvider, deadline.Token)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        throw new LinkTimeoutException(
            $"the transfer moved nothing for {_options.TransferStallTimeout}, after {transfer.Sent} bytes",
            last);
    }

    /// <inheritdoc/>
    public async Task<ILinkChannelWriter> OpenChannelAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ObjectDisposedException.ThrowIf(_disposed, this);

        using CancellationTokenSource expiry = new(_options.RequestDeadline, _options.TimeProvider);
        using CancellationTokenSource deadline =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token, expiry.Token);

        // A channel is not resumed, so it is opened on one session and lives
        // and dies with it. There is nothing to retry onto.
        LinkSession session = await CurrentSessionAsync(deadline.Token).ConfigureAwait(false);
        Stream stream = await session.OpenChannelAsync(name, deadline.Token).ConfigureAwait(false);
        // Handed to the session, which ends it when it ends: an application
        // that unwinds its pipeline on Closed must hear that from the session
        // dying rather than only from the send that discovers it.
        return await session.HoldAsync(new OutgoingChannel(name, this, stream, _log, session.Alive)).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task WaitUntilConnectedAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
        await CurrentSessionAsync(linked.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// Catches the commonest way to announce a length that is not the one
    /// being sent, before a byte of it has crossed the network.
    /// </summary>
    private static void EnsureLengthMatches(Stream content, TransferOffer offer)
    {
        if (offer.Length is long announced && content.CanSeek && content.Length - content.Position != announced)
        {
            throw new LinkException(
                $"the transfer announces {announced} bytes and its content has "
                + $"{content.Length - content.Position}");
        }
    }

    private Task<LinkSession> CurrentSessionAsync(CancellationToken ct)
    {
        TaskCompletionSource<LinkSession> ready;
        lock (_mu)
        {
            ready = _ready;
        }
        return ready.Task.WaitAsync(ct);
    }

    private Task CurrentSessionEnded()
    {
        lock (_mu)
        {
            return _sessionEnded.Task;
        }
    }

    /// <summary>Wakes everyone who was waiting for the current session to end.</summary>
    private void ReleaseSessionEnded()
    {
        TaskCompletionSource ended;
        lock (_mu)
        {
            ended = _sessionEnded;
            _sessionEnded = NewSessionEnded();
        }
        ended.TrySetResult();
    }

    private async Task SuperviseAsync(CancellationToken ct)
    {
        TimeSpan backoff = _options.MinReconnectDelay;
        int failures = 0;

        while (!ct.IsCancellationRequested)
        {
            string reason;
            LinkDisconnectReason kind = LinkDisconnectReason.NetworkLost;
            LinkSession? session = null;
            try
            {
                INodeGateway gateway = await _node.EnsureAsync(ct).ConfigureAwait(false);
                ITailcatConnection connection =
                    await _source.NextSessionAsync(gateway, ct).ConfigureAwait(false);

                session = new LinkSession(
                    connection,
                    CurrentHandler,
                    ChannelServing,
                    _ledger,
                    _transfers,
                    _options.RequestTimeout,
                    _options.TransferStallTimeout,
                    _options.TimeProvider,
                    // Handlers are bound to the peer, not to this session, so
                    // one that is running when the session drops finishes and
                    // the sender's retry is answered from the ledger.
                    _cts.Token);
                session.Start();
                await OnSessionUpAsync(session, ct).ConfigureAwait(false);

                long upSince = _options.TimeProvider.GetTimestamp();
                reason = await WatchAsync(session, ct).ConfigureAwait(false);

                // Only a session that held counts as a success. One that comes
                // up and dies at once — a relay that accepts the handshake and
                // drops it, a peer that closes right after it — would
                // otherwise pin the retry at MinReconnectDelay for ever and
                // never reach the failure count that rebuilds the node, which
                // is the only repair for a socket a suspended laptop broke.
                if (_options.TimeProvider.GetElapsedTime(upSince) >= _options.HeartbeatInterval)
                {
                    failures = 0;
                    backoff = _options.MinReconnectDelay;
                }
                else
                {
                    failures++;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (PairingRefusedException ex)
            {
                failures++;
                reason = ex.Message;
                kind = LinkDisconnectReason.Refused;
            }
            catch (Exception ex) when (IsRecoverable(ex))
            {
                failures++;
                reason = ex.Message;
            }
            catch (Exception ex)
            {
                // Not something waiting will fix — no QUIC on this platform,
                // a store that cannot be written. Callers hear about it
                // instead of waiting forever for a link that will never come.
                _log.Warn($"link stopped: {ex.Message}");
                Stop(ex);
                return;
            }
            finally
            {
                if (session is not null)
                {
                    // Here rather than in OnSessionDown, which a dispose and a
                    // fatal stop both return without reaching: a gauge of what
                    // is up now must not keep counting a session that is not.
                    ReleaseSessionCount();
                    await session.DisposeAsync().ConfigureAwait(false);
                }
            }

            OnSessionDown(kind, reason);

            if (_dials && failures >= _options.RebuildNodeAfterFailures)
            {
                failures = 0;
                LinkTelemetry.NodeRebuilt();
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
            backoff = Grow(backoff);
        }
    }

    /// <summary>
    /// Watches a live session until it ends, checking on the peer meanwhile.
    /// </summary>
    private async Task<string> WatchAsync(LinkSession session, CancellationToken ct)
    {
        while (true)
        {
            Task ended = session.Ended;
            Task heartbeat = Task.Delay(_options.HeartbeatInterval, _options.TimeProvider, ct);
            if (await Task.WhenAny(ended, heartbeat).ConfigureAwait(false) == ended)
            {
                return await session.Ended.ConfigureAwait(false);
            }

            try
            {
                await session.PingAsync(ct).ConfigureAwait(false);
            }
            catch (LinkException ex)
            {
                return ex.Message;
            }
        }
    }

    private async Task OnSessionUpAsync(LinkSession session, CancellationToken ct)
    {
        // Only on the end that dialled, where there was no handshake to decide
        // anything. On a host the handshake already wrote the pairing, so this
        // would be a no-op — except in the one window where it is not: a peer
        // forgotten while its session was coming up would be written straight
        // back, past every invitation and MaxPeers check.
        if (_dials)
        {
            await _pairing.PairWithAsync(session.Peer, cancellationToken: ct).ConfigureAwait(false);
            if (_pairing.State.PeerWith(session.Peer) is { } refreshed)
            {
                Remember(refreshed);
            }
        }

        lock (_mu)
        {
            _session = session;
            _sessionIsUp = true;
            _ready.TrySetResult(session);
        }
        _log.Say($"link up with {session.Peer}");
        LinkTelemetry.SessionUp();
        MoveTo(LinkConnectionState.Connected);
        Raise(() => Connected?.Invoke(this, EventArgs.Empty), nameof(Connected));
    }

    private void OnSessionDown(LinkDisconnectReason kind, string reason)
    {
        lock (_mu)
        {
            _session = null;
            // A promise that already handed out the dead session is no use to
            // the next caller; one that nobody has completed still is.
            if (_ready.Task.IsCompleted)
            {
                _ready = NewReady();
            }
        }
        ReleaseSessionEnded();
        _log.Say($"link down: {reason}");
        LinkTelemetry.Reconnecting(kind);
        MoveTo(LinkConnectionState.Reconnecting);
        Raise(
            () => Disconnected?.Invoke(this, new DisconnectedEventArgs(kind, reason)),
            nameof(Disconnected));
    }

    /// <summary>
    /// Gives back the count a session took on the gauge of sessions that are
    /// up, once however often it is called and never for one that never came
    /// up.
    /// </summary>
    private void ReleaseSessionCount()
    {
        lock (_mu)
        {
            if (!_sessionIsUp)
            {
                return;
            }
            _sessionIsUp = false;
        }
        LinkTelemetry.SessionEnded();
    }

    /// <summary>Retires the peer for good, with the reason it will not come back.</summary>
    private void Stop(Exception fatal)
    {
        lock (_mu)
        {
            _session = null;
            // The promise may already hold the session that has just died, and
            // that one cannot be failed; callers need a fresh one to fail.
            if (_ready.Task.IsCompleted)
            {
                _ready = NewReady();
            }
            _ready.TrySetException(fatal);
            // Nobody may ever await it, and an unobserved fault would surface
            // later as an unrelated crash on the finalizer thread.
            _ = _ready.Task.Exception;
        }
        ReleaseSessionEnded();
        MoveTo(LinkConnectionState.Faulted);
        Raise(
            () => Disconnected?.Invoke(
                this, new DisconnectedEventArgs(LinkDisconnectReason.Faulted, fatal.Message, fatal)),
            nameof(Disconnected));
    }

    private void MoveTo(LinkConnectionState next)
    {
        lock (_mu)
        {
            if (_state == next)
            {
                return;
            }
            _state = next;
        }
        Raise(() => StateChanged?.Invoke(this, new LinkStateChangedEventArgs(next)), nameof(StateChanged));
    }

    /// <summary>
    /// Runs an application's event handler where its failure stays its own.
    /// The supervision loop raises these, and a handler that throws would
    /// otherwise take the loop down with it — leaving the link dead for good.
    /// </summary>
    private void Raise(Action raise, string eventName)
    {
        try
        {
            raise();
        }
        catch (Exception ex)
        {
            _log.Warn($"{eventName} handler threw: {ex.Message}");
        }
    }

    private LinkRequestHandler? CurrentHandler() =>
        _handlers.Request is { } answer
            ? (request, ct) => answer(this, request, ct)
            : null;

    private LinkTransferHandler? CurrentTransferHandler() =>
        _handlers.Transfer is { } receive
            ? (transfer, ct) => receive(this, transfer, ct)
            : null;

    private LinkChannelServe? ChannelServing(string name) =>
        _handlers.Channel(name) is { } take
            ? async (stream, ct) =>
            {
                IncomingChannel channel = new(name, this, stream, _log, ct);
                try
                {
                    await take(this, channel, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Application code threw. There is no answer left to put a
                    // failure in — the channel was accepted frames ago — so the
                    // peer learns only that the channel ended. Said here rather
                    // than left to fault a fire-and-forget task, where it would
                    // be lost entirely: a channel handler that throws must be
                    // as visible as a request handler that does.
                    _log.Warn($"the \"{name}\" channel handler threw: {ex.Message}");
                }
                finally
                {
                    // The channel ends when the handler returns, whether or
                    // not the handler read to the end: nobody else is going
                    // to close one it stopped reading half way through.
                    await channel.EndAsync("the handler returned").ConfigureAwait(false);
                }
            }
            : null;

    private TimeSpan Grow(TimeSpan backoff) =>
        backoff >= _options.MaxReconnectDelay ? _options.MaxReconnectDelay
            : TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, _options.MaxReconnectDelay.Ticks));

    // Everything that a later attempt might get past. A refused TLS handshake
    // (AuthenticationException) belongs here: it is what a race against a
    // restarted host's certificate looks like, and a permanent stop would need
    // the invitation code again.
    internal static bool IsRecoverable(Exception ex) =>
        ex is TailcatException or LinkException or QuicException or AuthenticationException
            or IOException or SocketException or ObjectDisposedException or OperationCanceledException
            or InvalidOperationException;

    private static TaskCompletionSource<LinkSession> NewReady() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource NewSessionEnded() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        await _cts.CancelAsync().ConfigureAwait(false);
        // Before waiting on anything: a handler still reading a half-delivered
        // transfer is holding the supervisor's shutdown up until it is told
        // that no more of it is coming.
        _transfers.ExpireAll();
        Task? supervisor;
        lock (_mu)
        {
            supervisor = _supervisor;
        }
        if (supervisor is not null)
        {
            try
            {
                await supervisor.ConfigureAwait(false);
            }
            catch (Exception ex) when (IsRecoverable(ex))
            {
            }
        }

        LinkSession? session;
        lock (_mu)
        {
            session = _session;
            _session = null;
            _ready.TrySetCanceled();
        }
        if (session is not null)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
        await _source.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();
    }
}

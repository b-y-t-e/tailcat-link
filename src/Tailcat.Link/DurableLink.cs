// Copyright (c) Tailscale Inc & contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Net.Quic;
using System.Net.Sockets;
using System.Security.Authentication;
using Tailcat.Keys;
using Tailcat.Link.Protocol;
using Tailcat.Link.Storage;
using Tailcat.Link.Transport;
using Tailcat.Net;

namespace Tailcat.Link;

/// <summary>
/// The link itself: one supervision loop that always has a session up, or is
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
/// Callers are kept away from all of it. A request made while the link is
/// down waits for the next session rather than failing, so an application
/// does not need its own retry loop on top of this one.
/// </para>
/// </remarks>
internal sealed class DurableLink : ILink
{
    private readonly PairingRecord _pairing;
    private readonly LinkOptions _options;
    private readonly ISessionSource _source;
    private readonly IInvitationSource _invitation;
    private readonly ExchangeLedger _ledger;
    private readonly Lock _mu = new();
    private readonly CancellationTokenSource _cts = new();

    private INodeGateway? _gateway;
    private LinkSession? _session;
    private TaskCompletionSource<LinkSession> _ready = NewReady();
    private TaskCompletionSource _sessionEnded = NewSessionEnded();
    private LinkRequestHandler? _handler;
    private Task? _supervisor;
    private bool _disposed;

    public DurableLink(
        PairingRecord pairing,
        IInvitationSource invitation,
        ISessionSource source,
        INodeGateway gateway,
        LinkOptions options)
    {
        _pairing = pairing;
        _invitation = invitation;
        _source = source;
        _gateway = gateway;
        _options = options;
        // The protocol's window, not this machine's: what has to be remembered
        // is how long the machine at the *other* end may keep retrying, and
        // nothing on the wire says. LinkOptions bounds every sender by the
        // same constant so that this is always long enough.
        _ledger = new ExchangeLedger(LinkProtocol.ExchangeRetention, options.TimeProvider);
    }

    /// <inheritdoc/>
    public InvitationCode InvitationCode => _invitation.Current;

    /// <inheritdoc/>
    public DateTimeOffset? InvitationExpiresAt => _invitation.ExpiresAt;

    /// <inheritdoc/>
    public async Task<InvitationCode> RenewInvitationAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
        return await _invitation.RenewAsync(linked.Token).ConfigureAwait(false);
    }

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
    public NodePublic Peer => _pairing.Peer;

    /// <inheritdoc/>
    public event Action? Connected;

    /// <inheritdoc/>
    public event Action<string>? Disconnected;

    /// <summary>Begins keeping the link up.</summary>
    public void Start() => _supervisor ??= Task.Run(() => SuperviseAsync(_cts.Token), CancellationToken.None);

    /// <inheritdoc/>
    public void OnRequest(LinkRequestHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_mu)
        {
            _handler = handler;
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
        while (!deadline.IsCancellationRequested)
        {
            // Taken before the attempt: a session that dies during it is
            // reported by the supervisor afterwards, and this still catches it.
            Task ended = CurrentSessionEnded();
            LinkSession? attempted = null;
            try
            {
                attempted = await CurrentSessionAsync(deadline.Token).ConfigureAwait(false);
                return await attempted.RequestAsync(exchange, request, deadline.Token).ConfigureAwait(false);
            }
            catch (RemoteHandlerException)
            {
                // The peer answered; it just did not like the request. That is
                // an answer, and the caller gets it rather than a retry.
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
                // The dying session is still the one this link hands out until
                // the supervisor has finished tearing it down, so retrying at
                // once would spin on it for as long as QUIC takes to close.
                await ended.WaitAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        throw new LinkException($"no answer within {_options.RequestDeadline}", last);
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
    public async Task WaitUntilConnectedAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
        await CurrentSessionAsync(linked.Token).ConfigureAwait(false);
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
            LinkSession? session = null;
            try
            {
                INodeGateway gateway = await EnsureGatewayAsync(ct).ConfigureAwait(false);
                TailcatConnection connection =
                    await _source.NextSessionAsync(gateway, ct).ConfigureAwait(false);

                session = new LinkSession(
                    connection,
                    CurrentHandler,
                    _ledger,
                    _options.RequestTimeout,
                    _options.TimeProvider,
                    // Handlers are bound to the link, not to this session, so
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
                _options.Log?.Invoke($"link stopped: {ex.Message}");
                Stop(ex);
                return;
            }
            finally
            {
                if (session is not null)
                {
                    await session.DisposeAsync().ConfigureAwait(false);
                }
            }

            OnSessionDown(reason);

            if (failures >= _options.RebuildNodeAfterFailures)
            {
                failures = 0;
                await DiscardGatewayAsync().ConfigureAwait(false);
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
        // On the joining end this is already the machine that was dialled; on
        // the hosting end it is whoever arrived with the code first.
        await _pairing.PairWithAsync(session.Peer, ct).ConfigureAwait(false);

        lock (_mu)
        {
            _session = session;
            _ready.TrySetResult(session);
        }
        _options.Log?.Invoke("link up");
        Raise(() => Connected?.Invoke(), nameof(Connected));
    }

    private void OnSessionDown(string reason)
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
        _options.Log?.Invoke($"link down: {reason}");
        Raise(() => Disconnected?.Invoke(reason), nameof(Disconnected));
    }

    /// <summary>Retires the link for good, with the reason it will not come back.</summary>
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
        Raise(() => Disconnected?.Invoke(fatal.Message), nameof(Disconnected));
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
            _options.Log?.Invoke($"{eventName} handler threw: {ex.Message}");
        }
    }

    private LinkRequestHandler? CurrentHandler()
    {
        lock (_mu)
        {
            return _handler;
        }
    }

    private async Task<INodeGateway> EnsureGatewayAsync(CancellationToken ct)
    {
        INodeGateway? gateway;
        lock (_mu)
        {
            gateway = _gateway;
        }
        if (gateway is not null)
        {
            return gateway;
        }

        // Same key and, for a host, the same pinned region: the rebuilt node
        // has the address the peer already has.
        LinkState state = _pairing.State;
        gateway = await _options.Gateway
            .CreateAsync(state.PrivateKey, state.HomeRegionId, ct).ConfigureAwait(false);
        lock (_mu)
        {
            _gateway = gateway;
        }
        _options.Log?.Invoke("node rebuilt");
        return gateway;
    }

    private async Task DiscardGatewayAsync()
    {
        INodeGateway? gateway;
        lock (_mu)
        {
            gateway = _gateway;
            _gateway = null;
        }
        if (gateway is not null)
        {
            await gateway.DisposeAsync().ConfigureAwait(false);
        }
    }

    private TimeSpan Grow(TimeSpan backoff) =>
        backoff >= _options.MaxReconnectDelay ? _options.MaxReconnectDelay
            : TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, _options.MaxReconnectDelay.Ticks));

    // Everything that a later attempt might get past. A refused TLS handshake
    // (AuthenticationException) belongs here: it is what a race against a
    // restarted host's certificate looks like, and a permanent stop would need
    // the invitation code again.
    private static bool IsRecoverable(Exception ex) =>
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
        if (_supervisor is not null)
        {
            try
            {
                await _supervisor.ConfigureAwait(false);
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
        await DiscardGatewayAsync().ConfigureAwait(false);
        _cts.Dispose();
    }
}

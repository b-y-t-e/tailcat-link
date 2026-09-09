// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using Tailcat.Keys;
using Microsoft.Extensions.Logging;
using Tailcat.Link.Diagnostics;

namespace Tailcat.Link;

/// <summary>
/// A link to exactly one machine: <see cref="LinkHost"/> with a bound of one
/// peer, behind the narrower interface most applications want.
/// </summary>
/// <remarks>
/// It is a facade and deliberately nothing more. The moment this held its own
/// pairing rules they would drift from the host's, and the drift would be in
/// who is let in — so everything here delegates, and the only work it does is
/// deciding which peer "the peer" means.
/// </remarks>
internal sealed class DurableLink : ILink
{
    private readonly LinkHost _host;
    private readonly LinkOptions _options;
    private readonly LinkLog _log;
    private readonly Lock _mu = new();
    private ILinkPeer? _peer;
    private bool _started;
    private bool _disposed;

    public DurableLink(LinkHost host, LinkOptions options)
    {
        _host = host;
        _options = options;
        _log = new LinkLog(options.LoggerFactory?.CreateLogger<ILink>(), options.Log);
        _host.PeerJoined += (_, _) => Raise(() => Connected?.Invoke(), nameof(Connected));
        _host.PeerLeft += (_, left) =>
        {
            Raise(() => Disconnected?.Invoke(left.Detail), nameof(Disconnected));
            Raise(
                () => SessionEnded?.Invoke(this, new DisconnectedEventArgs(left.Reason, left.Detail)),
                nameof(SessionEnded));
        };
        _host.PeerAppeared += (_, appeared) => Follow(appeared.Peer);

        // The end that joined is given its peer before this facade exists, so
        // the event above would never fire for it.
        foreach (ILinkPeer known in _host.Peers)
        {
            Follow(known);
        }
    }

    /// <inheritdoc/>
    public InvitationCode InvitationCode => _host.InvitationCode;

    /// <inheritdoc/>
    public DateTimeOffset? InvitationExpiresAt => _host.InvitationExpiresAt;

    /// <inheritdoc/>
    public bool IsConnected => _host.Peers.Any(peer => peer.IsConnected);

    /// <inheritdoc/>
    public NodePublic Peer => _host.Pairing.State.PeerKey;

    /// <inheritdoc/>
    /// <remarks>
    /// Before anything has paired there is no peer to ask, and the answer is
    /// what this end is doing about that: waiting for one.
    /// </remarks>
    public LinkConnectionState State
    {
        get
        {
            lock (_mu)
            {
                return _peer?.State
                    ?? (_started ? LinkConnectionState.Connecting : LinkConnectionState.Idle);
            }
        }
    }

    /// <inheritdoc/>
    public event Action? Connected;

    /// <inheritdoc/>
    public event Action<string>? Disconnected;

    /// <inheritdoc/>
    public event EventHandler<DisconnectedEventArgs>? SessionEnded;

    /// <inheritdoc/>
    public event EventHandler<LinkStateChangedEventArgs>? StateChanged;

    /// <summary>Begins keeping the link up.</summary>
    public void Start()
    {
        lock (_mu)
        {
            _started = true;
        }
        _host.Start();
    }

    /// <summary>
    /// Takes the one machine's state as this link's own, because with
    /// <see cref="LinkOptions.MaxPeers"/> of one they are the same thing.
    /// </summary>
    private void Follow(ILinkPeer peer)
    {
        lock (_mu)
        {
            if (ReferenceEquals(_peer, peer))
            {
                return;
            }
            _peer = peer;
        }
        peer.StateChanged += (_, changed) =>
            Raise(() => StateChanged?.Invoke(this, changed), nameof(StateChanged));
    }

    /// <inheritdoc/>
    public void OnRequest(LinkRequestHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _host.SetRequestHandler((_, request, ct) => handler(request, ct));
    }

    /// <inheritdoc/>
    public void OnTransfer(LinkTransferHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _host.SetTransferHandler((_, transfer, ct) => handler(transfer, ct));
    }

    /// <inheritdoc/>
    public async Task<byte[]> RequestAsync(ReadOnlyMemory<byte> request, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ILinkPeer peer = await ThePeerAsync(_options.RequestDeadline, cancellationToken).ConfigureAwait(false);
        return await peer.RequestAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task NotifyAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ILinkPeer peer = await ThePeerAsync(_options.RequestDeadline, cancellationToken).ConfigureAwait(false);
        await peer.NotifyAsync(message, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task SendAsync(
        Stream content,
        TransferOffer offer,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ILinkPeer peer = await ThePeerAsync(_options.TransferStallTimeout, cancellationToken).ConfigureAwait(false);
        await peer.SendAsync(content, offer, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public void OnChannel(string name, Func<ILinkChannelReader, CancellationToken, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _host.OnChannel(name, (_, channel, ct) => handler(channel, ct));
    }

    /// <inheritdoc/>
    public async Task<ILinkChannelWriter> OpenChannelAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ILinkPeer peer = await ThePeerAsync(_options.RequestDeadline, cancellationToken).ConfigureAwait(false);
        return await peer.OpenChannelAsync(name, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public Task<InvitationCode> RenewInvitationAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _host.RenewInvitationAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task WaitUntilConnectedAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _host.WaitForPeerAsync(mustBeConnected: true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The one machine at the other end, waiting for it to pair if it has not
    /// yet.
    /// </summary>
    /// <remarks>
    /// Only for the peer to <em>exist</em>, not to be connected: from there
    /// the peer's own retry loop waits for a session, which is where the
    /// caller's deadline is really spent.
    /// </remarks>
    private async Task<ILinkPeer> ThePeerAsync(TimeSpan patience, CancellationToken cancellationToken)
    {
        using CancellationTokenSource expiry = new(patience, _options.TimeProvider);
        using CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, expiry.Token);
        try
        {
            return await _host.WaitForPeerAsync(mustBeConnected: false, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (expiry.IsCancellationRequested)
        {
            throw new LinkTimeoutException($"nothing is paired with this machine yet; gave up after {patience}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new LinkClosedException("the link was closed");
        }
    }

    /// <summary>
    /// Runs an application's event handler where its failure stays its own: a
    /// handler that throws must not take the host's loops down with it.
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

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        await _host.DisposeAsync().ConfigureAwait(false);
    }
}

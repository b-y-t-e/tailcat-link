// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using Microsoft.Extensions.Hosting;

namespace Tailcat.Link.Extensions.DependencyInjection;

/// <summary>
/// Owns one <see cref="ILinkHost"/> for as long as the application runs.
/// </summary>
/// <remarks>
/// The host is built once, when the application starts, and disposed once,
/// when it stops. Dispose ordering against a supervision loop is exactly the
/// thing every consumer would otherwise hand-roll and get subtly wrong.
/// </remarks>
/// <param name="appName">The name the pairing is stored under.</param>
/// <param name="options">Builds the options, from the application's services.</param>
/// <param name="services">Where those services come from.</param>
public sealed class LinkHostSource(
    string appName,
    Func<IServiceProvider, LinkOptions> options,
    IServiceProvider services) : IAsyncDisposable, IDisposable
{
    private readonly SemaphoreSlim _starting = new(1, 1);
    private readonly Lock _mu = new();
    private readonly List<Action<ILinkHost>> _pending = [];
    private ILinkHost? _host;
    private bool _disposed;

    /// <summary>
    /// The running host.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Before the application has started. Nothing is blocked on here on
    /// purpose: a service provider that waits on a network is a service
    /// provider that deadlocks.
    /// </exception>
    /// <exception cref="ObjectDisposedException">
    /// After the application has stopped, told apart from "not yet" because
    /// the two want opposite things of the caller.
    /// </exception>
    public ILinkHost Host
    {
        get
        {
            lock (_mu)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _host ?? throw new InvalidOperationException(
                    $"the link host for \"{appName}\" is not up yet; it is built when the application starts");
            }
        }
    }

    /// <summary>
    /// Applies a registration to the running host, or holds it until the host
    /// is built and applies it then, in the order the registrations arrived.
    /// </summary>
    /// <remarks>
    /// A handler is the one thing an application must be able to set before
    /// the link is up: a worker taking <see cref="ILinkHost"/> subscribes to
    /// <see cref="ILinkHost.PeerJoined"/> in its constructor, which runs while
    /// the container is being built, and a worker registered before this
    /// package's own has its <c>StartAsync</c> run first. A peer that joins in
    /// the host's first moment would otherwise have nothing listening for it.
    /// </remarks>
    /// <param name="register">What to do to the host, once there is one.</param>
    public void Register(Action<ILinkHost> register)
    {
        ArgumentNullException.ThrowIfNull(register);
        lock (_mu)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_host is null)
            {
                _pending.Add(register);
                return;
            }
            // Inside the lock so that a registration arriving as the host comes
            // up cannot overtake the ones already waiting: order matters when
            // one of them is a handler another replaces.
            register(_host);
        }
    }

    /// <summary>
    /// Undoes a registration on the running host, or does nothing once there
    /// is no host left to undo it on.
    /// </summary>
    /// <remarks>
    /// Unsubscribing is the mirror of <see cref="Register"/> and not the same
    /// shape: a worker that subscribes in its constructor unsubscribes in its
    /// <c>Dispose</c>, which the container runs *after* this source has been
    /// disposed by the hosted service's shutdown. Throwing there would take
    /// the application's shutdown down with it, and there is nothing left to
    /// unsubscribe from anyway.
    /// </remarks>
    /// <param name="unregister">What to undo on the host, if there is one.</param>
    public void Unregister(Action<ILinkHost> unregister)
    {
        ArgumentNullException.ThrowIfNull(unregister);
        lock (_mu)
        {
            if (_disposed)
            {
                return;
            }
            if (_host is null)
            {
                // Held in the same queue, so that a subscription taken back
                // before the host is built does not reappear when it is.
                _pending.Add(unregister);
                return;
            }
            unregister(_host);
        }
    }

    /// <summary>Builds the host, or returns the one already built.</summary>
    public async Task<ILinkHost> StartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _starting.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_host is not null)
            {
                return _host;
            }

            ILinkHost host = await TailcatLink
                .HostManyAsync(appName, options(services), cancellationToken).ConfigureAwait(false);
            bool abandoned;
            lock (_mu)
            {
                // Checked again here: disposal does not wait on the build, so an
                // application shutting down while the node is still coming up
                // would otherwise publish a host nothing is left to dispose —
                // and a node with its relay connections would run to the end of
                // the process.
                abandoned = _disposed;
                if (!abandoned)
                {
                    foreach (Action<ILinkHost> register in _pending)
                    {
                        register(host);
                    }
                    _pending.Clear();
                    // Published last, so that nothing reaches the host between its
                    // handlers being set and the first peer being let in.
                    _host = host;
                }
            }

            if (abandoned)
            {
                await host.DisposeAsync().ConfigureAwait(false);
                throw new ObjectDisposedException(
                    nameof(LinkHostSource),
                    $"the link host for \"{appName}\" was shut down while it was still coming up");
            }
            return host;
        }
        finally
        {
            _starting.Release();
        }
    }

    /// <summary>Takes the host down when the container is disposed synchronously.</summary>
    /// <remarks>
    /// The container's own <c>Dispose</c> refuses a singleton that is only
    /// <see cref="IAsyncDisposable"/>, and <c>using IHost host =
    /// builder.Build()</c> is how most applications are written — which is
    /// exactly the shutdown this package exists to stop anybody hand-rolling.
    /// Prefer <see cref="DisposeAsync"/> wherever there is a way to await.
    /// </remarks>
    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
        GC.SuppressFinalize(this);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        ILinkHost? host;
        lock (_mu)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            host = _host;
            _host = null;
            // Registrations nobody will ever apply: the host they were waiting
            // for is not going to be built.
            _pending.Clear();
        }

        if (host is not null)
        {
            await host.DisposeAsync().ConfigureAwait(false);
        }
        // The semaphore is deliberately left alive: a build still in flight
        // releases it in its own finally, and disposing it here would turn that
        // release into an ObjectDisposedException thrown out of the
        // application's start-up. It holds nothing that leaks.
    }
}

/// <summary>Gives <see cref="LinkHostSource"/> the application's lifetime.</summary>
/// <param name="source">The host to bring up and take down.</param>
internal sealed class LinkHostService(LinkHostSource source) : IHostedService
{
    /// <inheritdoc/>
    public async Task StartAsync(CancellationToken cancellationToken) =>
        await source.StartAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>Takes the host down when the application stops.</summary>
    /// <remarks>
    /// Here rather than at the container's disposal: between the two, an
    /// application's own services are already stopping, and a link still up
    /// would go on admitting peers and running their requests into handlers
    /// whose dependencies are being taken away. An application that stops its
    /// host without disposing the container would otherwise leave a node and
    /// its relay connections running for good.
    /// </remarks>
    public Task StopAsync(CancellationToken cancellationToken) => source.DisposeAsync().AsTask();
}

/// <summary>
/// The <see cref="ILinkHost"/> that gets injected: every member is answered by
/// the one <see cref="LinkHostSource"/> holds, looked up per call.
/// </summary>
/// <remarks>
/// Not the host itself, because a service provider hands out constructor
/// arguments before any hosted service has run: <c>MyWorker(ILinkHost host)</c>
/// is constructed while the link is still being built, and injecting the real
/// host would fail there — in the most obvious use this package has. Storing
/// one of these costs nothing, and the first member called after the
/// application started answers from the running host.
///
/// Setting a handler or subscribing to an event is held rather than answered
/// that way: a worker that subscribes in its constructor, or in a
/// <c>StartAsync</c> that runs before this package's own, is registered on the
/// host the moment it is built. Everything else needs a host that exists and
/// says so.
/// </remarks>
/// <param name="source">Where the running host comes from.</param>
internal sealed class LinkHostProxy(LinkHostSource source) : ILinkHost, IDisposable
{
    /// <inheritdoc/>
    public InvitationCode InvitationCode => source.Host.InvitationCode;

    /// <inheritdoc/>
    public DateTimeOffset? InvitationExpiresAt => source.Host.InvitationExpiresAt;

    /// <inheritdoc/>
    public IReadOnlyList<LinkInvitation> Invitations => source.Host.Invitations;

    /// <inheritdoc/>
    public IReadOnlyList<ILinkPeer> Peers => source.Host.Peers;

    /// <inheritdoc/>
    public int MaxPeers => source.Host.MaxPeers;

    /// <inheritdoc/>
    public event EventHandler<PeerEventArgs>? PeerJoined
    {
        add => source.Register(host => host.PeerJoined += value);
        remove => source.Unregister(host => host.PeerJoined -= value);
    }

    /// <inheritdoc/>
    public event EventHandler<PeerLeftEventArgs>? PeerLeft
    {
        add => source.Register(host => host.PeerLeft += value);
        remove => source.Unregister(host => host.PeerLeft -= value);
    }

    /// <inheritdoc/>
    public void SetRequestHandler(LinkPeerRequestHandler handler) =>
        source.Register(host => host.SetRequestHandler(handler));

    /// <inheritdoc/>
    public void SetTransferHandler(LinkPeerTransferHandler handler) =>
        source.Register(host => host.SetTransferHandler(handler));

    /// <inheritdoc/>
    public void OnChannel(string name, LinkChannelHandler handler) =>
        source.Register(host => host.OnChannel(name, handler));

    /// <inheritdoc/>
    public Task<LinkInvitation> InviteAsync(
        InvitationRequest? request = null,
        CancellationToken cancellationToken = default) =>
        source.Host.InviteAsync(request, cancellationToken);

    /// <inheritdoc/>
    public Task<bool> RevokeInvitationAsync(Guid invitationId, CancellationToken cancellationToken = default) =>
        source.Host.RevokeInvitationAsync(invitationId, cancellationToken);

    /// <inheritdoc/>
    public Task ForgetPeerAsync(ILinkPeer peer, CancellationToken cancellationToken = default) =>
        source.Host.ForgetPeerAsync(peer, cancellationToken);

    /// <inheritdoc/>
    public Task<ILinkPeer> WaitForPeerAsync(CancellationToken cancellationToken = default) =>
        source.Host.WaitForPeerAsync(cancellationToken);

    /// <summary>Takes the host down, as disposing the injected host should.</summary>
    /// <remarks>
    /// Through the source, so that the hosted service's own shutdown and this
    /// one are the same idempotent path rather than two ends racing.
    /// </remarks>
    public ValueTask DisposeAsync() => source.DisposeAsync();

    /// <summary>Takes the host down when the container is disposed synchronously.</summary>
    /// <remarks>
    /// For the same reason <see cref="LinkHostSource.Dispose"/> exists: a
    /// container disposed synchronously refuses a singleton that is only
    /// <see cref="IAsyncDisposable"/>, and this one is registered as
    /// <see cref="ILinkHost"/> in every application that uses the package.
    /// </remarks>
    public void Dispose() => source.Dispose();
}

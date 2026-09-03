// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using Tailcat.Keys;
using Tailcat.Net;

namespace Tailcat.Link.Transport;

/// <summary>
/// The gateway every application outside a test uses: a real
/// <see cref="TailcatNode"/> on Tailscale's public relays.
/// </summary>
public sealed class TailcatNodeGatewayFactory : INodeGatewayFactory
{
    /// <summary>How long a session handshake may take before it is given up on.</summary>
    /// <remarks>
    /// Generous by default: the two machines meet through a shared public
    /// relay, and a loaded one takes its time. Giving up early only means
    /// starting over, which is slower still.
    /// </remarks>
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Where the node reports what it is doing. Nothing, by default.</summary>
    public ITailcatObserver Observer { get; init; } = NullTailcatObserver.Instance;

    /// <inheritdoc/>
    public async Task<INodeGateway> CreateAsync(
        NodePrivate privateKey,
        int? homeRegionId,
        CancellationToken cancellationToken = default)
    {
        TailcatNode node = await TailcatNode.CreateAsync(
            new TailcatNodeOptions
            {
                PrivateKey = privateKey,
                HomeRegionId = homeRegionId,
                HandshakeTimeout = HandshakeTimeout,
                Observer = Observer,
            },
            cancellationToken).ConfigureAwait(false);
        return new TailcatNodeGateway(node);
    }
}

/// <summary>A gateway backed by one live node.</summary>
internal sealed class TailcatNodeGateway(TailcatNode node) : INodeGateway
{
    private readonly TailcatNode _node = node;
    private bool _disposed;

    public ConnBlob Address => _node.Address;

    public int HomeRegionId => _node.HomeRegionId;

    public async Task<ITailcatConnection> ConnectAsync(ConnBlob peer, CancellationToken cancellationToken = default) =>
        await _node.ConnectAsync(peer, cancellationToken).ConfigureAwait(false);

    public async Task<ITailcatConnection> AcceptAsync(CancellationToken cancellationToken = default) =>
        await _node.AcceptConnectionAsync(cancellationToken).ConfigureAwait(false);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        await _node.DisposeAsync().ConfigureAwait(false);
    }
}

// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using Tailcat.Link.Storage;

namespace Tailcat.Link.Transport;

/// <summary>
/// The one node a host and every one of its peers share, and the only place
/// that throws it away and builds another.
/// </summary>
/// <remarks>
/// A machine has one identity and one address, so it has one node however
/// many peers it holds. Rebuilding is the blunt repair for a network stack
/// that broke underneath — a laptop resumed from sleep behind another NAT —
/// and putting it here is what stops several peers from each deciding to do
/// it at once.
/// </remarks>
internal sealed class NodeHolder(INodeGatewayFactory factory, Func<LinkState> state) : IAsyncDisposable
{
    private readonly Lock _mu = new();
    private readonly SemaphoreSlim _building = new(1, 1);
    private INodeGateway? _gateway;
    private bool _disposed;

    /// <summary>The node as it stands, or null while there is none.</summary>
    public INodeGateway? Current
    {
        get
        {
            lock (_mu)
            {
                return _gateway;
            }
        }
    }

    /// <summary>Takes over a node that has already been built.</summary>
    public void Adopt(INodeGateway gateway)
    {
        lock (_mu)
        {
            _gateway = gateway;
        }
    }

    /// <summary>Returns the node, building it from the stored identity if there is none.</summary>
    /// <remarks>
    /// One at a time, and never a spare. A host has two callers here — its
    /// accept loop and every peer's supervision loop — and letting both build
    /// a node was not the harmless waste it looked like. A node announces
    /// itself to the relay the moment it exists and the relay keeps one
    /// connection per key, so the loser's login displaced the winner's and was
    /// then thrown away. What was left was a machine that believed it was
    /// listening and whose published address had nobody behind it. Relayed,
    /// that is unreachable outright; where QUIC could find a direct path it
    /// stayed hidden.
    /// </remarks>
    public async Task<INodeGateway> EnsureAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Current is { } existing)
        {
            return existing;
        }

        await _building.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Asked again with the turn in hand: whoever held it has just
            // built the node this caller was about to build a second one of.
            if (Current is { } waited)
            {
                return waited;
            }
            ObjectDisposedException.ThrowIf(_disposed, this);

            // Same key and, for a host, the same pinned region: the rebuilt
            // node has the address the peer already has.
            LinkState stored = state();
            INodeGateway built = await factory
                .CreateAsync(stored.PrivateKey, stored.HomeRegionId, cancellationToken).ConfigureAwait(false);
            Adopt(built);
            return built;
        }
        finally
        {
            _building.Release();
        }
    }

    /// <summary>Throws the node away, so the next <see cref="EnsureAsync"/> builds a fresh one.</summary>
    public async Task DiscardAsync()
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

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        await DiscardAsync().ConfigureAwait(false);
        _building.Dispose();
    }
}

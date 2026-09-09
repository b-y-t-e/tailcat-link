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
    public async Task<INodeGateway> EnsureAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Current is { } existing)
        {
            return existing;
        }

        // Same key and, for a host, the same pinned region: the rebuilt node
        // has the address the peer already has.
        LinkState stored = state();
        INodeGateway built = await factory
            .CreateAsync(stored.PrivateKey, stored.HomeRegionId, cancellationToken).ConfigureAwait(false);

        INodeGateway? spare = null;
        INodeGateway held;
        lock (_mu)
        {
            if (_gateway is null)
            {
                _gateway = built;
            }
            else
            {
                spare = built;
            }
            // Taken under the same lock that chose the winner. Reading it
            // afterwards would let a DiscardAsync in between leave nothing to
            // return but the node this call is about to dispose, and the
            // caller would dial a disposed one.
            held = _gateway;
        }
        if (spare is not null)
        {
            // Somebody else got there first while this one was being built.
            await spare.DisposeAsync().ConfigureAwait(false);
        }
        return held;
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
    }
}

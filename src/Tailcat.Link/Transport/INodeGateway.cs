// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using Tailcat.Keys;
using Tailcat.Net;

namespace Tailcat.Link.Transport;

/// <summary>
/// The one thing a link needs from the network: an address to publish, and a
/// way to get a session to a peer — by dialling one or by accepting one.
/// </summary>
/// <remarks>
/// A gateway is disposable and replaceable on purpose. When a machine's
/// network changes underneath it badly enough that its node cannot recover,
/// the link throws the gateway away and builds another from the same stored
/// identity, which is indistinguishable to the peer from the machine having
/// rebooted.
/// </remarks>
public interface INodeGateway : IAsyncDisposable
{
    /// <summary>This machine's address, which is what an invitation code carries.</summary>
    ConnBlob Address { get; }

    /// <summary>The relay region this machine listens in.</summary>
    int HomeRegionId { get; }

    /// <summary>Opens a session to <paramref name="peer"/>.</summary>
    Task<TailcatConnection> ConnectAsync(ConnBlob peer, CancellationToken cancellationToken = default);

    /// <summary>Waits for a peer to open a session to this machine.</summary>
    Task<TailcatConnection> AcceptAsync(CancellationToken cancellationToken = default);
}

/// <summary>Builds gateways from a stored identity.</summary>
public interface INodeGatewayFactory
{
    /// <summary>
    /// Brings up a node under <paramref name="privateKey"/>.
    /// </summary>
    /// <param name="privateKey">The machine's stored identity.</param>
    /// <param name="homeRegionId">
    /// The relay region to listen in, pinning the machine's address. Null
    /// measures the closest region, which a host must only do once — see
    /// <see cref="Storage.LinkState.HomeRegionId"/>.
    /// </param>
    /// <param name="cancellationToken">Cancels bringing the node up.</param>
    Task<INodeGateway> CreateAsync(
        NodePrivate privateKey,
        int? homeRegionId,
        CancellationToken cancellationToken = default);
}

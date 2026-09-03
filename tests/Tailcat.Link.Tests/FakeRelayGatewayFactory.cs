// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using Tailcat.Derp;
using Tailcat.Keys;
using Tailcat.Link.Transport;
using Tailcat.Net;
using Tailcat.Tailcfg;

namespace Tailcat.Link.Tests;

/// <summary>
/// Builds real nodes — the whole handshake, QUIC and all — against an
/// in-memory relay, so the link can be taken apart and put back together in a
/// test without a public relay or a second machine.
/// </summary>
internal sealed class FakeRelayGatewayFactory(
    FakeDerpRelay relay,
    IReadOnlyList<PeerTransport>? transports = null) : INodeGatewayFactory
{
    /// <summary>The one region the fake relay serves.</summary>
    public const int RegionId = 950;

    private readonly Lock _mu = new();
    private readonly List<TailcatNode> _nodes = [];

    /// <summary>How many nodes have been built, so a test can see a rebuild happen.</summary>
    public int NodesCreated { get; private set; }

    public async Task<INodeGateway> CreateAsync(
        NodePrivate privateKey,
        int? homeRegionId,
        CancellationToken cancellationToken = default)
    {
        TailcatNode node = await TailcatNode.CreateAsync(
            new TailcatNodeOptions
            {
                PrivateKey = privateKey,
                // Null lets the node work it out; a test that means to
                // exercise the relayed transport names it, because a pair
                // that can do QUIC always does.
                Transports = transports,
                DerpMap = new DerpMap
                {
                    Regions =
                    {
                        [RegionId] = new DerpRegion
                        {
                            RegionID = RegionId,
                            Nodes = [new DerpNode { Name = "fake", HostName = "relay.invalid" }],
                        },
                    },
                },
                HomeRegionId = RegionId,
                // Both machines are on loopback, so they already know every
                // address the other can reach them at; asking a STUN server
                // that is not there would only cost the handshake its timeout.
                StunServers = [],
                HandshakeTimeout = TimeSpan.FromSeconds(8),
                ConnectRelay = async (_, token) => await DerpClient.ConnectOverStreamAsync(
                    await relay.DialAsync(token), privateKey, relay.PublicKey, token),
            },
            cancellationToken);

        lock (_mu)
        {
            _nodes.Add(node);
            NodesCreated++;
        }
        return new TailcatNodeGateway(node);
    }

    /// <summary>
    /// Destroys every node built so far, the way a machine losing its network
    /// stack does: no goodbye to the peer, and nothing that will ever work
    /// again until a new node is built from the same stored key.
    /// </summary>
    public async Task BreakEveryNodeAsync()
    {
        TailcatNode[] nodes;
        lock (_mu)
        {
            nodes = [.. _nodes];
            _nodes.Clear();
        }
        foreach (TailcatNode node in nodes)
        {
            await node.DisposeAsync();
        }
    }
}

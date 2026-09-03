// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using Tailcat.Keys;

namespace Tailcat.Net;

/// <summary>
/// A live session with another node: reliable, encrypted, and carrying as
/// many independent streams as the two sides want.
/// </summary>
/// <remarks>
/// <para>
/// This is deliberately the smallest thing a session can be, because it is
/// what the layers above are written against. Everything they do — the
/// pairing handshake, one stream per request — needs only a peer to name and
/// streams to open and accept, so anything that can carry ordered bytes
/// between two authenticated nodes can be a session, whether or not it is
/// <see cref="TailcatConnection"/>'s QUIC.
/// </para>
/// <para>
/// What is missing from it is as deliberate: which path traffic takes, and
/// whether it moved, belong to a session built on <see cref="PeerLink"/> and
/// have no meaning for one that is relayed for its whole life.
/// </para>
/// </remarks>
public interface ITailcatConnection : IAsyncDisposable
{
    /// <summary>The node on the other end.</summary>
    NodePublic Peer { get; }

    /// <summary>Opens a new bidirectional stream to the peer.</summary>
    Task<Stream> OpenStreamAsync(CancellationToken cancellationToken = default);

    /// <summary>Waits for the peer to open a stream.</summary>
    Task<Stream> AcceptStreamAsync(CancellationToken cancellationToken = default);
}

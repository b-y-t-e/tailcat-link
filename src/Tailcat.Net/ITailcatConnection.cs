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
/// Where traffic is flowing belongs here too, because "the relay" is an
/// answer as real as a punched-open address: a caller deciding whether a
/// session is fast enough, or an operator asking why it is not, needs the
/// same question answered whatever is carrying it.
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

    /// <summary>How traffic is currently reaching the peer.</summary>
    PeerPath CurrentPath { get; }

    /// <summary>Every path known to this session, and what is known about it.</summary>
    IReadOnlyList<PeerPath> Paths { get; }

    /// <summary>
    /// Raised when traffic moves to a different path. A session that can only
    /// ever be relayed never raises it.
    /// </summary>
    event Action<PeerPath>? PathChanged;

    /// <summary>
    /// Waits until traffic is flowing over a direct path, or the timeout
    /// passes.
    /// </summary>
    /// <remarks>
    /// Hole punching may simply fail — between two sufficiently hostile NATs
    /// there is no direct path — and a relayed transport has none by
    /// definition. Both answer false and keep working.
    /// </remarks>
    /// <returns>True if a direct path is in use.</returns>
    async Task<bool> WaitForDirectPathAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            while (CurrentPath.Kind != PeerPathKind.Direct)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), cts.Token).ConfigureAwait(false);
            }
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }
}

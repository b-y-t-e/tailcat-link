// Copyright (c) Tailscale Inc & contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Net.Quic;
using Tailcat.Keys;

namespace Tailcat.Net;

/// <summary>
/// A live session with another node: reliable, encrypted, and carrying as
/// many independent streams as the two sides want.
/// </summary>
/// <remarks>
/// The session survives the path underneath it changing. It starts out
/// relayed and moves to a direct path as soon as one is punched open, without
/// interrupting any stream; <see cref="CurrentPath"/> says where it is now,
/// and <see cref="PathChanged"/> fires when it moves.
/// </remarks>
public sealed class TailcatConnection : IAsyncDisposable
{
    private readonly QuicConnection _quic;
    private readonly PeerLink _link;
    private readonly UdpBridge _bridge;
    private readonly Func<TailcatConnection, ValueTask>? _onClosed;
    private bool _disposed;

    internal TailcatConnection(
        QuicConnection quic,
        PeerLink link,
        UdpBridge bridge,
        NodePublic peer,
        Func<TailcatConnection, ValueTask>? onClosed = null)
    {
        _quic = quic;
        _link = link;
        _bridge = bridge;
        Peer = peer;
        // The node keeps a session per peer, and closing here is the only
        // signal it gets that this one is over. Without it the session and its
        // link outlive the connection, and every receive loop keeps searching
        // them for the rest of the node's life.
        _onClosed = onClosed;
        _link.PathChanged += OnPathChanged;
    }

    /// <summary>The node on the other end.</summary>
    public NodePublic Peer { get; }

    /// <summary>How traffic is currently reaching the peer.</summary>
    public PeerPath CurrentPath => _link.CurrentPath;

    /// <summary>Every candidate path and what is known about it.</summary>
    public IReadOnlyList<PeerPath> Paths => _link.Paths;

    /// <summary>Raised when traffic moves to a different path.</summary>
    public event Action<PeerPath>? PathChanged;

    /// <summary>Opens a new bidirectional stream to the peer.</summary>
    public async Task<Stream> OpenStreamAsync(CancellationToken cancellationToken = default) =>
        await _quic.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, cancellationToken).ConfigureAwait(false);

    /// <summary>Waits for the peer to open a stream.</summary>
    public async Task<Stream> AcceptStreamAsync(CancellationToken cancellationToken = default) =>
        await _quic.AcceptInboundStreamAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Waits until traffic is flowing over a direct path, or the timeout
    /// passes. Hole punching may simply fail — between two sufficiently
    /// hostile NATs there is no direct path — in which case the session stays
    /// on the relay and keeps working.
    /// </summary>
    /// <returns>True if a direct path is in use.</returns>
    public async Task<bool> WaitForDirectPathAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
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

    private void OnPathChanged(PeerPath path) => PathChanged?.Invoke(path);

    /// <summary>Closes the session and stops probing paths.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        _link.PathChanged -= OnPathChanged;
        await _quic.DisposeAsync().ConfigureAwait(false);
        await _bridge.DisposeAsync().ConfigureAwait(false);
        await _link.DisposeAsync().ConfigureAwait(false);
        if (_onClosed is not null)
        {
            await _onClosed(this).ConfigureAwait(false);
        }
    }
}

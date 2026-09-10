// Copyright (c) Andrzej Ból and contributors
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
public sealed class TailcatConnection : ITailcatConnection
{
    private readonly QuicConnection _quic;
    private readonly PeerLink _link;
    private readonly UdpBridge _bridge;
    private readonly Func<TailcatConnection, ValueTask>? _onClosed;
    private volatile bool _disposed;

    // Why the node ended this session, when the node is what ended it. Null
    // means nothing but the owner disposing its own connection, which is the
    // one case where ObjectDisposedException is the truth. Both fields are
    // volatile because the reason is written on the node's thread and read on
    // the caller's — including from the streams handed out, which read it
    // long after they were made: without that, a reader on a weakly ordered
    // machine may take the flag and still miss the reason, and report the
    // disposal this change exists to stop reporting.
    private volatile string? _endedBecause;

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
    public async Task<Stream> OpenStreamAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            QuicStream opened = await _quic.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, cancellationToken)
                .ConfigureAwait(false);
            return Carrying(opened);
        }
        catch (Exception ended) when (IsEndOfSession(ended) && _endedBecause is { } why)
        {
            throw new TailcatException(why, ended);
        }
    }

    /// <summary>Waits for the peer to open a stream.</summary>
    public async Task<Stream> AcceptStreamAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            QuicStream accepted = await _quic.AcceptInboundStreamAsync(cancellationToken).ConfigureAwait(false);
            return Carrying(accepted);
        }
        catch (Exception ended) when (IsEndOfSession(ended) && _endedBecause is { } why)
        {
            // A serve loop parked here is the usual way the layer above hears
            // that a session is over, and QuicConnection can only say that
            // something was disposed or aborted — which names the type that
            // noticed and nothing that happened.
            throw new TailcatException(why, ended);
        }
    }

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

    // What closing the QUIC connection underneath looks like to a caller,
    // whether it was already parked in a call or made a fresh one. Only ever
    // consulted once the node has said why it took the session away, so a
    // connection the peer aborted still surfaces QUIC's own account of it.
    private static bool IsEndOfSession(Exception ex) => ex is ObjectDisposedException or QuicException;

    // A stream handed out now will still be being read when the node takes
    // the session away, and that read is where the layer above actually hears
    // about it. The relayed transport tells its streams the same thing.
    private SessionStream Carrying(QuicStream stream) => new(stream, () => _endedBecause);

    private void OnPathChanged(PeerPath path) => PathChanged?.Invoke(path);

    /// <summary>Ends this session because the node did, saying why.</summary>
    /// <param name="reason">
    /// What the owner of this connection is to be told when it next uses it.
    /// A session the node takes away is an ordinary end — the peer re-dialled,
    /// the node was shut down — and the layer above repairs all of them by
    /// reconnecting. It only has to be told which, and the node is the only
    /// thing that knows.
    /// </param>
    internal async ValueTask CloseAsync(string reason)
    {
        _endedBecause = reason;
        await DisposeAsync().ConfigureAwait(false);
    }

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

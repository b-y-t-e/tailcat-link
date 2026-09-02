// Copyright (c) Tailscale Inc & contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Net.Sockets;
using System.Threading.Channels;
using Tailcat.Keys;
using Tailcat.Tailcfg;

namespace Tailcat.Derp;

/// <summary>
/// Somewhere packets can be sent addressed by a peer's public key.
/// </summary>
/// <remarks>
/// It exists so that callers depend on "a relay" rather than on one TCP
/// connection to one, which is what lets a connection be replaced underneath
/// them — and lets tests substitute one entirely.
/// </remarks>
public interface IRelay
{
    /// <summary>The key this node is reachable at through the relay.</summary>
    NodePublic PublicKey { get; }

    /// <summary>Sends a packet to <paramref name="destination"/>. Delivery is best effort.</summary>
    Task SendAsync(NodePublic destination, ReadOnlyMemory<byte> packet, CancellationToken cancellationToken = default);

    /// <summary>Packets arriving from other nodes.</summary>
    ChannelReader<DerpReceivedPacket> Packets { get; }
}

/// <summary>
/// A relay connection that survives the relay going away: it reconnects,
/// with backoff, and keeps the same node key so peers can still reach it.
/// </summary>
/// <remarks>
/// A DERP connection is a single long-lived TCP connection, and those end —
/// the relay restarts, a NAT drops the mapping, the network changes. Without
/// this, one dropped connection would silently end a node's ability to be
/// reached, since a relay is how peers find each other in the first place.
/// </remarks>
public sealed class DerpConnection : IRelay, IAsyncDisposable
{
    // Backoff between reconnection attempts: fast at first, since most drops
    // are momentary, then backing off so a relay that is down isn't hammered.
    private static readonly TimeSpan MinBackoff = TimeSpan.FromMilliseconds(200);
    // How long a connection must last to count as healthy. Below it, a relay
    // that accepts and immediately drops would be retried five times a second.
    private static readonly TimeSpan StablePeriod = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    private readonly Func<CancellationToken, Task<DerpClient>> _connect;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<DerpReceivedPacket> _packets =
        Channel.CreateUnbounded<DerpReceivedPacket>(new UnboundedChannelOptions { SingleWriter = true });
    private readonly SemaphoreSlim _clientMu = new(1, 1);

    private DerpClient _client;
    private Task? _receiveLoop;
    private bool _disposed;

    private DerpConnection(Func<CancellationToken, Task<DerpClient>> connect, DerpClient client, TimeProvider time)
    {
        _connect = connect;
        _client = client;
        _time = time;
    }

    /// <summary>Connects to <paramref name="node"/> and keeps the connection up.</summary>
    public static Task<DerpConnection> ConnectAsync(
        DerpNode node,
        NodePrivate privateKey,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(node);
        return ConnectAsync(ct => DerpClient.ConnectAsync(node, privateKey, ct), timeProvider, cancellationToken);
    }

    /// <summary>
    /// Connects using <paramref name="connect"/>, which is called again for
    /// every reconnection.
    /// </summary>
    /// <remarks>
    /// Taking the dial step as a function lets a caller choose a different
    /// relay node on a retry, and lets tests stand one up in memory.
    /// </remarks>
    public static async Task<DerpConnection> ConnectAsync(
        Func<CancellationToken, Task<DerpClient>> connect,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connect);
        DerpClient client = await connect(cancellationToken).ConfigureAwait(false);
        DerpConnection connection = new(connect, client, timeProvider ?? TimeProvider.System);
        connection._receiveLoop = Task.Run(() => connection.ReceiveLoopAsync(connection._cts.Token), CancellationToken.None);
        return connection;
    }

    /// <inheritdoc/>
    public NodePublic PublicKey => _client.PublicKey;

    /// <summary>The relay's own public key.</summary>
    public NodePublic ServerPublicKey => _client.ServerPublicKey;

    /// <inheritdoc/>
    public ChannelReader<DerpReceivedPacket> Packets => _packets.Reader;

    /// <summary>How many times the connection has been re-established.</summary>
    public int ReconnectCount { get; private set; }

    /// <summary>Raised after the connection has been re-established.</summary>
    public event Action? Reconnected;

    /// <inheritdoc/>
    public async Task SendAsync(NodePublic destination, ReadOnlyMemory<byte> packet, CancellationToken cancellationToken = default)
    {
        try
        {
            await _client.SendAsync(destination, packet, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or DerpProtocolException)
        {
            // The connection is on its way out and the receive loop is already
            // reconnecting. This packet is lost, which callers must tolerate
            // from a relay anyway.
        }
    }

    // What one reconnection attempt did. The distinction matters: backing off
    // is the answer to a relay that cannot be reached, not to one that comes
    // straight back.
    private enum ReconnectOutcome
    {
        Connected,
        Failed,
        Cancelled,
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        TimeSpan backoff = MinBackoff;
        long connectedAt = _time.GetTimestamp();
        while (!ct.IsCancellationRequested)
        {
            try
            {
                DerpReceivedPacket packet = await _client.ReceiveAsync(ct).ConfigureAwait(false);
                await _packets.Writer.WriteAsync(packet, ct).ConfigureAwait(false);
                backoff = MinBackoff;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is EndOfStreamException or IOException or DerpProtocolException or ObjectDisposedException)
            {
                if (ct.IsCancellationRequested)
                {
                    return;
                }

                // A connection that stood for a while and then dropped says
                // nothing about the relay's availability, so the next attempt
                // starts from the floor again. Only a relay that keeps
                // refusing — or that accepts and drops us straight away —
                // earns a longer wait.
                bool wasStable = _time.GetElapsedTime(connectedAt) >= StablePeriod;
                switch (await ReconnectAsync(backoff, ct).ConfigureAwait(false))
                {
                    case ReconnectOutcome.Cancelled:
                        return;
                    case ReconnectOutcome.Connected:
                        connectedAt = _time.GetTimestamp();
                        backoff = wasStable ? MinBackoff : Grow(backoff);
                        break;
                    default:
                        backoff = Grow(backoff);
                        break;
                }
            }
        }
    }

    private static TimeSpan Grow(TimeSpan backoff) =>
        backoff >= MaxBackoff ? MaxBackoff : backoff * 2;

    private async Task<ReconnectOutcome> ReconnectAsync(TimeSpan backoff, CancellationToken ct)
    {
        try
        {
            await Task.Delay(backoff, _time, ct).ConfigureAwait(false);

            await _clientMu.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                DerpClient replacement = await _connect(ct).ConfigureAwait(false);
                DerpClient old = _client;
                _client = replacement;
                await old.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                _clientMu.Release();
            }

            ReconnectCount++;
            Reconnected?.Invoke();
            return ReconnectOutcome.Connected;
        }
        catch (OperationCanceledException)
        {
            return ReconnectOutcome.Cancelled;
        }
        catch (Exception ex) when (ex is IOException or SocketException or DerpProtocolException)
        {
            // Still unreachable; the loop tries again with a longer backoff.
            return ReconnectOutcome.Failed;
        }
    }

    /// <summary>Closes the connection and stops reconnecting.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        await _cts.CancelAsync().ConfigureAwait(false);
        _packets.Writer.TryComplete();
        await _client.DisposeAsync().ConfigureAwait(false);
        if (_receiveLoop is not null)
        {
            try
            {
                await _receiveLoop.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
            }
        }
        _clientMu.Dispose();
        _cts.Dispose();
    }
}

// Copyright (c) Andrzej Ból and contributors
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

    private readonly DerpLiveness _liveness;
    private readonly DerpLivenessWatch _watch;
    private readonly LostPacketResender _resender;

    private DerpClient _client;
    private Task? _receiveLoop;
    private Task? _livenessLoop;
    private bool _disposed;

    private DerpConnection(
        Func<CancellationToken, Task<DerpClient>> connect,
        DerpClient client,
        TimeProvider time,
        DerpLiveness liveness)
    {
        _connect = connect;
        _client = client;
        _time = time;
        _liveness = liveness;
        _watch = new DerpLivenessWatch(liveness, time, () => _client, AbandonAsync);
        _resender = new LostPacketResender(time);
        _client.Notice += OnNotice;
        _client.FrameReceived += OnFrameReceived;
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
    public static Task<DerpConnection> ConnectAsync(
        Func<CancellationToken, Task<DerpClient>> connect,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default) =>
        ConnectAsync(connect, DerpLiveness.Default, timeProvider, cancellationToken);

    /// <summary>
    /// Connects using <paramref name="connect"/>, checking the connection's
    /// liveness on <paramref name="liveness"/>'s clock — shortened in tests.
    /// </summary>
    /// <param name="connect">Dials one connection; called again for every reconnection.</param>
    /// <param name="liveness">How a connection that stopped carrying bytes is noticed.</param>
    /// <param name="timeProvider">The clock, for backoff and liveness.</param>
    /// <param name="cancellationToken">Cancels the first connection attempt.</param>
    internal static async Task<DerpConnection> ConnectAsync(
        Func<CancellationToken, Task<DerpClient>> connect,
        DerpLiveness liveness,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connect);
        DerpClient client = await connect(cancellationToken).ConfigureAwait(false);
        ArgumentNullException.ThrowIfNull(liveness);
        DerpConnection connection = new(connect, client, timeProvider ?? TimeProvider.System, liveness);
        connection._receiveLoop = Task.Run(() => connection.ReceiveLoopAsync(connection._cts.Token), CancellationToken.None);
        if (connection._liveness.Enabled)
        {
            connection._livenessLoop = Task.Run(() => connection._watch.RunAsync(connection._cts.Token), CancellationToken.None);
        }
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

    /// <summary>
    /// Raised for what the relay says that is not a packet, across
    /// reconnections. See <see cref="DerpNotice"/>.
    /// </summary>
    public event Action<DerpNotice>? Notice;

    /// <summary>
    /// How many connections were abandoned because they stopped carrying
    /// bytes without ending. See <see cref="DerpLiveness"/>.
    /// </summary>
    internal int StalledCount => _watch.StalledCount;

    /// <summary>Raised, on the receiving task, for every frame the relay sends. For tests.</summary>
    internal event Action? FrameReceived;

    /// <summary>Raised with the pause before each reconnection attempt. For tests.</summary>
    internal event Action<TimeSpan>? ReconnectScheduled;

    /// <summary>Runs one liveness check now, for tests whose liveness never checks on its own.</summary>
    internal Task CheckLivenessAsync(CancellationToken cancellationToken) => _watch.CheckAsync(cancellationToken);

    /// <inheritdoc/>
    public async Task SendAsync(NodePublic destination, ReadOnlyMemory<byte> packet, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!await _resender.RecordAsync(destination, packet, cancellationToken).ConfigureAwait(false))
            {
                return; // goes out with the resend, on the replacement
            }
            _watch.Sent();
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

                // Until the first attempt at a replacement is over, sends wait,
                // so that what the dead connection lost goes out again ahead of
                // them. Only the first: a relay that stays unreachable must not
                // hold every send back for as long as it is down.
                _resender.BeginReplacement(_watch.LastFrameAt);

                // A connection that stood for a while and then dropped says
                // nothing about the relay's availability, so the next attempt
                // starts from the floor again. Only a relay that keeps
                // refusing — or that accepts and drops us straight away —
                // earns a longer wait.
                //
                // A connection this end abandoned for going silent counts as
                // having held: it was up and carrying bytes until something in
                // the path stopped it. Backing off from it would turn a network
                // that cuts flows every few seconds into a node that is
                // unreachable most of the time.
                bool wasStable = _time.GetElapsedTime(connectedAt) >= StablePeriod
                    || _watch.WasAbandoned(_client);
                switch (await ReconnectAsync(backoff, ct).ConfigureAwait(false))
                {
                    case ReconnectOutcome.Cancelled:
                        _resender.ReplacementEnded();
                        return;
                    case ReconnectOutcome.Connected:
                        _resender.ReplacementEnded();
                        connectedAt = _time.GetTimestamp();
                        backoff = wasStable ? MinBackoff : Grow(backoff);
                        break;
                    default:
                        _resender.ReplacementFailed();
                        backoff = Grow(backoff);
                        break;
                }
            }
        }
    }

    private void OnNotice(DerpNotice notice) => Notice?.Invoke(notice);

    private void OnFrameReceived()
    {
        _watch.FrameReceived();
        _resender.Answered();
        FrameReceived?.Invoke();
    }

    // Closes client if it is still the current connection and stillCondemned
    // holds with the connection held steady. Closing the client ends the
    // receive loop's read, and the loop reconnects as it would for any dropped
    // connection.
    private async Task AbandonAsync(DerpClient client, Func<bool> stillCondemned, CancellationToken ct)
    {
        await _clientMu.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (ReferenceEquals(client, _client) && stillCondemned())
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _clientMu.Release();
        }
    }

    // Resends what the dead connection may have swallowed and puts
    // <paramref name="replacement"/> in place of it. Called under _clientMu.
    private async Task AdoptAsync(DerpClient replacement, CancellationToken ct)
    {
        DerpClient old = _client;
        await _resender.ResendAsync(replacement, _liveness.Timeout, () => SwitchTo(replacement), ct).ConfigureAwait(false);

        // After the resend, because an answer forgets everything sent more
        // than a moment ago.
        OnFrameReceived(); // the handshake itself was an answer
        await old.DisposeAsync().ConfigureAwait(false);
    }

    private void SwitchTo(DerpClient replacement)
    {
        _client.Notice -= OnNotice;
        _client.FrameReceived -= OnFrameReceived;
        replacement.Notice += OnNotice;
        replacement.FrameReceived += OnFrameReceived;
        _client = replacement;
    }

    private static TimeSpan Grow(TimeSpan backoff) =>
        backoff >= MaxBackoff ? MaxBackoff : backoff * 2;

    private async Task<ReconnectOutcome> ReconnectAsync(TimeSpan backoff, CancellationToken ct)
    {
        try
        {
            ReconnectScheduled?.Invoke(backoff);
            await Task.Delay(backoff, _time, ct).ConfigureAwait(false);

            await _clientMu.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                DerpClient replacement = await _connect(ct).ConfigureAwait(false);
                try
                {
                    await AdoptAsync(replacement, ct).ConfigureAwait(false);
                }
                catch
                {
                    // A replacement that never became _client is nobody's to
                    // close later, and leaving it open logs a second client in
                    // under this node's key — which is the very state a relay
                    // reports as "another client connected".
                    await replacement.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
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
        foreach (Task? loop in new[] { _receiveLoop, _livenessLoop })
        {
            if (loop is null)
            {
                continue;
            }
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
            }
        }
        _clientMu.Dispose();
        _cts.Dispose();
    }
}

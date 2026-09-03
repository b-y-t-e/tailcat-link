// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Net;
using System.Net.Sockets;

namespace Tailcat.Net;

/// <summary>
/// Carries a local QUIC endpoint's datagrams over a <see cref="PeerLink"/>.
/// </summary>
/// <remarks>
/// <para>
/// The platform QUIC stack insists on speaking to a UDP socket, and cannot be
/// handed a custom transport. So the bridge gives it one: a loopback socket
/// that stands in for the peer. Datagrams the QUIC stack sends there are
/// forwarded over whichever path the link currently prefers, and datagrams
/// arriving from the peer are delivered back to the QUIC stack as if they had
/// come straight from that address.
/// </para>
/// <para>
/// This indirection is also what makes the relay-to-direct switch invisible:
/// QUIC always sees one stable peer address — the bridge — while underneath,
/// the link moves between the relay and a punched-open UDP path. This is the
/// same trick Tailscale's magicsock plays on WireGuard.
/// </para>
/// </remarks>
public sealed class UdpBridge : IAsyncDisposable
{
    private readonly Socket _socket;
    private readonly PeerLink _link;
    private readonly CancellationTokenSource _cts = new();
    private readonly Lock _mu = new();
    private Task? _pump;
    private IPEndPoint? _quicEndPoint;
    private bool _disposed;

    /// <summary>
    /// Creates a bridge for <paramref name="link"/>.
    /// </summary>
    /// <param name="link">The link to the peer.</param>
    /// <param name="quicEndPoint">
    /// Where the local QUIC stack listens, for the accepting side. The
    /// connecting side leaves this null: its QUIC client dials
    /// <see cref="LocalEndPoint"/>, and the bridge learns its address from the
    /// first datagram.
    /// </param>
    public UdpBridge(PeerLink link, IPEndPoint? quicEndPoint = null)
    {
        ArgumentNullException.ThrowIfNull(link);
        _link = link;
        _quicEndPoint = quicEndPoint;

        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        _link.DatagramReceived += OnDatagramFromPeer;
    }

    /// <summary>
    /// The loopback address that stands in for the peer. The connecting side's
    /// QUIC client dials this.
    /// </summary>
    public IPEndPoint LocalEndPoint => (IPEndPoint)_socket.LocalEndPoint!;

    /// <summary>Starts pumping datagrams in both directions.</summary>
    public void Start() => _pump ??= Task.Run(() => PumpAsync(_cts.Token));

    private void OnDatagramFromPeer(ReadOnlyMemory<byte> datagram)
    {
        IPEndPoint? target;
        lock (_mu)
        {
            target = _quicEndPoint;
        }
        if (target is null)
        {
            // Nothing local to hand it to yet: the QUIC stack hasn't spoken.
            return;
        }
        // The datagram points into the receive loop's shared buffer, which is
        // overwritten by the next packet. The send below outlives this call, so
        // it must own its bytes — without the copy, QUIC intermittently reads
        // another packet's contents, which looks like random corruption only
        // once a direct path is in use and only under load.
        byte[] owned = datagram.ToArray();

        // Fire and forget: a datagram that can't be delivered is simply lost,
        // which is what QUIC already copes with. The continuation is not
        // optional — a send still in flight when the socket closes faults, and
        // an unobserved fault surfaces later, far from here, as a
        // TaskScheduler.UnobservedTaskException about a bridge nobody kept.
        _ = _socket.SendToAsync(owned, SocketFlags.None, target, _cts.Token)
            .AsTask()
            .ContinueWith(
                static t => _ = t.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        byte[] buffer = new byte[64 * 1024];
        EndPoint any = new IPEndPoint(IPAddress.Loopback, 0);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                SocketReceiveFromResult res = await _socket
                    .ReceiveFromAsync(buffer, SocketFlags.None, any, ct)
                    .ConfigureAwait(false);

                lock (_mu)
                {
                    // The connecting side learns its QUIC client's ephemeral
                    // port here, with the first packet it sends.
                    _quicEndPoint ??= (IPEndPoint)res.RemoteEndPoint;
                }

                await _link.SendDatagramAsync(buffer.AsMemory(0, res.ReceivedBytes), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                // A transient socket error shouldn't kill the bridge; QUIC
                // will retransmit whatever was lost.
                if (ex is ObjectDisposedException)
                {
                    return;
                }
            }
        }
    }

    /// <summary>Stops the bridge and closes its socket.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        _link.DatagramReceived -= OnDatagramFromPeer;
        await _cts.CancelAsync().ConfigureAwait(false);
        _socket.Dispose();
        if (_pump is not null)
        {
            try
            {
                await _pump.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
        _cts.Dispose();
    }
}

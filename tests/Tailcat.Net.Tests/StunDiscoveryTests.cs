// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Tailcat.Derp;
using Tailcat.Keys;
using Tailcat.Tailcfg;

namespace Tailcat.Net.Tests;

/// <summary>
/// Covers how a node learns the public address peers must aim at to punch a
/// hole to it.
/// </summary>
/// <remarks>
/// This is the quietest failure in the library: a node that learns nothing
/// advertises only its LAN addresses, so a peer on another network has
/// nothing to try, every session stays on the relay, and everything still
/// works — only slowly. It was also the real state of affairs, because none
/// of the relays in tailcat's DERP map answer STUN.
/// </remarks>
public class StunDiscoveryTests
{
    private const int RegionId = 900;

    // Answers a binding request with the address it came from, which is what
    // a STUN server on the far side of a NAT would report.
    private sealed class FakeStunServer : IDisposable
    {
        private readonly Socket _socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        private readonly CancellationTokenSource _cts = new();

        public FakeStunServer(IPEndPoint? reportInstead = null)
        {
            _socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            EndPoint = (IPEndPoint)_socket.LocalEndPoint!;
            Reported = reportInstead;
            _ = Task.Run(LoopAsync);
        }

        public IPEndPoint EndPoint { get; }

        public IPEndPoint? Reported { get; set; }

        public int Requests { get; private set; }

        private async Task LoopAsync()
        {
            byte[] buf = new byte[1500];
            while (!_cts.IsCancellationRequested)
            {
                SocketReceiveFromResult got;
                try
                {
                    got = await _socket.ReceiveFromAsync(buf, new IPEndPoint(IPAddress.Any, 0), _cts.Token);
                }
                catch (Exception ex) when (ex is SocketException or ObjectDisposedException or OperationCanceledException)
                {
                    return;
                }

                if (!Stun.IsStunPacket(buf.AsSpan(0, got.ReceivedBytes)))
                {
                    continue;
                }
                Requests++;

                IPEndPoint report = Reported ?? (IPEndPoint)got.RemoteEndPoint;
                await _socket.SendToAsync(
                    BuildResponse(buf.AsSpan(8, Stun.TransactionIdLen), report), got.RemoteEndPoint, _cts.Token);
            }
        }

        private static byte[] BuildResponse(ReadOnlySpan<byte> transactionId, IPEndPoint mapped)
        {
            // Header, then one XOR-MAPPED-ADDRESS attribute: the port and the
            // address are xored with the magic cookie.
            byte[] msg = new byte[Stun.HeaderLen + 12];
            BinaryPrimitives.WriteUInt16BigEndian(msg, Stun.BindingSuccessResponse);
            BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(2), 12);
            BinaryPrimitives.WriteUInt32BigEndian(msg.AsSpan(4), Stun.MagicCookie);
            transactionId.CopyTo(msg.AsSpan(8));

            Span<byte> attr = msg.AsSpan(Stun.HeaderLen);
            BinaryPrimitives.WriteUInt16BigEndian(attr, Stun.XorMappedAddress);
            BinaryPrimitives.WriteUInt16BigEndian(attr[2..], 8);
            attr[4] = 0;
            attr[5] = 1; // IPv4
            BinaryPrimitives.WriteUInt16BigEndian(attr[6..], (ushort)(mapped.Port ^ (Stun.MagicCookie >> 16)));

            Span<byte> cookie = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(cookie, Stun.MagicCookie);
            byte[] address = mapped.Address.GetAddressBytes();
            for (int i = 0; i < 4; i++)
            {
                attr[8 + i] = (byte)(address[i] ^ cookie[i]);
            }
            return msg;
        }

        public void Dispose()
        {
            _cts.Cancel();
            _socket.Dispose();
            _cts.Dispose();
        }
    }

    private static TailcatNodeOptions OptionsFor(
        FakeDerpRelay relay,
        NodePrivate key,
        IReadOnlyList<IPEndPoint>? stunServers,
        IReadOnlyList<string>? fallbackHosts) => new()
        {
            PrivateKey = key,
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
            StunServers = stunServers,
            StunFallbackHosts = fallbackHosts ?? [],
            ConnectRelay = async (_, token) => await DerpClient.ConnectOverStreamAsync(
                await relay.DialAsync(token), key, relay.PublicKey, token),
        };

    /// <summary>
    /// The address a STUN server reports is advertised to peers. Without it
    /// nothing a peer receives is reachable from another network.
    /// </summary>
    [Fact]
    public async Task TheAddressStunReportsIsAdvertisedToPeers()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using FakeDerpRelay relay = new();
        using FakeStunServer stun = new(reportInstead: new IPEndPoint(IPAddress.Parse("203.0.113.7"), 41234));

        await using TailcatNode node = await TailcatNode.CreateAsync(
            OptionsFor(relay, NodePrivate.NewKey(), [stun.EndPoint], null), ct);

        IReadOnlyList<IPEndPoint> endpoints = await node.LocalEndpointsAsync(ct);

        Assert.Contains(new IPEndPoint(IPAddress.Parse("203.0.113.7"), 41234), endpoints);
    }

    /// <summary>
    /// A DERP map whose relays do not run STUN — which is every relay in
    /// tailcat's own map — must not leave the node without a public address.
    /// The servers named in the map answered nothing here, so the fallback is
    /// the only thing standing between this node and a lifetime on the relay.
    /// </summary>
    [Fact]
    public async Task ServersThatNeverAnswerFallBackToTheOnesThatDo()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using FakeDerpRelay relay = new();
        using FakeStunServer working = new(reportInstead: new IPEndPoint(IPAddress.Parse("198.51.100.9"), 5555));

        // Reserved for documentation, so nothing is listening and nothing will
        // answer — the same silence a relay without STUN gives.
        IPEndPoint silent = new(IPAddress.Parse("192.0.2.1"), 3478);

        NodePrivate key = NodePrivate.NewKey();
        await using TailcatNode node = await TailcatNode.CreateAsync(
            new TailcatNodeOptions
            {
                PrivateKey = key,
                DerpMap = new DerpMap
                {
                    Regions =
                    {
                        [RegionId] = new DerpRegion
                        {
                            RegionID = RegionId,
                            Nodes =
                            [
                                new DerpNode
                                {
                                    Name = "fake",
                                    HostName = "relay.invalid",
                                    IPv4 = silent.Address.ToString(),
                                    STUNPort = silent.Port,
                                },
                            ],
                        },
                    },
                },
                HomeRegionId = RegionId,
                StunFallbackHosts = [$"127.0.0.1:{working.EndPoint.Port}"],
                ConnectRelay = async (_, token) => await DerpClient.ConnectOverStreamAsync(
                    await relay.DialAsync(token), key, relay.PublicKey, token),
            },
            ct);

        IReadOnlyList<IPEndPoint> endpoints = await node.LocalEndpointsAsync(ct);

        Assert.Contains(new IPEndPoint(IPAddress.Parse("198.51.100.9"), 5555), endpoints);
        Assert.True(working.Requests > 0, "the fallback server was never asked");
    }

    /// <summary>
    /// Naming the servers explicitly means those and no others: a node pinned
    /// to one network must not quietly reach a public server on the internet.
    /// </summary>
    [Fact]
    public async Task NamingTheServersTurnsTheFallbackOff()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using FakeDerpRelay relay = new();
        using FakeStunServer fallback = new();

        await using TailcatNode node = await TailcatNode.CreateAsync(
            OptionsFor(relay, NodePrivate.NewKey(), [], [$"127.0.0.1:{fallback.EndPoint.Port}"]), ct);

        await node.LocalEndpointsAsync(ct);

        Assert.Equal(0, fallback.Requests);
    }

    /// <summary>
    /// A NAT that moves this node's mapping must be noticed and announced.
    /// Nothing local changes when it happens — same interface, same local
    /// port — so no system event fires, and the peer goes on probing a port
    /// that no longer exists. That is what stalled the first test between two
    /// real NATs: the advertised port moved from 65264 to 65204 and the peer
    /// was never told.
    /// </summary>
    [Fact]
    public async Task AMovedNatMappingIsAnnouncedToLiveSessions()
    {
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromMinutes(2));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        IPEndPoint before = new(IPAddress.Parse("203.0.113.7"), 40001);
        IPEndPoint after = new(IPAddress.Parse("203.0.113.7"), 40002);
        using FakeStunServer stun = new(reportInstead: before);

        FakeTimeProvider time = new(DateTimeOffset.UnixEpoch);
        NodePrivate dialerKey = NodePrivate.NewKey();
        NodePrivate listenerKey = NodePrivate.NewKey();

        // Only the dialler's mapping moves; the listener is the end that has
        // to hear about it.
        await using TailcatNode listener = await TailcatNode.CreateAsync(
            OptionsWithTime(relay, listenerKey, [], time), ct);
        await using TailcatNode dialer = await TailcatNode.CreateAsync(
            OptionsWithTime(relay, dialerKey, [stun.EndPoint], time), ct);

        await using ITailcatConnection server = await AcceptWhileConnectingAsync(listener, dialer, ct);

        while (!server.Paths.Any(p => Equals(p.Remote, before)))
        {
            await Task.Delay(50, ct);
        }

        stun.Reported = after;
        while (!server.Paths.Any(p => Equals(p.Remote, after)))
        {
            // Drives the node's endpoint watch loop, which runs on this clock.
            time.Advance(TimeSpan.FromSeconds(25));
            await Task.Delay(100, ct);
        }

        Assert.Contains(server.Paths, p => Equals(p.Remote, after));
    }

    private static TailcatNodeOptions OptionsWithTime(
        FakeDerpRelay relay,
        NodePrivate key,
        IReadOnlyList<IPEndPoint> stunServers,
        TimeProvider time) => new()
        {
            PrivateKey = key,
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
            StunServers = stunServers,
            StunFallbackHosts = [],
            TimeProvider = time,
            ConnectRelay = async (_, token) => await DerpClient.ConnectOverStreamAsync(
                await relay.DialAsync(token), key, relay.PublicKey, token),
        };

    // QUIC opens streams lazily and the accepting side must already be
    // accepting when the dialler arrives, so the two are started together.
    private static async Task<ITailcatConnection> AcceptWhileConnectingAsync(
        TailcatNode listener,
        TailcatNode dialer,
        CancellationToken ct)
    {
        Task<ITailcatConnection> accepted = listener.AcceptConnectionAsync(ct);
        await dialer.ConnectAsync(listener.Address, ct);
        return await accepted;
    }
}

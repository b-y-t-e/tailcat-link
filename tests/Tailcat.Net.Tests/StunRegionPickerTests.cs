// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Tailcat.Tailcfg;

namespace Tailcat.Net.Tests;

/// <summary>
/// Covers picking the closest relay region, the port of Go's
/// <c>PickBestRegion</c>. Every relayed byte crosses the relay twice, so a
/// badly chosen region taxes the whole session.
/// </summary>
public class StunRegionPickerTests
{
    // A STUN server on loopback that answers after a settable delay, so a
    // "near" and a "far" region can be told apart without a network.
    private sealed class FakeStunServer : IAsyncDisposable
    {
        private readonly Socket _socket;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;

        public FakeStunServer(TimeSpan delay, bool answer = true)
        {
            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            _loop = Task.Run(() => ServeAsync(delay, answer, _cts.Token));
        }

        public IPEndPoint EndPoint => (IPEndPoint)_socket.LocalEndPoint!;

        private async Task ServeAsync(TimeSpan delay, bool answer, CancellationToken ct)
        {
            byte[] buf = new byte[1500];
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    SocketReceiveFromResult res = await _socket.ReceiveFromAsync(
                        buf, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), ct);
                    // The whole span lives inside this call: one may not be held
                    // across an await.
                    byte[]? reply = answer ? TryBuildReply(buf.AsSpan(0, res.ReceivedBytes)) : null;
                    if (reply is null)
                    {
                        continue;
                    }
                    await RespondAsync(reply, res.RemoteEndPoint, delay, ct);
                }
                catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
                {
                    return;
                }
            }
        }

        private static byte[]? TryBuildReply(ReadOnlySpan<byte> request) =>
            Stun.TryGetTransactionId(request, out ReadOnlySpan<byte> tid)
                ? BuildResponse(tid, new IPEndPoint(IPAddress.Parse("203.0.113.1"), 1234))
                : null;

        private async Task RespondAsync(byte[] reply, EndPoint replyTo, TimeSpan delay, CancellationToken ct)
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, ct);
            }
            await _socket.SendToAsync(reply, SocketFlags.None, replyTo, ct);
        }

        private static byte[] BuildResponse(ReadOnlySpan<byte> transactionId, IPEndPoint mapped)
        {
            byte[] addr = mapped.Address.GetAddressBytes();
            byte[] value = new byte[4 + addr.Length];
            value[1] = 0x01;
            BinaryPrimitives.WriteUInt16BigEndian(value.AsSpan(2), (ushort)(mapped.Port ^ (Stun.MagicCookie >> 16)));

            Span<byte> cookieAndTid = stackalloc byte[4 + Stun.TransactionIdLen];
            BinaryPrimitives.WriteUInt32BigEndian(cookieAndTid, Stun.MagicCookie);
            transactionId.CopyTo(cookieAndTid[4..]);
            for (int i = 0; i < addr.Length; i++)
            {
                value[4 + i] = (byte)(addr[i] ^ cookieAndTid[i]);
            }

            byte[] msg = new byte[Stun.HeaderLen + 4 + value.Length];
            BinaryPrimitives.WriteUInt16BigEndian(msg, Stun.BindingSuccessResponse);
            BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(2), (ushort)(4 + value.Length));
            BinaryPrimitives.WriteUInt32BigEndian(msg.AsSpan(4), Stun.MagicCookie);
            transactionId.CopyTo(msg.AsSpan(8));
            BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(Stun.HeaderLen), Stun.XorMappedAddress);
            BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(Stun.HeaderLen + 2), (ushort)value.Length);
            value.CopyTo(msg.AsSpan(Stun.HeaderLen + 4));
            return msg;
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            _socket.Dispose();
            try
            {
                await _loop;
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
            }
            _cts.Dispose();
        }
    }

    private static DerpRegion RegionAt(int id, IPEndPoint stun) => new()
    {
        RegionID = id,
        RegionCode = $"r{id}",
        Nodes = [new DerpNode { Name = $"{id}a", HostName = $"derp{id}.example", IPv4 = stun.Address.ToString(), STUNPort = stun.Port }],
    };

    private static DerpMap MapOf(params DerpRegion[] regions) => new() { Regions = regions.ToDictionary(r => r.RegionID) };

    [Fact]
    public async Task PicksTheRegionWithTheLowestLatency()
    {
        await using FakeStunServer near = new(TimeSpan.Zero);
        await using FakeStunServer far = new(TimeSpan.FromMilliseconds(250));

        DerpMap map = MapOf(RegionAt(1, far.EndPoint), RegionAt(2, near.EndPoint));
        StunRegionPicker picker = new(TimeSpan.FromSeconds(3));

        int best = await picker.PickBestRegionAsync(map, TestContext.Current.CancellationToken);

        Assert.Equal(2, best);
        Assert.Equal(2, picker.LastLatencies.Count);
        Assert.True(picker.LastLatencies[2] < picker.LastLatencies[1]);
    }

    /// <summary>A region that never answers is simply not ranked.</summary>
    [Fact]
    public async Task UnreachableRegionsAreSkipped()
    {
        await using FakeStunServer answering = new(TimeSpan.Zero);
        await using FakeStunServer silent = new(TimeSpan.Zero, answer: false);

        DerpMap map = MapOf(RegionAt(1, silent.EndPoint), RegionAt(2, answering.EndPoint));
        StunRegionPicker picker = new(TimeSpan.FromMilliseconds(300));

        int best = await picker.PickBestRegionAsync(map, TestContext.Current.CancellationToken);

        Assert.Equal(2, best);
        Assert.DoesNotContain(1, picker.LastLatencies.Keys);
    }

    /// <summary>
    /// Zero means "no measurement", which tells the caller to choose some
    /// other way rather than to fail.
    /// </summary>
    [Fact]
    public async Task NoAnswersMeansNoChoice()
    {
        await using FakeStunServer silent = new(TimeSpan.Zero, answer: false);

        DerpMap map = MapOf(RegionAt(1, silent.EndPoint));
        StunRegionPicker picker = new(TimeSpan.FromMilliseconds(200));

        Assert.Equal(0, await picker.PickBestRegionAsync(map, TestContext.Current.CancellationToken));
        Assert.Empty(picker.LastLatencies);
    }

    [Fact]
    public async Task EmptyMapMeansNoChoice()
    {
        StunRegionPicker picker = new(TimeSpan.FromMilliseconds(200));

        Assert.Equal(0, await picker.PickBestRegionAsync(new DerpMap(), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(0, Stun.DefaultPort)]  // 0 means the standard STUN port
    [InlineData(3479, 3479)]
    public void RegionStunPortIsResolved(int configured, int expected)
    {
        DerpRegion region = new()
        {
            RegionID = 1,
            Nodes = [new DerpNode { IPv4 = "192.0.2.1", STUNPort = configured }],
        };

        IPEndPoint? ep = StunRegionPicker.StunEndpointOf(region);

        Assert.NotNull(ep);
        Assert.Equal(expected, ep.Port);
    }

    /// <summary>
    /// When no relay runs STUN — the real state of tailcat's DERP map — the
    /// ranking must still tell regions apart. Without this the picker measures
    /// nothing, the caller takes the lowest-numbered region, and every node in
    /// the world lands on the same relay however far away it is.
    /// </summary>
    [Fact]
    public async Task RanksByTcpWhenNoRegionAnswersStun()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        // Listening, so its TCP handshake completes at once.
        using Socket reachable = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        reachable.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        reachable.Listen(4);
        IPEndPoint open = (IPEndPoint)reachable.LocalEndPoint!;

        // Documentation-reserved, so the connection attempt goes nowhere.
        DerpRegion unreachable = new()
        {
            RegionID = 1,
            Nodes = [new DerpNode { Name = "1a", HostName = "derp1.example", IPv4 = "192.0.2.1", STUNPort = -1, DERPPort = 443 }],
        };
        DerpRegion answering = new()
        {
            RegionID = 2,
            Nodes = [new DerpNode { Name = "2a", HostName = "derp2.example", IPv4 = open.Address.ToString(), STUNPort = -1, DERPPort = open.Port }],
        };

        StunRegionPicker picker = new(timeout: TimeSpan.FromMilliseconds(700));
        int best = await picker.PickBestRegionAsync(MapOf(unreachable, answering), ct);

        Assert.Equal(2, best);
    }

    /// <summary>A node marked as having no STUN (-1) can't be probed.</summary>
    [Fact]
    public void NodesWithoutStunAreSkipped()
    {
        DerpRegion region = new()
        {
            RegionID = 1,
            Nodes =
            [
                new DerpNode { IPv4 = "192.0.2.1", STUNPort = -1 },
                new DerpNode { IPv4 = "192.0.2.2", STUNPort = 3478 },
            ],
        };

        IPEndPoint? ep = StunRegionPicker.StunEndpointOf(region);

        Assert.Equal(IPAddress.Parse("192.0.2.2"), ep?.Address);
    }
}

// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Quic;
using System.Security.Cryptography;
using Tailcat.Keys;
using Tailcat.Link.Storage;

namespace Tailcat.Link.Tests;

/// <summary>
/// Two machines kept apart by a relay that keeps going dark: every few seconds
/// one of their relay connections stops carrying bytes without ending, the way
/// a firewall that has lost track of the flow drops it. Traffic runs all the
/// while, and everything sent has to arrive whole.
/// </summary>
/// <remarks>
/// <para>
/// This is the network a field report came from — a server behind a router
/// that cut every TCP flow a few hundred kilobytes in, and UDP too — and the
/// tests hold the library to what it achieved there: sessions that outlive
/// the cuts, with nothing configured.
/// </para>
/// <para>
/// Every option is left at its default on purpose. A test that tuned the
/// timeouts would prove only that some setting works; what has to be true is
/// that an application which sets nothing gets this.
/// </para>
/// <para>
/// Direct paths are switched off in the test gateway, because on one machine
/// loopback always punches and the traffic would never touch the relay being
/// broken.
/// </para>
/// </remarks>
public class RelayChaosTests
{
    private static readonly TimeSpan CutEvery = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RunFor = TimeSpan.FromSeconds(40);

    /// <summary>
    /// Over QUIC a cut relay connection is replaced before the session notices,
    /// so neither end ever sees the other leave, exchanges of every size come
    /// back intact, and a channel — which does not survive a session — loses
    /// no frame and gets none out of order.
    /// </summary>
    [Fact]
    public async Task AQuicSessionOutlivesARelayThatKeepsGoingDark()
    {
        if (!QuicListener.IsSupported)
        {
            Assert.Skip("this machine has no QUIC; Windows 10 has none and Linux needs libmsquic");
        }

        using CancellationTokenSource cts = LinkHarness.Deadline(RunFor + TimeSpan.FromMinutes(2));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        FakeRelayGatewayFactory gateways = new(relay) { DirectPaths = false };
        await using Pair pair = await Pair.ConnectAsync(gateways, ct);

        int sessionsLost = 0;
        pair.Host.PeerLeft += (_, _) => Interlocked.Increment(ref sessionsLost);
        pair.Guest.Disconnected += _ => Interlocked.Increment(ref sessionsLost);

        using ChannelCheck channel = pair.StartChannel(ct);
        Task<int> exchanges = pair.RunExchangesAsync(RunFor, ct);
        int cuts = await CutTheRelayAsync(relay, pair, RunFor, ct);

        int completed = await exchanges;
        await channel.FinishAsync(ct);

        Assert.True(cuts >= 6, $"only {cuts} cuts happened");
        Assert.True(completed > 20, $"only {completed} exchanges completed");
        Assert.Equal(0, Volatile.Read(ref sessionsLost));
        Assert.Equal(channel.Sent, channel.Received);
    }

    /// <summary>
    /// The relayed transport cannot keep a session through a cut — it has no
    /// retransmission — but nothing sent is lost: every exchange resumes on the
    /// next session and arrives whole.
    /// </summary>
    [Fact]
    public async Task ARelay1LinkLosesNoExchangeToARelayThatKeepsGoingDark()
    {
        using CancellationTokenSource cts = LinkHarness.Deadline(RunFor + TimeSpan.FromMinutes(2));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        FakeRelayGatewayFactory gateways = new(relay, [Tailcat.Net.PeerTransport.Relay1]);
        await using Pair pair = await Pair.ConnectAsync(gateways, ct);

        Task<int> exchanges = pair.RunExchangesAsync(RunFor, ct);
        int cuts = await CutTheRelayAsync(relay, pair, RunFor, ct);
        int completed = await exchanges;

        Assert.True(cuts >= 6, $"only {cuts} cuts happened");
        Assert.True(completed > 10, $"only {completed} exchanges completed");
    }

    /// <summary>
    /// The relay hanging up on both machines at once — a relay restarting —
    /// costs a QUIC session nothing either.
    /// </summary>
    [Fact]
    public async Task AQuicSessionOutlivesARelayThatHangsUpOnEveryone()
    {
        if (!QuicListener.IsSupported)
        {
            Assert.Skip("this machine has no QUIC; Windows 10 has none and Linux needs libmsquic");
        }

        using CancellationTokenSource cts = LinkHarness.Deadline(TimeSpan.FromMinutes(2));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        FakeRelayGatewayFactory gateways = new(relay) { DirectPaths = false };
        await using Pair pair = await Pair.ConnectAsync(gateways, ct);

        int sessionsLost = 0;
        pair.Host.PeerLeft += (_, _) => Interlocked.Increment(ref sessionsLost);

        Task<int> exchanges = pair.RunExchangesAsync(TimeSpan.FromSeconds(20), ct);
        for (int i = 0; i < 4; i++)
        {
            await Task.Delay(CutEvery, ct);
            relay.DisconnectClient(pair.HostKey);
            relay.DisconnectClient(pair.GuestKey);
        }

        Assert.True(await exchanges > 10);
        Assert.Equal(0, Volatile.Read(ref sessionsLost));
    }

    /// <summary>
    /// A relayed session with nothing to say stays up on the defaults. An
    /// application once had to set the heartbeat to five seconds because an
    /// idle relayed link dropped every ten; that must not be anybody's job.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnIdleRelayedSessionStaysUp(bool relay1)
    {
        if (!relay1 && !QuicListener.IsSupported)
        {
            Assert.Skip("this machine has no QUIC; Windows 10 has none and Linux needs libmsquic");
        }

        using CancellationTokenSource cts = LinkHarness.Deadline(TimeSpan.FromMinutes(3));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        FakeRelayGatewayFactory gateways = relay1
            ? new(relay, [Tailcat.Net.PeerTransport.Relay1])
            : new(relay) { DirectPaths = false };
        await using Pair pair = await Pair.ConnectAsync(gateways, ct);

        int sessionsLost = 0;
        pair.Host.PeerLeft += (_, _) => Interlocked.Increment(ref sessionsLost);
        pair.Guest.Disconnected += _ => Interlocked.Increment(ref sessionsLost);

        await Task.Delay(TimeSpan.FromSeconds(45), ct);

        Assert.Equal(0, Volatile.Read(ref sessionsLost));
        byte[] payload = RandomNumberGenerator.GetBytes(64);
        Assert.Equal(SHA256.HashData(payload), await pair.Guest.RequestAsync(payload, ct));
    }

    // Alternates between the two machines, so both ends' detection is exercised.
    private static async Task<int> CutTheRelayAsync(FakeDerpRelay relay, Pair pair, TimeSpan runFor, CancellationToken ct)
    {
        Stopwatch clock = Stopwatch.StartNew();
        int cuts = 0;
        while (clock.Elapsed + CutEvery < runFor)
        {
            await Task.Delay(CutEvery, ct);
            relay.Freeze(cuts % 2 == 0 ? pair.HostKey : pair.GuestKey);
            cuts++;
        }
        return cuts;
    }

    private sealed class Pair : IAsyncDisposable
    {
        private Pair(ILinkHost host, ILink guest)
        {
            Host = host;
            Guest = guest;
        }

        public ILinkHost Host { get; }

        public ILink Guest { get; }

        public NodePublic HostKey => Guest.Peer;

        public NodePublic GuestKey => Host.Peers[0].Key;

        public static async Task<Pair> ConnectAsync(FakeRelayGatewayFactory gateways, CancellationToken ct)
        {
            ILinkHost host = await TailcatLink.HostManyAsync(
                "chaos", new LinkOptions { Gateway = gateways, Store = new InMemoryLinkStore() }, ct);

            // The answer is the request's own hash, so a byte changed anywhere
            // on the way in is as visible as one changed on the way back.
            host.SetRequestHandler(new LinkPeerRequestHandler((_, request, _) =>
                Task.FromResult<ReadOnlyMemory<byte>>(SHA256.HashData(request.Span))));

            ILink guest = await TailcatLink.JoinAsync(
                "chaos",
                host.InvitationCode.Value,
                new LinkOptions { Gateway = gateways, Store = new InMemoryLinkStore() },
                ct);
            await guest.WaitUntilConnectedAsync(ct);
            await host.WaitForPeerAsync(ct);
            return new Pair(host, guest);
        }

        /// <summary>Sends requests of mixed sizes, one after another, checking every answer.</summary>
        public async Task<int> RunExchangesAsync(TimeSpan runFor, CancellationToken ct)
        {
            int[] sizes = [16, 1_000, 30_000, 300_000, 2_000_000];
            Stopwatch clock = Stopwatch.StartNew();
            int completed = 0;
            while (clock.Elapsed < runFor)
            {
                byte[] payload = RandomNumberGenerator.GetBytes(sizes[completed % sizes.Length]);
                byte[] answer = await Guest.RequestAsync(payload, ct);
                Assert.Equal(SHA256.HashData(payload), answer);
                completed++;
            }
            return completed;
        }

        public ChannelCheck StartChannel(CancellationToken ct) => new(this, ct);

        public async ValueTask DisposeAsync()
        {
            await Guest.DisposeAsync();
            await Host.DisposeAsync();
        }
    }

    /// <summary>Numbered frames one way, checked for gaps and order at the other end.</summary>
    private sealed class ChannelCheck : IDisposable
    {
        private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _sending;
        private int _received;
        private int _sent;
        private string? _problem;

        public ChannelCheck(Pair pair, CancellationToken ct)
        {
            pair.Host.OnChannel("numbers", async (_, channel, token) =>
            {
                int expected = 0;
                await foreach (ReadOnlyMemory<byte> frame in channel.ReadAllAsync(token))
                {
                    int number = BinaryPrimitives.ReadInt32BigEndian(frame.Span);
                    if (number != expected)
                    {
                        _problem ??= $"frame {number} arrived where {expected} was due";
                    }
                    expected = number + 1;
                    Interlocked.Increment(ref _received);
                }
                _done.TrySetResult();
            });

            _sending = Task.Run(async () =>
            {
                using CancellationTokenSource both = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
                await using ILinkChannelWriter writer = await pair.Guest.OpenChannelAsync("numbers", ct);
                byte[] frame = new byte[4 + 1024];
                try
                {
                    while (!both.IsCancellationRequested)
                    {
                        BinaryPrimitives.WriteInt32BigEndian(frame, _sent);
                        await writer.SendAsync(frame.ToArray(), both.Token);
                        Interlocked.Increment(ref _sent);
                        await Task.Delay(50, both.Token);
                    }
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested)
                {
                }
            }, ct);
        }

        public int Sent => Volatile.Read(ref _sent);

        public int Received => Volatile.Read(ref _received);

        public async Task FinishAsync(CancellationToken ct)
        {
            await _stop.CancelAsync();
            await _sending;
            await _done.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
            Assert.Null(_problem);
        }

        public void Dispose() => _stop.Dispose();
    }
}

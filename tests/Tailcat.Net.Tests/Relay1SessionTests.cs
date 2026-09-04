// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Net;
using System.Security.Cryptography;
using System.Text;
using Tailcat.Derp;
using Tailcat.Keys;
using Tailcat.Net.Relay1;
using Tailcat.Tailcfg;

namespace Tailcat.Net.Tests;

/// <summary>
/// Covers the transport that carries a session on the relay itself, for the
/// ends that cannot have QUIC — a browser, which has no UDP socket, and
/// Windows 10, which has no QUIC at all.
/// </summary>
/// <remarks>
/// Both nodes here are held to <see cref="PeerTransport.Relay1"/>, because a
/// pair that can do QUIC always does and the relayed path would otherwise
/// never run.
/// </remarks>
public class Relay1SessionTests
{
    private const int RegionId = 901;

    private static TailcatNodeOptions OptionsFor(FakeDerpRelay relay, NodePrivate key) => new()
    {
        PrivateKey = key,
        Transports = [PeerTransport.Relay1],
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
        StunServers = [],
        StunFallbackHosts = [],
        ConnectRelay = async (_, token) => await DerpClient.ConnectOverStreamAsync(
            await relay.DialAsync(token), key, relay.PublicKey, token),
    };

    private static async Task<TailcatNode> NodeAsync(FakeDerpRelay relay, CancellationToken ct)
    {
        NodePrivate key = NodePrivate.NewKey();
        return await TailcatNode.CreateAsync(OptionsFor(relay, key), ct);
    }

    private static async Task<(ITailcatConnection Client, ITailcatConnection Server)> PairAsync(
        FakeDerpRelay relay,
        TailcatNode listener,
        TailcatNode dialer,
        CancellationToken ct)
    {
        Task<ITailcatConnection> accepted = listener.AcceptConnectionAsync(ct);
        ITailcatConnection client = await dialer.ConnectAsync(listener.Address, ct);
        return (client, await accepted);
    }

    /// <summary>
    /// The whole point: two nodes with no QUIC between them still get a
    /// session, and it is the relay that carries it.
    /// </summary>
    [Fact]
    public async Task TwoNodesWithoutQuicStillExchangeAStream()
    {
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromMinutes(1));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        await using TailcatNode listener = await NodeAsync(relay, ct);
        await using TailcatNode dialer = await NodeAsync(relay, ct);

        (ITailcatConnection client, ITailcatConnection server) = await PairAsync(relay, listener, dialer, ct);
        await using (client)
        await using (server)
        {
            Assert.Equal(PeerPathKind.Relay, client.CurrentPath.Kind);
            Assert.Equal(PeerPathKind.Relay, server.CurrentPath.Kind);

            // QUIC opens streams lazily and so does this: the peer sees the
            // stream when the opener writes, never before.
            Stream outbound = await client.OpenStreamAsync(ct);
            await using (outbound)
            {
                await outbound.WriteAsync("hello relay"u8.ToArray(), ct);
                await outbound.FlushAsync(ct);

                Stream inbound = await server.AcceptStreamAsync(ct);
                await using (inbound)
                {
                    byte[] buf = new byte[64];
                    int read = await inbound.ReadAsync(buf, ct);
                    Assert.Equal("hello relay", Encoding.UTF8.GetString(buf, 0, read));

                    await inbound.WriteAsync("hello back"u8.ToArray(), ct);
                    await inbound.FlushAsync(ct);

                    int back = await outbound.ReadAsync(buf, ct);
                    Assert.Equal("hello back", Encoding.UTF8.GetString(buf, 0, back));
                }
            }
        }
    }

    /// <summary>
    /// Several streams at once, which is the other thing QUIC was providing.
    /// Ids from the two ends must not collide, and one stream's bytes must
    /// not land in another.
    /// </summary>
    [Fact]
    public async Task StreamsAreIndependentOfOneAnother()
    {
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromMinutes(1));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        await using TailcatNode listener = await NodeAsync(relay, ct);
        await using TailcatNode dialer = await NodeAsync(relay, ct);

        (ITailcatConnection client, ITailcatConnection server) = await PairAsync(relay, listener, dialer, ct);
        await using (client)
        await using (server)
        {
            // Answers whatever arrives, uppercased, one task per stream.
            Task serving = Task.Run(async () =>
            {
                for (int i = 0; i < 5; i++)
                {
                    Stream inbound = await server.AcceptStreamAsync(ct);
                    _ = Task.Run(async () =>
                    {
                        await using (inbound)
                        {
                            byte[] buf = new byte[128];
                            int read = await inbound.ReadAsync(buf, ct);
                            await inbound.WriteAsync(
                                Encoding.UTF8.GetBytes(
                                    Encoding.UTF8.GetString(buf, 0, read).ToUpperInvariant()),
                                ct);
                            await inbound.FlushAsync(ct);
                        }
                    }, ct);
                }
            }, ct);

            List<Task<string>> exchanges = [];
            for (int i = 0; i < 5; i++)
            {
                string sent = $"stream-{i}";
                exchanges.Add(Task.Run(async () =>
                {
                    Stream stream = await client.OpenStreamAsync(ct);
                    await using (stream)
                    {
                        await stream.WriteAsync(Encoding.UTF8.GetBytes(sent), ct);
                        await stream.FlushAsync(ct);
                        byte[] buf = new byte[128];
                        int read = await stream.ReadAsync(buf, ct);
                        return Encoding.UTF8.GetString(buf, 0, read);
                    }
                }, ct));
            }

            string[] answers = await Task.WhenAll(exchanges);
            await serving;
            Assert.Equal(
                ["STREAM-0", "STREAM-1", "STREAM-2", "STREAM-3", "STREAM-4"],
                answers.Order().ToArray());
        }
    }

    /// <summary>
    /// A payload far past one record, which is what exercises the chunking
    /// and the credit the receiver hands back. Without the credit a sender
    /// would push the lot at a relay that drops what it cannot deliver.
    /// </summary>
    [Fact]
    public async Task ALargePayloadArrivesWholeAndInOrder()
    {
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromMinutes(2));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        await using TailcatNode listener = await NodeAsync(relay, ct);
        await using TailcatNode dialer = await NodeAsync(relay, ct);

        (ITailcatConnection client, ITailcatConnection server) = await PairAsync(relay, listener, dialer, ct);
        await using (client)
        await using (server)
        {
            // Past the initial window, so the sender has to wait for credit
            // rather than sending it all at once.
            byte[] payload = RandomNumberGenerator.GetBytes((1024 * 1024) + 7);

            Stream outbound = await client.OpenStreamAsync(ct);
            Task sending = Task.Run(async () =>
            {
                await using (outbound)
                {
                    await outbound.WriteAsync(payload, ct);
                    await outbound.FlushAsync(ct);
                }
            }, ct);

            Stream inbound = await server.AcceptStreamAsync(ct);
            byte[] received;
            await using (inbound)
            {
                using MemoryStream sink = new();
                await inbound.CopyToAsync(sink, ct);
                received = sink.ToArray();
            }
            await sending;

            Assert.Equal(payload.Length, received.Length);
            Assert.True(payload.AsSpan().SequenceEqual(received), "the payload came back changed");
        }
    }

    /// <summary>
    /// Closing the session must not leave a reader waiting for bytes that
    /// will never come — the failure that a relayed link cannot see, because
    /// writing into a dead session succeeds.
    /// </summary>
    [Fact]
    public async Task ClosingTheSessionUnblocksItsStreams()
    {
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromMinutes(1));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        await using TailcatNode listener = await NodeAsync(relay, ct);
        await using TailcatNode dialer = await NodeAsync(relay, ct);

        (ITailcatConnection client, ITailcatConnection server) = await PairAsync(relay, listener, dialer, ct);
        await using (server)
        {
            Stream stream = await client.OpenStreamAsync(ct);
            await stream.WriteAsync("knock"u8.ToArray(), ct);
            await stream.FlushAsync(ct);
            await using Stream inbound = await server.AcceptStreamAsync(ct);
            byte[] buf = new byte[16];
            await inbound.ReadAsync(buf, ct);

            Task<int> waiting = Task.Run(async () => await stream.ReadAsync(buf, ct), ct);
            await client.DisposeAsync();

            // Either an orderly end or a stated failure; what must not happen
            // is waiting for good.
            try
            {
                Assert.Equal(0, await waiting);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>
    /// A FIN for a stream this end has already answered and closed — which is
    /// every request — must not raise the stream again. The JavaScript half
    /// retires the ids it has closed for exactly this; before the same
    /// retirement landed here, the host raised a phantom per request and its
    /// serving loop parked in <c>LinkFrame.ReadAsync</c> for good, one task
    /// and one stream leaked per request on a long-lived paired link.
    /// </summary>
    [Fact]
    public async Task AFinThatArrivesAfterThisEndClosedTheStreamDoesNotOpenAnotherOne()
    {
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromMinutes(1));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        await using TailcatNode listener = await NodeAsync(relay, ct);
        await using TailcatNode dialer = await NodeAsync(relay, ct);

        (ITailcatConnection client, ITailcatConnection server) = await PairAsync(relay, listener, dialer, ct);
        await using (client)
        await using (server)
        {
            Stream request = await client.OpenStreamAsync(ct);
            await request.WriteAsync("status"u8.ToArray(), ct);
            await request.FlushAsync(ct);

            Stream served = await server.AcceptStreamAsync(ct);
            byte[] buf = new byte[16];
            int asked = await served.ReadAsync(buf, ct);
            Assert.Equal("status", Encoding.UTF8.GetString(buf, 0, asked));
            await served.WriteAsync("STATUS"u8.ToArray(), ct);
            await served.FlushAsync(ct);
            // Closing the answered stream is what every server does; the
            // client's own FIN for the same id lands afterwards.
            await served.DisposeAsync();

            int back = await request.ReadAsync(buf, ct);
            Assert.Equal("STATUS", Encoding.UTF8.GetString(buf, 0, back));
            await request.DisposeAsync();

            // What the server accepts next must be the live stream, not a
            // phantom raised by the late FIN: the phantom would have been
            // queued first, and nothing would ever read it.
            Stream again = await client.OpenStreamAsync(ct);
            await again.WriteAsync("again"u8.ToArray(), ct);
            await again.FlushAsync(ct);

            Stream next = await server.AcceptStreamAsync(ct);
            Assert.Equal(3UL, ((Relay1Stream)next).Id);
            int read = await next.ReadAsync(buf, ct);
            Assert.Equal("again", Encoding.UTF8.GetString(buf, 0, read));
        }
    }

    /// <summary>
    /// The same when the peer's streams arrive out of turn: the .NET half
    /// numbers a stream before taking the lock that serialises its sending,
    /// so two concurrent requests can reach the relay with the higher id
    /// first. Retiring is per id — the run between them collapses — so a
    /// late FIN for the older of the two cannot reopen it either. The
    /// JavaScript half has this test for exactly that case.
    /// </summary>
    [Fact]
    public async Task AFinForTheOlderOfTwoOutOfOrderStreamsDoesNotReopenIt()
    {
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromMinutes(1));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        await using TailcatNode listener = await NodeAsync(relay, ct);
        await using TailcatNode dialer = await NodeAsync(relay, ct);

        (ITailcatConnection client, ITailcatConnection server) = await PairAsync(relay, listener, dialer, ct);
        await using (client)
        await using (server)
        {
            Stream older = await server.OpenStreamAsync(ct);
            Stream newer = await server.OpenStreamAsync(ct);
            await newer.WriteAsync("from 4"u8.ToArray(), ct);
            await newer.FlushAsync(ct);
            await older.WriteAsync("from 2"u8.ToArray(), ct);
            await older.FlushAsync(ct);

            Stream first = await client.AcceptStreamAsync(ct);
            Stream second = await client.AcceptStreamAsync(ct);
            Assert.Equal(4UL, ((Relay1Stream)first).Id);
            Assert.Equal(2UL, ((Relay1Stream)second).Id);

            // Closing both is what a server does; the listener's own FINs
            // for the same ids land afterwards.
            await first.DisposeAsync();
            await second.DisposeAsync();
            await older.DisposeAsync();
            await newer.DisposeAsync();

            Stream third = await server.OpenStreamAsync(ct);
            await third.WriteAsync("from 6"u8.ToArray(), ct);
            await third.FlushAsync(ct);

            Stream next = await client.AcceptStreamAsync(ct);
            Assert.Equal(6UL, ((Relay1Stream)next).Id);
            byte[] buf = new byte[16];
            int read = await next.ReadAsync(buf, ct);
            Assert.Equal("from 6", Encoding.UTF8.GetString(buf, 0, read));
        }
    }

    /// <summary>Every disposable here is idempotent, as everywhere else.</summary>
    [Fact]
    public async Task DisposingTwiceIsHarmless()
    {
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromMinutes(1));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        await using TailcatNode listener = await NodeAsync(relay, ct);
        await using TailcatNode dialer = await NodeAsync(relay, ct);

        (ITailcatConnection client, ITailcatConnection server) = await PairAsync(relay, listener, dialer, ct);
        await server.DisposeAsync();
        await client.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    /// <summary>
    /// A node held to the relayed transport still refuses a peer that asks
    /// for QUIC, and says so rather than timing out.
    /// </summary>
    [Fact]
    public async Task ANodeWithoutQuicRefusesAPeerThatOnlySpeaksIt()
    {
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromMinutes(1));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        await using TailcatNode listener = await NodeAsync(relay, ct);
        await relay.WaitForClientAsync(listener.PublicKey, ct);

        NodePrivate stranger = NodePrivate.NewKey();
        await using DerpClient client = await DerpClient.ConnectOverStreamAsync(
            await relay.DialAsync(ct), stranger, relay.PublicKey, ct);

        PeerHello hello = new(
            SessionId: 1,
            new byte[PeerHello.FingerprintLen],
            [],
            HomeRegionId: RegionId,
            Transports: [PeerTransport.Quic]);
        await client.SendAsync(
            listener.PublicKey,
            PeerMessage.Seal(PeerMessageType.Hello, hello.Encode(), stranger, listener.PublicKey),
            ct);

        DerpReceivedPacket answer = await client.ReceiveAsync(ct);
        Assert.True(PeerMessage.TryOpen(
            answer.Payload.Span, stranger, listener.PublicKey, out PeerMessageType type, out byte[]? payload));
        Assert.Equal(PeerMessageType.HelloAck, type);
        Assert.True(PeerHello.TryDecode(payload, out PeerHello? ack));

        Assert.Equal([PeerTransport.Relay1], ack.Transports);
        Assert.Equal(0, listener.SessionCount);
    }
}

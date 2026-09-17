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

// TAILCAT001: the sealed-message design is what this file implements and
// tests, so its experimental warning has nothing to tell it. Only a consumer
// reaching for Seal or TryOpen from outside is meant to hear it.
#pragma warning disable TAILCAT001

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

            // The knock only has to arrive; what it says is not the point, and
            // one read is allowed to return less than was asked for.
            Assert.NotEqual(0, await inbound.ReadAsync(buf, ct));

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

    /// <summary>
    /// A record that arrives a second time is ignored rather than taken for a
    /// gap. That is what a peer's relay connection dying looks like from here:
    /// it resends what it sent just before, not knowing what got through, and
    /// the session has to come out of that intact.
    /// </summary>
    [Fact]
    public async Task ARecordThatArrivesTwiceIsIgnored()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        NodePrivate dialerKey = NodePrivate.NewKey();
        NodePrivate hostKey = NodePrivate.NewKey();
        Relay1Ephemeral dialerHalf = new();
        Relay1Ephemeral hostHalf = new();
        Relay1Keys dialerKeys = dialerHalf.Derive(hostHalf.PublicKey, 9, dialerKey.Public(), hostKey.Public());
        Relay1Keys hostKeys = hostHalf.Derive(dialerHalf.PublicKey, 9, dialerKey.Public(), hostKey.Public());

        List<byte[]> records = [];
        await using Relay1Connection dialer = new(
            hostKey.Public(), dialerKeys, isDialer: true,
            (record, _) => { lock (records) { records.Add(record.ToArray()); } return Task.CompletedTask; });
        await using Relay1Connection host = new(
            dialerKey.Public(), hostKeys, isDialer: false, (_, _) => Task.CompletedTask);

        await using (Stream stream = await dialer.OpenStreamAsync(ct))
        {
            await stream.WriteAsync("one "u8.ToArray(), ct);
            await stream.WriteAsync("two "u8.ToArray(), ct);
            await stream.WriteAsync("three"u8.ToArray(), ct);
        }

        // Delivered with the first two again after the third, as a resend
        // following a dead relay connection would deliver them.
        byte[][] delivered = [records[0], records[1], records[2], records[0], records[1], records[3]];
        foreach (byte[] record in delivered)
        {
            Assert.Equal(Relay1RecordOutcome.Taken, host.HandleRecord(record));
        }

        await using Stream accepted = await host.AcceptStreamAsync(ct);
        using StreamReader reader = new(accepted);
        Assert.Equal("one two three", await reader.ReadToEndAsync(ct));
    }

    /// <summary>
    /// A send cancelled while the relay is busy does not cost the far end a
    /// record. The counter used to be spent before the send, so a cancellation
    /// that stopped the record from going out left a gap nothing had dropped —
    /// and the far end closed a session on a perfectly healthy relay.
    /// </summary>
    [Fact]
    public async Task ASendCancelledWhileTheRelayIsBusyLeavesNoGap()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        (Relay1Keys dialerKeys, Relay1Keys hostKeys, NodePublic dialerPublic, NodePublic hostPublic) = SessionKeys();

        // The first record waits for the relay, as a send does behind another
        // one's write, and gives up if its caller does.
        TaskCompletionSource relayBusy = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int held = 0;
        List<byte[]> records = [];
        await using Relay1Connection dialer = new(
            hostPublic, dialerKeys, isDialer: true,
            async (record, token) =>
            {
                if (Interlocked.Exchange(ref held, 1) == 0)
                {
                    await relayBusy.Task.WaitAsync(token);
                }
                lock (records)
                {
                    records.Add(record.ToArray());
                }
            });
        await using Relay1Connection host = new(
            dialerPublic, hostKeys, isDialer: false, (_, _) => Task.CompletedTask);

        await using (Stream stream = await dialer.OpenStreamAsync(ct))
        {
            using CancellationTokenSource caller = new();
            Task first = stream.WriteAsync("one "u8.ToArray(), caller.Token).AsTask();
            await caller.CancelAsync();
            relayBusy.SetResult();
            try
            {
                await first;
            }
            catch (OperationCanceledException)
            {
                // Either outcome is the caller's to see; a hole is not.
            }
            await stream.WriteAsync("two"u8.ToArray(), ct);
        }

        foreach (byte[] record in records)
        {
            Assert.NotEqual(Relay1RecordOutcome.SessionBroken, host.HandleRecord(record));
        }
    }

    /// <summary>
    /// A record sealed for another session is ignored, not taken as the end of
    /// this one. A machine whose relay connection died resends what it sent
    /// just before — and when the session those records belonged to has since
    /// been replaced, they arrive at the new one under keys it does not have.
    /// That ended every new session built straight after a cut.
    /// </summary>
    [Fact]
    public async Task ARecordFromAnotherSessionIsIgnored()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        (Relay1Keys dialerKeys, Relay1Keys hostKeys, NodePublic dialerPublic, NodePublic hostPublic) = SessionKeys();
        (Relay1Keys oldDialerKeys, _, _, _) = SessionKeys();

        List<byte[]> records = [];
        List<byte[]> oldRecords = [];
        await using Relay1Connection dialer = new(
            hostPublic, dialerKeys, isDialer: true,
            (record, _) => { lock (records) { records.Add(record.ToArray()); } return Task.CompletedTask; });
        await using Relay1Connection oldDialer = new(
            hostPublic, oldDialerKeys, isDialer: true,
            (record, _) => { lock (oldRecords) { oldRecords.Add(record.ToArray()); } return Task.CompletedTask; });
        await using Relay1Connection host = new(
            dialerPublic, hostKeys, isDialer: false, (_, _) => Task.CompletedTask);

        await using (Stream stale = await oldDialer.OpenStreamAsync(ct))
        {
            await stale.WriteAsync("from the session before"u8.ToArray(), ct);
        }
        await using (Stream stream = await dialer.OpenStreamAsync(ct))
        {
            await stream.WriteAsync("one "u8.ToArray(), ct);
            await stream.WriteAsync("two"u8.ToArray(), ct);
        }

        // The resend reaches the new session first, as it does after a cut.
        foreach (byte[] record in oldRecords)
        {
            Assert.Equal(Relay1RecordOutcome.Unopened, host.HandleRecord(record));
        }
        foreach (byte[] record in records)
        {
            Assert.Equal(Relay1RecordOutcome.Taken, host.HandleRecord(record));
        }

        await using Stream accepted = await host.AcceptStreamAsync(ct);
        using StreamReader reader = new(accepted);
        Assert.Equal("one two", await reader.ReadToEndAsync(ct));
    }

    /// <summary>
    /// A genuine record missing still ends the session: ignoring what does not
    /// open must not turn into ignoring a gap.
    /// </summary>
    [Fact]
    public async Task AGenuineRecordMissingStillEndsTheSession()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        (Relay1Keys dialerKeys, Relay1Keys hostKeys, NodePublic dialerPublic, NodePublic hostPublic) = SessionKeys();

        List<byte[]> records = [];
        await using Relay1Connection dialer = new(
            hostPublic, dialerKeys, isDialer: true,
            (record, _) => { lock (records) { records.Add(record.ToArray()); } return Task.CompletedTask; });
        await using Relay1Connection host = new(
            dialerPublic, hostKeys, isDialer: false, (_, _) => Task.CompletedTask);

        await using (Stream stream = await dialer.OpenStreamAsync(ct))
        {
            await stream.WriteAsync("one "u8.ToArray(), ct);
            await stream.WriteAsync("two"u8.ToArray(), ct);
        }

        Assert.Equal(Relay1RecordOutcome.Taken, host.HandleRecord(records[0]));
        Assert.Equal(Relay1RecordOutcome.SessionBroken, host.HandleRecord(records[2]));
    }

    /// <summary>
    /// Ignored records are reported as a count, not one by one: a relay cut
    /// can bring back thousands of the session before's, and a report for each
    /// flooded the log. The first is reported at once, the rest once an
    /// interval has passed.
    /// </summary>
    [Fact]
    public void IgnoredRecordsAreReportedAsACountAtMostOncePerInterval()
    {
        FakeTimeProvider time = new(DateTimeOffset.UnixEpoch);
        Relay1IgnoredRecords ignored = new(time);

        Assert.True(ignored.Count(out long first));
        Assert.Equal(1, first);
        for (int i = 0; i < 1000; i++)
        {
            Assert.False(ignored.Count(out _));
        }

        time.Advance(Relay1IgnoredRecords.ReportInterval);
        Assert.True(ignored.Count(out long later));
        Assert.Equal(1001, later);
    }

    private static (Relay1Keys Dialer, Relay1Keys Host, NodePublic DialerPublic, NodePublic HostPublic) SessionKeys()
    {
        NodePrivate dialerKey = NodePrivate.NewKey();
        NodePrivate hostKey = NodePrivate.NewKey();
        Relay1Ephemeral dialerHalf = new();
        Relay1Ephemeral hostHalf = new();
        return (
            dialerHalf.Derive(hostHalf.PublicKey, 9, dialerKey.Public(), hostKey.Public()),
            hostHalf.Derive(dialerHalf.PublicKey, 9, dialerKey.Public(), hostKey.Public()),
            dialerKey.Public(),
            hostKey.Public());
    }

    /// <summary>
    /// A hello repeated because its answer was slow is answered with the same
    /// key as the first. The repeat used to be answered with no key at all, and
    /// a dialler that heard that answer first gave up with "the peer agreed to
    /// relay1 without sending an ephemeral key" — over a slow relay, most
    /// handshakes.
    /// </summary>
    [Fact]
    public async Task ARepeatedHelloIsAnsweredWithTheKeyOfTheSessionItBuilt()
    {
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromMinutes(1));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        await using TailcatNode listener = await NodeAsync(relay, ct);
        await relay.WaitForClientAsync(listener.PublicKey, ct);

        NodePrivate dialer = NodePrivate.NewKey();
        await using DerpClient client = await DerpClient.ConnectOverStreamAsync(
            await relay.DialAsync(ct), dialer, relay.PublicKey, ct);

        byte[] hello = SealedRelay1Hello(sessionId: 7, new Relay1Ephemeral(), dialer, listener.PublicKey);

        await client.SendAsync(listener.PublicKey, hello, ct);
        PeerHello first = await NextHelloAckAsync(client, dialer, listener.PublicKey, ct);

        await client.SendAsync(listener.PublicKey, hello, ct);
        PeerHello second = await NextHelloAckAsync(client, dialer, listener.PublicKey, ct);

        Assert.NotNull(first.Ephemeral);
        Assert.NotNull(second.Ephemeral);
        Assert.Equal(first.Ephemeral, second.Ephemeral);
        Assert.Equal(1, listener.SessionCount);
    }

    /// <summary>
    /// Two copies of one hello arriving together build one session, and the
    /// key in either answer is the key that session speaks.
    /// </summary>
    /// <remarks>
    /// Each hello is answered on a task of its own, so both copies used to find
    /// no session yet and build one each; the second replaced the first under a
    /// different key. The dialler kept the first answer, and nothing it sent
    /// ever opened at the other end — a session that came up and then heard
    /// nothing, which is exactly what a peer that is switched off looks like.
    /// Several rounds, because the window is a race.
    /// </remarks>
    [Fact]
    public async Task TwoCopiesOfAHelloArrivingTogetherBuildOneSessionBothAnswersAgreeOn()
    {
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromMinutes(2));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        await using TailcatNode listener = await NodeAsync(relay, ct);
        await relay.WaitForClientAsync(listener.PublicKey, ct);

        for (ulong sessionId = 1; sessionId <= 5; sessionId++)
        {
            NodePrivate dialer = NodePrivate.NewKey();
            await using DerpClient client = await DerpClient.ConnectOverStreamAsync(
                await relay.DialAsync(ct), dialer, relay.PublicKey, ct);

            Relay1Ephemeral mine = new();
            byte[] hello = SealedRelay1Hello(sessionId, mine, dialer, listener.PublicKey);
            await Task.WhenAll(
                client.SendAsync(listener.PublicKey, hello, ct),
                client.SendAsync(listener.PublicKey, hello, ct));

            PeerHello first = await NextHelloAckAsync(client, dialer, listener.PublicKey, ct);
            PeerHello second = await NextHelloAckAsync(client, dialer, listener.PublicKey, ct);
            Assert.NotNull(first.Ephemeral);
            Assert.Equal(first.Ephemeral, second.Ephemeral);

            // The proof that matters: a stream sealed with keys derived from the
            // answer is one the listener can read.
            Relay1Keys keys = mine.Derive(first.Ephemeral, sessionId, dialer.Public(), listener.PublicKey);
            await using Relay1Connection session = new(
                listener.PublicKey,
                keys,
                isDialer: true,
                (record, token) => client.SendAsync(listener.PublicKey, record, token));

            Task<ITailcatConnection> accepted = listener.AcceptConnectionAsync(ct);
            await using (Stream stream = await session.OpenStreamAsync(ct))
            {
                await stream.WriteAsync("opens"u8.ToArray(), ct);
            }

            await using ITailcatConnection server = await accepted;
            await using Stream incoming = await server.AcceptStreamAsync(ct);
            byte[] buffer = new byte[5];
            await incoming.ReadExactlyAsync(buffer, ct);
            Assert.Equal("opens", Encoding.ASCII.GetString(buffer));
        }
    }

    private static byte[] SealedRelay1Hello(ulong sessionId, Relay1Ephemeral ephemeral, NodePrivate dialer, NodePublic listener)
    {
        PeerHello hello = new(
            sessionId,
            new byte[PeerHello.FingerprintLen],
            [],
            HomeRegionId: RegionId,
            Transports: [PeerTransport.Relay1],
            Ephemeral: ephemeral.PublicKey);
        return PeerMessage.Seal(PeerMessageType.Hello, hello.Encode(), dialer, listener);
    }

    private static async Task<PeerHello> NextHelloAckAsync(
        DerpClient client, NodePrivate self, NodePublic from, CancellationToken ct)
    {
        while (true)
        {
            DerpReceivedPacket packet = await client.ReceiveAsync(ct);
            if (PeerMessage.TryOpen(packet.Payload.Span, self, from, out PeerMessageType type, out byte[]? payload) &&
                type == PeerMessageType.HelloAck &&
                PeerHello.TryDecode(payload, out PeerHello? ack))
            {
                return ack;
            }
        }
    }

    /// <summary>
    /// A session the node takes away says what took it, rather than leaving
    /// whoever held it to read "Cannot access a disposed object".
    /// </summary>
    /// <remarks>
    /// This is the field case: one machine re-dials, the node replaces the
    /// session it had for that key, and the layer above finds its connection
    /// gone. Reconnecting is the whole of the repair, so the only thing that
    /// end needs is to be told which ordinary thing happened — and the node
    /// is the only thing that knows. A report of a link flapping every few
    /// seconds carried nothing but the disposed-object line, from two loops
    /// at once, and it named neither the peer nor the cause.
    /// </remarks>
    [Fact]
    public async Task AMachineThatDialsAgainSaysSoOnTheSessionItReplaced()
    {
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromMinutes(1));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        await using TailcatNode listener = await NodeAsync(relay, ct);

        // The same identity both times, which is what a machine that lost its
        // network and rebuilt its node looks like from here.
        NodePrivate key = NodePrivate.NewKey();

        Task<ITailcatConnection> firstArrival = listener.AcceptConnectionAsync(ct);
        ITailcatConnection served;
        await using (TailcatNode first = await TailcatNode.CreateAsync(OptionsFor(relay, key), ct))
        {
            await using ITailcatConnection dialled = await first.ConnectAsync(listener.Address, ct);
            served = await firstArrival;
        }

        await using TailcatNode again = await TailcatNode.CreateAsync(OptionsFor(relay, key), ct);
        Task<ITailcatConnection> secondArrival = listener.AcceptConnectionAsync(ct);
        await using ITailcatConnection redialled = await again.ConnectAsync(listener.Address, ct);
        await using ITailcatConnection replacement = await secondArrival;

        await using (served)
        {
            TailcatException ended = await Assert.ThrowsAsync<TailcatException>(
                async () => await served.OpenStreamAsync(ct));
            Assert.Contains("opened another session", ended.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The reason reaches a loop already parked in
    /// <see cref="ITailcatConnection.AcceptStreamAsync"/>, which is how the
    /// layer above actually hears that a session has ended.
    /// </summary>
    /// <remarks>
    /// Almost nothing learns of an end from a fresh call: a serve loop sits in
    /// accept and a transfer sits in a read, and both were being told only
    /// that a channel had been closed. A reason that reaches the calls nobody
    /// is making is a reason nobody reads.
    /// </remarks>
    [Fact]
    public async Task AParkedAcceptHearsWhyTheSessionEnded()
    {
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromMinutes(1));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        await using TailcatNode listener = await NodeAsync(relay, ct);

        NodePrivate key = NodePrivate.NewKey();

        Task<ITailcatConnection> firstArrival = listener.AcceptConnectionAsync(ct);
        ITailcatConnection served;
        await using (TailcatNode first = await TailcatNode.CreateAsync(OptionsFor(relay, key), ct))
        {
            await using ITailcatConnection dialled = await first.ConnectAsync(listener.Address, ct);
            served = await firstArrival;
        }

        // Parked before anything replaces the session, the way a serve loop is.
        Task<Stream> waiting = served.AcceptStreamAsync(ct);

        await using TailcatNode again = await TailcatNode.CreateAsync(OptionsFor(relay, key), ct);
        Task<ITailcatConnection> secondArrival = listener.AcceptConnectionAsync(ct);
        await using ITailcatConnection redialled = await again.ConnectAsync(listener.Address, ct);
        await using ITailcatConnection replacement = await secondArrival;

        await using (served)
        {
            TailcatException ended = await Assert.ThrowsAsync<TailcatException>(async () => await waiting);
            Assert.Contains("opened another session", ended.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A stream of a session this node took away says what this node did, and
    /// does not put the node's words in the peer's mouth.
    /// </summary>
    /// <remarks>
    /// A RESET frame and a session the node ended are two different events
    /// that leave the same stream unusable, and only the first is the peer's
    /// doing. Reporting the second as "the peer reset the stream: the node was
    /// shut down" sends whoever reads the log to the wrong machine, and the
    /// link above copies the wording into its own exception unchanged.
    /// </remarks>
    [Fact]
    public async Task AStreamThisNodeEndedDoesNotBlameThePeer()
    {
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromMinutes(1));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        await using TailcatNode listener = await NodeAsync(relay, ct);

        // The same identity both times: a machine that lost its network and
        // rebuilt its node, which is what replaces the session here.
        NodePrivate key = NodePrivate.NewKey();

        Task<ITailcatConnection> firstArrival = listener.AcceptConnectionAsync(ct);
        ITailcatConnection served;
        await using (TailcatNode first = await TailcatNode.CreateAsync(OptionsFor(relay, key), ct))
        {
            await using ITailcatConnection dialled = await first.ConnectAsync(listener.Address, ct);
            served = await firstArrival;
        }

        // Handed out while the session is alive, the way a transfer's stream
        // is, and still held when the session is replaced.
        Stream stream = await served.OpenStreamAsync(ct);

        await using TailcatNode again = await TailcatNode.CreateAsync(OptionsFor(relay, key), ct);
        Task<ITailcatConnection> secondArrival = listener.AcceptConnectionAsync(ct);
        await using ITailcatConnection redialled = await again.ConnectAsync(listener.Address, ct);
        await using ITailcatConnection replacement = await secondArrival;

        await using (served)
        {
            byte[] buffer = new byte[16];
            IOException reading = await Assert.ThrowsAsync<IOException>(async () =>
            {
                int read = await stream.ReadAsync(buffer, ct);
                Assert.Fail($"the read returned {read} instead of saying why the session ended");
            });
            Assert.Contains("opened another session", reading.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("the peer reset", reading.Message, StringComparison.Ordinal);

            IOException writing = await Assert.ThrowsAsync<IOException>(
                async () => await stream.WriteAsync(buffer, ct));
            Assert.Contains("opened another session", writing.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("the peer reset", writing.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A relay1 stream already being read says who took the session, instead
    /// of ending as if the peer had simply stopped sending.
    /// </summary>
    /// <remarks>
    /// The reason is set while the read is parked, so a read that only looks
    /// at it before it waits never sees it: it is woken by the closed queue
    /// and reports an orderly end of stream, which the layer above reads as a
    /// peer that stopped mid-frame. That is the shape the flapping was
    /// reported in — a request waiting for its answer at the moment the node
    /// replaced the session — and this is the transport it was reported on.
    /// </remarks>
    [Fact]
    public async Task AStreamAlreadyBeingReadSaysWhoTookTheSession()
    {
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromMinutes(1));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        await using TailcatNode listener = await NodeAsync(relay, ct);

        NodePrivate key = NodePrivate.NewKey();

        Task<ITailcatConnection> firstArrival = listener.AcceptConnectionAsync(ct);
        ITailcatConnection served;
        await using (TailcatNode first = await TailcatNode.CreateAsync(OptionsFor(relay, key), ct))
        {
            await using ITailcatConnection dialled = await first.ConnectAsync(listener.Address, ct);
            served = await firstArrival;
        }

        Stream stream = await served.OpenStreamAsync(ct);

        // Parked the way a request waiting for its answer is, before anything
        // has replaced the session.
        ValueTask<int> reading = stream.ReadAsync(new byte[16], ct);

        await using TailcatNode again = await TailcatNode.CreateAsync(OptionsFor(relay, key), ct);
        Task<ITailcatConnection> secondArrival = listener.AcceptConnectionAsync(ct);
        await using ITailcatConnection redialled = await again.ConnectAsync(listener.Address, ct);
        await using ITailcatConnection replacement = await secondArrival;

        await using (served)
        {
            IOException ended = await Assert.ThrowsAsync<IOException>(async () =>
            {
                int read = await reading;
                Assert.Fail($"the read returned {read} instead of saying why the session ended");
            });
            Assert.Contains("opened another session", ended.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("the peer reset", ended.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Content the peer finished sending still ends as content, even when the
    /// node takes the session away a moment later.
    /// </summary>
    /// <remarks>
    /// The FIN and the closing race: the FIN wakes a parked read, and the node
    /// can replace the session before that read runs again. Reading the reason
    /// then would turn a transfer that arrived whole into a broken one, and
    /// the layer above would resume it for nothing.
    /// </remarks>
    [Fact]
    public async Task ContentTheFinFinishedIsNotTurnedIntoAFailureByTheSessionEnding()
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
            Relay1Stream stream = (Relay1Stream)await client.OpenStreamAsync(ct);

            // Parked with nothing left to read, the way an end of content is
            // waited for.
            ValueTask<int> reading = stream.ReadAsync(new byte[16], ct);

            // The order the field report had: the FIN arrives, and the node
            // takes the session away before the woken read has run.
            stream.OnFin();
            stream.OnSessionClosed("the peer opened another session");

            Assert.Equal(0, await reading);
            Assert.Equal(0, await stream.ReadAsync(new byte[16], ct));
        }
    }

    /// <summary>
    /// A connection its own owner disposed is still a misused object, because
    /// there it is the caller's mistake and nothing happened to the session.
    /// </summary>
    [Fact]
    public async Task AConnectionItsOwnerDisposedIsStillADisposedObject()
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
            await client.DisposeAsync();
            await Assert.ThrowsAsync<ObjectDisposedException>(
                async () => await client.OpenStreamAsync(ct));
        }
    }
}

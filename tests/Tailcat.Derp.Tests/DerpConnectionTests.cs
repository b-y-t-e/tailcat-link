// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using Tailcat.Keys;

namespace Tailcat.Derp.Tests;

/// <summary>
/// Covers the relay connection that re-establishes itself. A relay is how
/// peers find a node at all, so a dropped connection that stayed dropped
/// would silently make the node unreachable.
/// </summary>
public class DerpConnectionTests
{
    private static async Task<DerpConnection> ConnectAsync(FakeDerpRelay relay, NodePrivate key, CancellationToken ct) =>
        await DerpConnection.ConnectAsync(
            async token => await DerpClient.ConnectOverStreamAsync(await relay.DialAsync(token), key, relay.PublicKey, token),
            cancellationToken: ct);

    [Fact]
    public async Task PacketsArriveThroughTheChannel()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using FakeDerpRelay relay = new();

        await using DerpConnection a = await ConnectAsync(relay, NodePrivate.NewKey(), ct);
        await using DerpConnection b = await ConnectAsync(relay, NodePrivate.NewKey(), ct);
        await relay.WaitForClientAsync(b.PublicKey, ct);

        await a.SendAsync(b.PublicKey, "through the channel"u8.ToArray(), ct);

        DerpReceivedPacket got = await b.Packets.ReadAsync(ct);
        Assert.Equal(a.PublicKey, got.Source);
        Assert.Equal("through the channel", Encoding.UTF8.GetString(got.Payload.Span));
        Assert.Equal(0, b.ReconnectCount);
    }

    /// <summary>
    /// When the relay drops the connection, the node reconnects on its own
    /// and keeps the same key, so peers can still reach it afterwards.
    /// </summary>
    [Fact]
    public async Task ConnectionIsReestablishedAfterADrop()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using FakeDerpRelay relay = new();

        NodePrivate key = NodePrivate.NewKey();
        await using DerpConnection a = await ConnectAsync(relay, key, ct);
        await using DerpConnection b = await ConnectAsync(relay, NodePrivate.NewKey(), ct);
        await relay.WaitForClientAsync(a.PublicKey, ct);
        await relay.WaitForClientAsync(b.PublicKey, ct);

        TaskCompletionSource reconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        a.Reconnected += () => reconnected.TrySetResult();

        // The relay hangs up on A, as a restarting relay would.
        relay.DisconnectClient(a.PublicKey);

        await reconnected.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        Assert.True(a.ReconnectCount >= 1);
        Assert.Equal(key.Public(), a.PublicKey);

        // And the node is reachable again on the same key.
        await relay.WaitForClientAsync(a.PublicKey, ct);
        await b.SendAsync(a.PublicKey, "still here"u8.ToArray(), ct);

        DerpReceivedPacket got = await a.Packets.ReadAsync(ct).AsTask().WaitAsync(TimeSpan.FromSeconds(10), ct);
        Assert.Equal("still here", Encoding.UTF8.GetString(got.Payload.Span));
    }

    /// <summary>
    /// Notices keep arriving after the connection is re-established. The
    /// client underneath is a new one, and a subscription left on the old one
    /// would go quiet exactly when the relay has the most to say.
    /// </summary>
    [Fact]
    public async Task NoticesStillArriveAfterAReconnect()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using FakeDerpRelay relay = new();
        await using DerpConnection a = await ConnectAsync(relay, NodePrivate.NewKey(), ct);
        await relay.WaitForClientAsync(a.PublicKey, ct);

        TaskCompletionSource reconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        a.Reconnected += () => reconnected.TrySetResult();
        TaskCompletionSource<DerpNotice> heard = new(TaskCreationOptions.RunContinuationsAsynchronously);
        a.Notice += notice => heard.TrySetResult(notice);

        relay.DisconnectClient(a.PublicKey);
        await reconnected.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        await relay.WaitForClientAsync(a.PublicKey, ct);

        await relay.SendHealthAsync(a.PublicKey, "Something else is connected with this key", ct);

        Assert.Equal(
            new DerpHealth("Something else is connected with this key"),
            await heard.Task.WaitAsync(TimeSpan.FromSeconds(10), ct));
    }

    // Short enough that a test waits seconds, not the defaults' tens of them.
    private static readonly DerpLiveness QuickLiveness = new()
    {
        ProbeAfterSend = TimeSpan.FromMilliseconds(300),
        ProbeWhenIdle = TimeSpan.FromMilliseconds(600),
        Timeout = TimeSpan.FromMilliseconds(500),
        CheckInterval = TimeSpan.FromMilliseconds(50),
    };

    private static async Task<DerpConnection> ConnectAsync(
        FakeDerpRelay relay, NodePrivate key, DerpLiveness liveness, CancellationToken ct, TimeProvider? time = null) =>
        await DerpConnection.ConnectAsync(
            async token => await DerpClient.ConnectOverStreamAsync(await relay.DialAsync(token), key, relay.PublicKey, token),
            liveness,
            time,
            ct);

    // The default timings, checked only when a test says so, on a clock it
    // advances: a verdict is then a matter of how far the clock moved, not of
    // how busy the machine running the test is.
    private static readonly DerpLiveness CheckedByTest = new() { CheckInterval = Timeout.InfiniteTimeSpan };

    /// <summary>
    /// A connection that stops carrying bytes without ending is replaced once
    /// a ping has gone unanswered — a second after a send and two more, with
    /// the defaults — instead of waiting out the operating system's
    /// retransmissions, and what is sent afterwards arrives.
    /// </summary>
    /// <remarks>
    /// The field case: a stateful firewall that lost track of the relay's
    /// TCP flow dropped everything on it in both directions while both ends
    /// still held it open. Every session on the relay was silent for the
    /// twenty-odd seconds Windows took to give up, and the link above
    /// declared the peer gone every time.
    /// </remarks>
    [Fact]
    public async Task AConnectionThatStopsCarryingBytesIsReplaced()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        FakeTimeProvider time = new(DateTimeOffset.UnixEpoch);
        await using FakeDerpRelay relay = new();
        await using DerpConnection a = await ConnectAsync(relay, NodePrivate.NewKey(), CheckedByTest, ct, time);
        await using DerpConnection b = await ConnectAsync(relay, NodePrivate.NewKey(), DerpLiveness.Off, ct);
        await relay.WaitForClientAsync(a.PublicKey, ct);
        await relay.WaitForClientAsync(b.PublicKey, ct);

        TaskCompletionSource reconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        a.Reconnected += () => reconnected.TrySetResult();

        relay.Freeze(a.PublicKey);
        await a.SendAsync(b.PublicKey, "into the black hole"u8.ToArray(), ct);
        await PingAndLetItGoUnansweredAsync(a, time, CheckedByTest.ProbeAfterSend, ct);

        await reconnected.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        Assert.Equal(1, a.StalledCount);

        await relay.WaitForClientAsync(a.PublicKey, ct);
        await b.SendAsync(a.PublicKey, "through the new connection"u8.ToArray(), ct);
        DerpReceivedPacket got = await a.Packets.ReadAsync(ct).AsTask().WaitAsync(TimeSpan.FromSeconds(10), ct);
        Assert.Equal("through the new connection", Encoding.UTF8.GetString(got.Payload.Span));
    }

    // Lets silence reach the point where a check pings, then lets the ping's
    // timeout pass without an answer and checks again.
    private static async Task PingAndLetItGoUnansweredAsync(
        DerpConnection connection, FakeTimeProvider time, TimeSpan silenceBeforePing, CancellationToken ct)
    {
        time.Advance(silenceBeforePing);
        await connection.CheckLivenessAsync(ct);
        time.Advance(CheckedByTest.Timeout);
        await connection.CheckLivenessAsync(ct);
    }

    /// <summary>
    /// What was sent into a connection that had silently died arrives anyway,
    /// in order and ahead of what is sent after: it goes out again on the
    /// connection that replaces it.
    /// </summary>
    /// <remarks>
    /// Without this QUIC over the relay waited out retransmission timers that
    /// had doubled with every loss, and a cut every few seconds outran them
    /// until QUIC gave up on the peer; relay1 had no retransmission at all.
    /// </remarks>
    [Fact]
    public async Task PacketsSentIntoADeadConnectionArriveOnItsReplacement()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using FakeDerpRelay relay = new();
        await using DerpConnection a = await ConnectAsync(relay, NodePrivate.NewKey(), QuickLiveness, ct);
        await using DerpConnection b = await ConnectAsync(relay, NodePrivate.NewKey(), DerpLiveness.Off, ct);
        await relay.WaitForClientAsync(a.PublicKey, ct);
        await relay.WaitForClientAsync(b.PublicKey, ct);

        TaskCompletionSource reconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        a.Reconnected += () => reconnected.TrySetResult();

        relay.Freeze(a.PublicKey);
        for (int i = 1; i <= 5; i++)
        {
            await a.SendAsync(b.PublicKey, Encoding.UTF8.GetBytes($"packet {i}"), ct);
        }

        await reconnected.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        await a.SendAsync(b.PublicKey, "packet 6"u8.ToArray(), ct);

        List<string> received = [];
        while (received.Count < 6)
        {
            DerpReceivedPacket got = await b.Packets.ReadAsync(ct).AsTask().WaitAsync(TimeSpan.FromSeconds(10), ct);
            received.Add(Encoding.UTF8.GetString(got.Payload.Span));
        }

        Assert.Equal(Enumerable.Range(1, 6).Select(i => $"packet {i}"), received);
    }

    /// <summary>
    /// A relay1 transfer that fills the dead connection with full-sized records
    /// gets every one of them back, not just the newest: a single record
    /// missing is a gap, and a gap ends the session.
    /// </summary>
    /// <remarks>
    /// What was kept for resending used to be capped at 64 KiB — two records.
    /// </remarks>
    [Fact]
    public async Task EveryFullSizedRecordSentIntoADeadConnectionArrivesOnItsReplacement()
    {
        const int Relay1RecordBytes = 32256;
        const int Records = 8;
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using FakeDerpRelay relay = new();
        await using DerpConnection a = await ConnectAsync(relay, NodePrivate.NewKey(), QuickLiveness, ct);
        await using DerpConnection b = await ConnectAsync(relay, NodePrivate.NewKey(), DerpLiveness.Off, ct);
        await relay.WaitForClientAsync(a.PublicKey, ct);
        await relay.WaitForClientAsync(b.PublicKey, ct);

        relay.Freeze(a.PublicKey);
        for (int number = 1; number <= Records; number++)
        {
            await a.SendAsync(b.PublicKey, Numbered(number, Relay1RecordBytes), ct);
        }

        await AssertArriveAsRelay1CountsThemAsync(b, Records, ct);
    }

    /// <summary>
    /// What is sent while a slow replacement is still taking the backlog goes
    /// out after it, on the replacement, rather than into the dead connection.
    /// </summary>
    /// <remarks>
    /// Sends used to wait two seconds at most and then write into the dead
    /// connection, after the resend had already taken its list: those packets
    /// were lost for good, which relay1 reads as a gap.
    /// </remarks>
    [Fact]
    public async Task PacketsSentDuringASlowResendFollowItOnTheReplacement()
    {
        const int SentIntoTheDeadConnection = 20;
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using FakeDerpRelay relay = new();
        NodePrivate key = NodePrivate.NewKey();
        int dials = 0;
        await using DerpConnection a = await DerpConnection.ConnectAsync(
            async token =>
            {
                Stream wire = await relay.DialAsync(token);
                if (Interlocked.Increment(ref dials) > 1)
                {
                    wire = new SlowWritingStream(wire, TimeSpan.FromMilliseconds(150));
                }
                return await DerpClient.ConnectOverStreamAsync(wire, key, relay.PublicKey, token);
            },
            QuickLiveness,
            cancellationToken: ct);
        await using DerpConnection b = await ConnectAsync(relay, NodePrivate.NewKey(), DerpLiveness.Off, ct);
        await relay.WaitForClientAsync(a.PublicKey, ct);
        await relay.WaitForClientAsync(b.PublicKey, ct);

        TaskCompletionSource replacing = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource reconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        a.ReconnectScheduled += _ => replacing.TrySetResult();
        a.Reconnected += () => reconnected.TrySetResult();

        relay.Freeze(a.PublicKey);
        int number = 0;
        while (number < SentIntoTheDeadConnection)
        {
            await a.SendAsync(b.PublicKey, Numbered(++number, 64), ct);
        }

        await replacing.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        while (!reconnected.Task.IsCompleted)
        {
            await a.SendAsync(b.PublicKey, Numbered(++number, 64), ct);
            await Task.Delay(100, ct);
        }
        await a.SendAsync(b.PublicKey, Numbered(++number, 64), ct);

        await AssertArriveAsRelay1CountsThemAsync(b, number, ct);
    }

    /// <summary>
    /// A send made while a replacement is working through a backlog returns
    /// promptly, and its packet still follows the backlog, in order.
    /// </summary>
    /// <remarks>
    /// Sends used to wait for the whole backlog, however long it took. PeerLink
    /// awaits its relay probe before its direct-path keepalives, so a long
    /// resend held back the path that was working and let it time out.
    /// </remarks>
    [Fact]
    public async Task ASendDuringASlowResendReturnsAndStillFollowsTheBacklog()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using FakeDerpRelay relay = new();
        NodePrivate key = NodePrivate.NewKey();
        PacketHoldingStream? replacement = null;

        // No liveness loop, and a per-packet resend deadline far beyond the
        // test, so the held resend is slow rather than failed.
        DerpLiveness patientResend = new() { Enabled = false, Timeout = TimeSpan.FromMinutes(5) };
        int dials = 0;
        await using DerpConnection a = await DerpConnection.ConnectAsync(
            async token =>
            {
                Stream wire = await relay.DialAsync(token);
                if (Interlocked.Increment(ref dials) == 2)
                {
                    wire = replacement = new PacketHoldingStream(wire);
                }
                return await DerpClient.ConnectOverStreamAsync(wire, key, relay.PublicKey, token);
            },
            patientResend,
            cancellationToken: ct);
        await using DerpConnection b = await ConnectAsync(relay, NodePrivate.NewKey(), DerpLiveness.Off, ct);
        await relay.WaitForClientAsync(a.PublicKey, ct);
        await relay.WaitForClientAsync(b.PublicKey, ct);

        TaskCompletionSource reconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        a.Reconnected += () => reconnected.TrySetResult();

        const int Backlog = 3;
        for (int number = 1; number <= Backlog; number++)
        {
            await a.SendAsync(b.PublicKey, Numbered(number, 64), ct);
        }

        relay.DisconnectClient(a.PublicKey);
        await WaitUntilAsync(() => replacement is not null, ct);
        await replacement!.FirstPacketHeld.WaitAsync(TimeSpan.FromSeconds(10), ct);

        // The timeout only stops a regression from hanging the run.
        await a.SendAsync(b.PublicKey, Numbered(Backlog + 1, 64), ct).WaitAsync(TimeSpan.FromSeconds(5), ct);
        Assert.False(reconnected.Task.IsCompleted, "the resend finished before the send returned, so it proves nothing");

        replacement.Release();
        await reconnected.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        await a.SendAsync(b.PublicKey, Numbered(Backlog + 2, 64), ct);

        await AssertArriveAsRelay1CountsThemAsync(b, Backlog + 2, ct);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct)
    {
        for (int i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(20, ct);
        }
        Assert.True(condition(), "the condition never held");
    }

    private static byte[] Numbered(int number, int size)
    {
        byte[] packet = new byte[size];
        BinaryPrimitives.WriteInt32BigEndian(packet, number);
        return packet;
    }

    // relay1's rule: a number already seen is a harmless copy, one ahead of
    // the next expected is a gap.
    private static async Task AssertArriveAsRelay1CountsThemAsync(DerpConnection receiver, int count, CancellationToken ct)
    {
        int expected = 1;
        while (expected <= count)
        {
            DerpReceivedPacket got = await receiver.Packets.ReadAsync(ct).AsTask().WaitAsync(TimeSpan.FromSeconds(15), ct);
            int number = BinaryPrimitives.ReadInt32BigEndian(got.Payload.Span);
            Assert.True(number <= expected, $"packet {number} arrived where {expected} was expected");
            if (number == expected)
            {
                expected++;
            }
        }
    }

    /// <summary>
    /// A connection that is quiet but answering is left alone: asking is
    /// cheap, replacing is not, and the relay's pong is the answer.
    /// </summary>
    [Fact]
    public async Task AQuietConnectionThatAnswersIsKept()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        FakeTimeProvider time = new(DateTimeOffset.UnixEpoch);
        await using FakeDerpRelay relay = new();
        await using DerpConnection a = await ConnectAsync(relay, NodePrivate.NewKey(), CheckedByTest, ct, time);
        await relay.WaitForClientAsync(a.PublicKey, ct);

        // Silent long enough to ask; nothing else is on the wire, so the next
        // frame is the relay's pong.
        TaskCompletionSource answered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        a.FrameReceived += () => answered.TrySetResult();
        time.Advance(CheckedByTest.ProbeWhenIdle);
        await a.CheckLivenessAsync(ct);
        await answered.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);

        time.Advance(CheckedByTest.Timeout);
        await a.CheckLivenessAsync(ct);

        Assert.Equal(0, a.ReconnectCount);
        Assert.Equal(0, a.StalledCount);
    }

    /// <summary>
    /// A connection whose writes stopped going out is abandoned even though the
    /// ping has to queue behind a write that will never finish. The ping's
    /// write used to be awaited without a deadline, so the check stopped right
    /// there, on exactly the connection it exists to catch.
    /// </summary>
    /// <remarks>
    /// <see cref="FakeDerpRelay.Freeze"/> cannot show this: it keeps reading, so
    /// the client's send buffer never fills and no write ever blocks.
    /// </remarks>
    [Fact]
    public async Task AConnectionWithAWriteStuckForGoodIsStillReplaced()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using FakeDerpRelay relay = new();
        NodePrivate key = NodePrivate.NewKey();
        StallingStream? first = null;
        await using DerpConnection a = await DerpConnection.ConnectAsync(
            async token =>
            {
                Stream wire = await relay.DialAsync(token);
                if (first is null)
                {
                    wire = first = new StallingStream(wire);
                }
                return await DerpClient.ConnectOverStreamAsync(wire, key, relay.PublicKey, token);
            },
            QuickLiveness,
            cancellationToken: ct);
        await using DerpConnection b = await ConnectAsync(relay, NodePrivate.NewKey(), DerpLiveness.Off, ct);
        await relay.WaitForClientAsync(a.PublicKey, ct);

        TaskCompletionSource reconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        a.Reconnected += () => reconnected.TrySetResult();

        first!.Stall();
        Task stuckSend = a.SendAsync(b.PublicKey, "never leaves"u8.ToArray(), ct);

        await reconnected.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        Assert.Equal(1, a.StalledCount);

        // Closing the abandoned connection releases the send stuck in it; a
        // relay is best effort, so it ends quietly rather than throwing.
        await stuckSend.WaitAsync(TimeSpan.FromSeconds(10), ct);
    }

    /// <summary>
    /// A replacement that is cut straight after its handshake does not hold the
    /// reconnection up: resending into it has a deadline, the attempt fails,
    /// and the next connection carries what the first one swallowed.
    /// </summary>
    /// <remarks>
    /// The resend used to write without one, under the lock the liveness check
    /// needs, with every sender waiting for it to finish: a node on a network
    /// that cuts flows every few seconds stalled there until it was disposed.
    /// </remarks>
    [Fact]
    public async Task AReplacementCutDuringTheResendIsReplacedInTurn()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using FakeDerpRelay relay = new();
        NodePrivate key = NodePrivate.NewKey();
        int dials = 0;
        StallingStream? first = null;
        await using DerpConnection a = await DerpConnection.ConnectAsync(
            async token =>
            {
                int dial = Interlocked.Increment(ref dials);
                StallingStream wire = new(await relay.DialAsync(token));
                first ??= wire;
                DerpClient client = await DerpClient.ConnectOverStreamAsync(wire, key, relay.PublicKey, token);
                if (dial == 2)
                {
                    wire.Stall();
                }
                return client;
            },
            QuickLiveness,
            cancellationToken: ct);
        await using DerpConnection b = await ConnectAsync(relay, NodePrivate.NewKey(), DerpLiveness.Off, ct);
        await relay.WaitForClientAsync(a.PublicKey, ct);
        await relay.WaitForClientAsync(b.PublicKey, ct);

        first!.Stall();
        _ = a.SendAsync(b.PublicKey, "swallowed twice"u8.ToArray(), ct);

        DerpReceivedPacket got = await b.Packets.ReadAsync(ct).AsTask().WaitAsync(TimeSpan.FromSeconds(15), ct);
        Assert.Equal("swallowed twice", Encoding.UTF8.GetString(got.Payload.Span));
        Assert.True(dials >= 3, $"only {dials} dials");
    }

    /// <summary>
    /// Connections that keep going dark are each replaced as quickly as the
    /// first. A drop counted as a relay that will not have us would back off
    /// towards thirty seconds, and a flow cut every few seconds would leave
    /// the node unreachable most of the time.
    /// </summary>
    [Fact]
    public async Task RepeatedStallsAreEachReplacedPromptly()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        FakeTimeProvider time = new(DateTimeOffset.UnixEpoch);
        await using FakeDerpRelay relay = new();
        await using DerpConnection a = await ConnectAsync(relay, NodePrivate.NewKey(), CheckedByTest, ct, time);
        await relay.WaitForClientAsync(a.PublicKey, ct);

        List<TimeSpan> pauses = [];
        a.ReconnectScheduled += pause => pauses.Add(pause);

        for (int round = 1; round <= 4; round++)
        {
            TaskCompletionSource reconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnReconnected() => reconnected.TrySetResult();
            a.Reconnected += OnReconnected;

            relay.Freeze(a.PublicKey);
            await PingAndLetItGoUnansweredAsync(a, time, CheckedByTest.ProbeWhenIdle, ct);
            await reconnected.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            a.Reconnected -= OnReconnected;

            await relay.WaitForClientAsync(a.PublicKey, ct);
        }

        Assert.Equal(4, a.StalledCount);
        Assert.Equal(4, pauses.Count);
        Assert.All(pauses, pause => Assert.Equal(pauses[0], pause));
    }

    /// <summary>
    /// Sends wait for the first attempt at a replacement only. Once one has
    /// failed they go straight through, so a relay that stays down does not
    /// hold every send back for as long as it is down.
    /// </summary>
    /// <remarks>
    /// Every send used to wait out the full pause on each of them, and a
    /// caller that awaits its relay send before probing a direct path — which
    /// PeerLink does — let the working direct path time out behind it.
    /// </remarks>
    [Fact]
    public async Task SendsDoNotWaitOnARelayThatStaysUnreachable()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using FakeDerpRelay relay = new();
        NodePrivate key = NodePrivate.NewKey();
        int dials = 0;
        await using DerpConnection a = await DerpConnection.ConnectAsync(
            async token => Interlocked.Increment(ref dials) == 1
                ? await DerpClient.ConnectOverStreamAsync(await relay.DialAsync(token), key, relay.PublicKey, token)
                : throw new IOException("the relay is down"),
            DerpLiveness.Off,
            cancellationToken: ct);
        await relay.WaitForClientAsync(a.PublicKey, ct);

        TaskCompletionSource secondAttempt = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int attempts = 0;
        a.ReconnectScheduled += _ =>
        {
            if (Interlocked.Increment(ref attempts) == 2)
            {
                secondAttempt.TrySetResult();
            }
        };

        relay.DisconnectClient(a.PublicKey);
        await secondAttempt.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);

        // Well inside the pause a send takes while the first attempt runs.
        await a.SendAsync(NodePrivate.NewKey().Public(), "into the outage"u8.ToArray(), ct)
            .WaitAsync(TimeSpan.FromSeconds(1), ct);
    }

    /// <summary>Sending over a connection that is down is dropped, not thrown.</summary>
    /// <remarks>
    /// A relay never promised delivery, and callers already handle loss;
    /// making a transient drop throw would push that handling everywhere.
    /// </remarks>
    [Fact]
    public async Task SendingWhileDisconnectedDoesNotThrow()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using FakeDerpRelay relay = new();
        await using DerpConnection a = await ConnectAsync(relay, NodePrivate.NewKey(), ct);
        await relay.WaitForClientAsync(a.PublicKey, ct);

        relay.DisconnectClient(a.PublicKey);

        // Racing the drop: whichever side wins, this must not throw.
        for (int i = 0; i < 5; i++)
        {
            await a.SendAsync(NodePrivate.NewKey().Public(), "into the void"u8.ToArray(), ct);
        }
    }

    /// <summary>
    /// A relay that drops a connection which had been up for a while is not
    /// treated as unreachable: the next attempt starts from the floor again.
    /// </summary>
    /// <remarks>
    /// The backoff used to double after every drop and reset only when a
    /// packet arrived, so a relay that hung up nightly but came straight back
    /// would climb to the 30-second ceiling and sit there. The stability
    /// window is what keeps that from turning into a hot reconnect loop
    /// against a relay that accepts and drops immediately.
    /// </remarks>
    [Fact]
    public async Task BackoffResetsAfterAConnectionThatHeld()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        FakeTimeProvider time = new(DateTimeOffset.UnixEpoch);
        await using FakeDerpRelay relay = new();

        NodePrivate key = NodePrivate.NewKey();
        await using DerpConnection a = await DerpConnection.ConnectAsync(
            async token => await DerpClient.ConnectOverStreamAsync(
                await relay.DialAsync(token), key, relay.PublicKey, token),
            timeProvider: time,
            cancellationToken: ct);
        await relay.WaitForClientAsync(a.PublicKey, ct);

        // Two drops with nothing in between: the relay looks unhealthy, so the
        // wait before each attempt doubles.
        await DropAndReconnectAsync(relay, a, ct);
        await DropAndReconnectAsync(relay, a, ct);

        // This one held for a minute before dropping, which says nothing about
        // whether the relay can be reached.
        time.Advance(TimeSpan.FromMinutes(1));
        await DropAndReconnectAsync(relay, a, ct);

        long startedAt = Stopwatch.GetTimestamp();
        await DropAndReconnectAsync(relay, a, ct);
        TimeSpan waited = Stopwatch.GetElapsedTime(startedAt);

        Assert.True(
            waited < TimeSpan.FromMilliseconds(700),
            $"reconnected after {waited.TotalMilliseconds:F0} ms; a reset backoff is ~200 ms, a doubled one ~1600 ms");
        Assert.Equal(4, a.ReconnectCount);
    }

    private static async Task DropAndReconnectAsync(FakeDerpRelay relay, DerpConnection connection, CancellationToken ct)
    {
        TaskCompletionSource reconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnReconnected() => reconnected.TrySetResult();
        connection.Reconnected += OnReconnected;
        try
        {
            relay.DisconnectClient(connection.PublicKey);
            await reconnected.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
            await relay.WaitForClientAsync(connection.PublicKey, ct);
        }
        finally
        {
            connection.Reconnected -= OnReconnected;
        }
    }

    [Fact]
    public async Task DisposingTwiceIsHarmless()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using FakeDerpRelay relay = new();
        DerpConnection a = await ConnectAsync(relay, NodePrivate.NewKey(), ct);

        await a.DisposeAsync();
        await a.DisposeAsync();
    }
}

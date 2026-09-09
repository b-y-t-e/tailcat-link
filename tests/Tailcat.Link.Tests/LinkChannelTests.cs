// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Buffers.Binary;
using Tailcat.Link.Storage;

namespace Tailcat.Link.Tests;

using static LinkHarness;

/// <summary>
/// Covers the third shape, between a message and a file: ordered within the
/// channel, and gone with its session.
/// </summary>
/// <remarks>
/// The case it exists for is realtime frames — audio, telemetry, input — where
/// a request is a round trip too many and a transfer promises a durability
/// that would be worse than useless.
/// </remarks>
public class LinkChannelTests
{
    private const int Frames = 200;

    /// <summary>
    /// Every frame arrives, once, in the order it was sent. That ordering is
    /// the whole of what a channel promises over a run of notifications.
    /// </summary>
    [Fact]
    public async Task FramesArriveInTheOrderTheyWereSent()
    {
        using CancellationTokenSource cts = Deadline(TimeSpan.FromMinutes(2));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        FakeRelayGatewayFactory gateways = new(relay);

        await using ILinkHost host = await TailcatLink.HostManyAsync(
            "demo", OptionsFor(gateways, new InMemoryLinkStore()), ct);

        List<int> heard = [];
        TaskCompletionSource<ChannelCloseReason> ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        host.OnChannel("audio", async (_, channel, token) =>
        {
            channel.Closed += (_, e) => ended.TrySetResult(e.Reason);
            await foreach (ReadOnlyMemory<byte> frame in channel.ReadAllAsync(token))
            {
                heard.Add(BinaryPrimitives.ReadInt32BigEndian(frame.Span));
            }
        });

        await using ILink phone = await JoinAsync(gateways, host, ct);
        await host.WaitForPeerAsync(ct);

        ILinkChannelWriter audio = await phone.OpenChannelAsync("audio", ct);
        await using (audio)
        {
            for (int i = 0; i < Frames; i++)
            {
                await audio.SendAsync(Numbered(i), ct);
            }
        }

        Assert.Equal(ChannelCloseReason.PeerClosed, await ended.Task.WaitAsync(ct));
        Assert.Equal(Enumerable.Range(0, Frames), heard);
    }

    /// <summary>
    /// A handler that stops reading after one frame still ends its channel:
    /// the channel ends when the handler returns, and a handler that broke
    /// out of the loop is exactly the one that closed nothing itself.
    /// </summary>
    [Fact]
    public async Task AHandlerThatStopsReadingEarlyEndsTheChannel()
    {
        using CancellationTokenSource cts = Deadline(TimeSpan.FromMinutes(2));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        FakeRelayGatewayFactory gateways = new(relay);

        await using ILinkHost host = await TailcatLink.HostManyAsync(
            "demo", OptionsFor(gateways, new InMemoryLinkStore()), ct);

        TaskCompletionSource<ChannelCloseReason> ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ILinkChannelReader? served = null;
        host.OnChannel("audio", async (_, channel, token) =>
        {
            served = channel;
            channel.Closed += (_, e) => ended.TrySetResult(e.Reason);
            await foreach (ReadOnlyMemory<byte> frame in channel.ReadAllAsync(token))
            {
                Assert.Equal(sizeof(int), frame.Length);
                break;
            }
        });

        await using ILink phone = await JoinAsync(gateways, host, ct);
        await host.WaitForPeerAsync(ct);

        ILinkChannelWriter audio = await phone.OpenChannelAsync("audio", ct);
        await audio.SendAsync(Numbered(0), ct);

        Assert.Equal(ChannelCloseReason.LocalClosed, await ended.Task.WaitAsync(ct));
        Assert.False(served!.IsOpen);
    }

    /// <summary>
    /// A channel nobody is listening for is refused outright, rather than
    /// swallowing frames into a machine that will never read them.
    /// </summary>
    [Fact]
    public async Task AChannelNobodyTakesIsRefused()
    {
        using CancellationTokenSource cts = Deadline(TimeSpan.FromMinutes(2));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        FakeRelayGatewayFactory gateways = new(relay);

        await using ILinkHost host = await TailcatLink.HostManyAsync(
            "demo", OptionsFor(gateways, new InMemoryLinkStore()), ct);

        await using ILink phone = await JoinAsync(gateways, host, ct);
        await host.WaitForPeerAsync(ct);

        RemoteHandlerException refused = await Assert.ThrowsAsync<RemoteHandlerException>(
            async () => await phone.OpenChannelAsync("audio", ct));
        Assert.Contains("\"audio\" channel", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Closing while frames are still going out ends the channel cleanly: the
    /// goodbye marker queues behind them rather than landing in the middle of
    /// one, which the reading end would see as a corrupt length prefix.
    /// </summary>
    [Fact]
    public async Task ClosingWhileFramesAreInFlightDoesNotCutOneInHalf()
    {
        using CancellationTokenSource cts = Deadline(TimeSpan.FromMinutes(2));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        FakeRelayGatewayFactory gateways = new(relay);

        await using ILinkHost host = await TailcatLink.HostManyAsync(
            "demo", OptionsFor(gateways, new InMemoryLinkStore()), ct);

        List<int> heard = [];
        TaskCompletionSource<ChannelCloseReason> ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        host.OnChannel("audio", async (_, channel, token) =>
        {
            channel.Closed += (_, e) => ended.TrySetResult(e.Reason);
            await foreach (ReadOnlyMemory<byte> frame in channel.ReadAllAsync(token))
            {
                heard.Add(BinaryPrimitives.ReadInt32BigEndian(frame.Span));
            }
        });

        await using ILink phone = await JoinAsync(gateways, host, ct);
        await host.WaitForPeerAsync(ct);

        ILinkChannelWriter audio = await phone.OpenChannelAsync("audio", ct);
        List<Task> sending = [.. Enumerable.Range(0, Frames).Select(i => audio.SendAsync(Numbered(i), ct))];

        // The race the goodbye has to lose: closing while those are queued.
        await audio.DisposeAsync();
        foreach (Task sent in sending)
        {
            await WaitForEndingAsync(sent);
        }

        // PeerClosed rather than SessionEnded is the assertion: a goodbye
        // written into the middle of a frame leaves the reader mid-length, so
        // it would end on the broken framing instead of on the goodbye.
        Assert.Equal(ChannelCloseReason.PeerClosed, await ended.Task.WaitAsync(ct));
        Assert.All(heard, value => Assert.InRange(value, 0, Frames - 1));
        Assert.Equal(heard.Distinct().Count(), heard.Count);
    }

    /// <summary>
    /// A send that joins the queue while the channel is closing finishes,
    /// rather than waiting on a turn that will never come.
    /// </summary>
    /// <remarks>
    /// The window is between the closing call giving the turn back and the
    /// channel being done with the queue: a sender that got that far is past
    /// the check that turns latecomers away, and is owed either a frame on
    /// the wire or a <see cref="LinkClosedException"/>. Waiting until the
    /// session dies hours later is neither, and looks to the application like
    /// a peer that stopped answering.
    /// </remarks>
    [Fact]
    public async Task ASendThatJoinsTheQueueWhileClosingDoesNotWaitForever()
    {
        using CancellationTokenSource cts = Deadline(TimeSpan.FromMinutes(2));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        FakeRelayGatewayFactory gateways = new(relay);

        await using ILinkHost host = await TailcatLink.HostManyAsync(
            "demo", OptionsFor(gateways, new InMemoryLinkStore()), ct);
        host.OnChannel("audio", async (_, channel, token) =>
        {
            await foreach (ReadOnlyMemory<byte> unread in channel.ReadAllAsync(token))
            {
            }
        });

        await using ILink phone = await JoinAsync(gateways, host, ct);
        await host.WaitForPeerAsync(ct);

        ILinkChannelWriter audio = await phone.OpenChannelAsync("audio", ct);
        List<Task> sending = [.. Enumerable.Range(0, Frames).Select(i => audio.SendAsync(Numbered(i), ct))];

        // Started before the close has finished, which is the whole point:
        // these are the ones that can find the queue mid-teardown.
        Task closing = audio.DisposeAsync().AsTask();
        sending.AddRange(Enumerable.Range(Frames, Frames).Select(i => audio.SendAsync(Numbered(i), ct)));

        await closing;
        foreach (Task sent in sending)
        {
            // The deadline is the assertion: a send abandoned on the queue
            // never completes at all, and the test fails as the timeout it is.
            await WaitForEndingAsync(sent).WaitAsync(ct);
        }
    }

    /// <summary>
    /// An application whose <see cref="ILinkChannel.Closed"/> handler throws
    /// is told about it in the log and nowhere else. Closing runs on the
    /// failure path of a send and inside <c>DisposeAsync</c>, so a handler
    /// escaping from there would replace the reason the caller is being told
    /// about — or fail a disposal that has nothing left to do.
    /// </summary>
    [Fact]
    public async Task AClosedHandlerThatThrowsDoesNotEscapeTheChannel()
    {
        using CancellationTokenSource cts = Deadline(TimeSpan.FromMinutes(2));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        FakeRelayGatewayFactory gateways = new(relay);

        await using ILinkHost host = await TailcatLink.HostManyAsync(
            "demo", OptionsFor(gateways, new InMemoryLinkStore()), ct);
        host.OnChannel("audio", async (_, channel, token) =>
        {
            await foreach (ReadOnlyMemory<byte> frame in channel.ReadAllAsync(token))
            {
                Assert.False(frame.IsEmpty);
            }
        });

        await using ILink phone = await JoinAsync(gateways, host, ct);
        await host.WaitForPeerAsync(ct);

        ILinkChannelWriter audio = await phone.OpenChannelAsync("audio", ct);
        audio.Closed += (_, _) => throw new InvalidOperationException("the page tore its pipeline down badly");

        await audio.SendAsync(new byte[] { 1, 2, 3 }, ct);
        await audio.DisposeAsync();
        Assert.False(audio.IsOpen);
    }

    /// <summary>
    /// A channel goes with the session that carried it rather than being
    /// resumed on the next one, which is what it promises rather than a gap.
    /// A request is the shape for what must survive a reconnection, and a
    /// transfer the shape for content that meets the same silence and wants a
    /// different answer.
    /// </summary>
    [Fact]
    public async Task AChannelEndsWithItsSession()
    {
        using CancellationTokenSource cts = Deadline(TimeSpan.FromMinutes(2));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        FakeRelayGatewayFactory gateways = new(relay);

        ILinkHost host = await TailcatLink.HostManyAsync(
            "demo", OptionsFor(gateways, new InMemoryLinkStore()), ct);

        TaskCompletionSource<ChannelCloseReason> ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource reading = new(TaskCreationOptions.RunContinuationsAsynchronously);
        host.OnChannel("audio", async (_, channel, token) =>
        {
            channel.Closed += (_, e) => ended.TrySetResult(e.Reason);
            await foreach (ReadOnlyMemory<byte> frame in channel.ReadAllAsync(token))
            {
                reading.TrySetResult();
            }
        });

        await using ILink phone = await JoinAsync(gateways, host, ct);
        await host.WaitForPeerAsync(ct);
        ILinkChannelWriter audio = await phone.OpenChannelAsync("audio", ct);
        TaskCompletionSource<ChannelCloseReason> sendingEnded =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        audio.Closed += (_, e) => sendingEnded.TrySetResult(e.Reason);
        await audio.SendAsync(new byte[] { 1, 2, 3 }, ct);
        await reading.Task.WaitAsync(ct);

        // The host going away is what a session dying looks like from the
        // reading end, and a channel is not resumed onto the next one.
        await host.DisposeAsync();

        Assert.Equal(ChannelCloseReason.SessionEnded, await ended.Task.WaitAsync(ct));
        // And the sending end is over too, said rather than merely true: an
        // application that tears its pipeline down on Closed sends nothing
        // more, so learning of it only from the next send is learning too
        // late. A channel is not resumed, so what it must not do is quietly
        // accept frames nobody will ever read.
        Assert.Equal(ChannelCloseReason.SessionEnded, await sendingEnded.Task.WaitAsync(ct));
        Assert.False(audio.IsOpen);
        Assert.False(await StillSendsAsync(audio, ct));
    }

    /// <summary>
    /// A send the caller cancels never leaves half a frame behind. The stream
    /// is shared by every frame of the channel, so a prefix without its body
    /// would have the reading end taking the next frame's data for a length
    /// from then on — which is why cancellation gives up the place in the
    /// queue and not a write already under way.
    /// </summary>
    [Fact]
    public async Task ACancelledSendDoesNotMisframeTheChannel()
    {
        using CancellationTokenSource cts = Deadline(TimeSpan.FromMinutes(2));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        FakeRelayGatewayFactory gateways = new(relay);

        await using ILinkHost host = await TailcatLink.HostManyAsync(
            "demo", OptionsFor(gateways, new InMemoryLinkStore()), ct);

        List<int> heard = [];
        TaskCompletionSource<Exception> broke = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<ChannelCloseReason> ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        host.OnChannel("audio", async (_, channel, token) =>
        {
            channel.Closed += (_, e) => ended.TrySetResult(e.Reason);
            try
            {
                await foreach (ReadOnlyMemory<byte> frame in channel.ReadAllAsync(token))
                {
                    heard.Add(BinaryPrimitives.ReadInt32BigEndian(frame.Span));
                }
            }
            catch (LinkException misframed)
            {
                broke.TrySetResult(misframed);
            }
        });

        await using ILink phone = await JoinAsync(gateways, host, ct);
        await host.WaitForPeerAsync(ct);

        ILinkChannelWriter audio = await phone.OpenChannelAsync("audio", ct);
        using CancellationTokenSource giveUp = new();
        List<Task> sending = [.. Enumerable.Range(0, Frames).Select(i => audio.SendAsync(Numbered(i), giveUp.Token))];
        await giveUp.CancelAsync();
        foreach (Task sent in sending)
        {
            await WaitForGivingUpAsync(sent);
        }

        // The frames that did get out are still whole and in order, which is
        // the assertion: a truncated one would have every frame after it read
        // at the wrong offset, and the ones sent here carry their own number.
        await using (audio)
        {
            await audio.SendAsync(Numbered(Frames), ct);
        }

        Assert.Equal(ChannelCloseReason.PeerClosed, await ended.Task.WaitAsync(ct));
        Assert.False(broke.Task.IsCompleted, "the reading end saw a length prefix that was not one");
        Assert.Equal(Frames, heard[^1]);
        Assert.Equal([.. heard.Order()], heard);
        Assert.Equal(heard.Distinct().Count(), heard.Count);
    }

    private static byte[] Numbered(int value)
    {
        byte[] frame = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(frame, value);
        return frame;
    }

    /// <summary>
    /// Waits for a send that raced the close: it either got out or was told
    /// the channel had ended, and anything else is the defect this covers.
    /// </summary>
    private static async Task WaitForEndingAsync(Task sent)
    {
        try
        {
            await sent;
        }
        catch (LinkException)
        {
        }
    }

    /// <summary>
    /// Waits for a send whose caller cancelled: it either got out or gave up
    /// its place in the queue, and the channel itself is fine either way.
    /// </summary>
    private static async Task WaitForGivingUpAsync(Task sent)
    {
        try
        {
            await sent;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task<bool> StillSendsAsync(ILinkChannelWriter audio, CancellationToken ct)
    {
        try
        {
            await audio.SendAsync(new byte[] { 4 }, ct);
            return true;
        }
        catch (LinkException)
        {
            return false;
        }
    }

    private static async Task<ILink> JoinAsync(
        FakeRelayGatewayFactory gateways,
        ILinkHost host,
        CancellationToken ct)
    {
        ILink link = await TailcatLink.JoinAsync(
            "demo",
            host.InvitationCode.Value,
            OptionsFor(gateways, new InMemoryLinkStore()),
            ct);
        await link.WaitUntilConnectedAsync(ct);
        return link;
    }
}

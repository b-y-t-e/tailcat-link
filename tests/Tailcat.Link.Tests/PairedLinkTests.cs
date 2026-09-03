// Copyright (c) Tailscale Inc & contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Tailcat.Keys;
using Tailcat.Link.Protocol;
using Tailcat.Link.Storage;
using Tailcat.Link.Transport;
using Tailcat.Net;

namespace Tailcat.Link.Tests;

/// <summary>
/// Covers the promise this library makes: pair two machines once with a code,
/// and from then on they find each other again after anything short of one of
/// them being thrown away.
/// </summary>
/// <remarks>
/// Every test runs against the in-memory relay, so CI runs all of it. Two
/// processes on one machine cannot prove NAT traversal — that is the transport
/// underneath, and its own tests cover it — but they can prove the part this
/// library is responsible for: that a link which was broken comes back with
/// nobody helping it.
/// </remarks>
public class PairedLinkTests
{
    /// <summary>
    /// Deliberately impatient compared to the defaults: a test should spend
    /// its time reconnecting, not waiting to notice that it must.
    /// </summary>
    private static LinkOptions OptionsFor(FakeRelayGatewayFactory gateways, ILinkStore store) => new()
    {
        Store = store,
        Gateway = gateways,
        RequestTimeout = TimeSpan.FromSeconds(5),
        RequestDeadline = TimeSpan.FromSeconds(45),
        HeartbeatInterval = TimeSpan.FromSeconds(1),
        MinReconnectDelay = TimeSpan.FromMilliseconds(200),
        MaxReconnectDelay = TimeSpan.FromSeconds(2),
    };

    private static CancellationTokenSource Deadline(TimeSpan limit)
    {
        CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        cts.CancelAfter(limit);
        return cts;
    }

    /// <summary>
    /// The whole of the intended usage: one machine shows a code, the other is
    /// given it, and both can ask the other for things.
    /// </summary>
    [Fact]
    public async Task ACodeIsAllItTakesToLinkTwoMachines()
    {
        using CancellationTokenSource cts = Deadline(TimeSpan.FromMinutes(2));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        FakeRelayGatewayFactory gateways = new(relay);

        await using ILink host = await TailcatLink.HostAsync(
            "demo", OptionsFor(gateways, new InMemoryLinkStore()), ct);
        host.OnRequest(command => $"host got: {command}");

        await using ILink operatorSide = await TailcatLink.JoinAsync(
            "demo", host.InvitationCode.Value, OptionsFor(gateways, new InMemoryLinkStore()), ct);
        operatorSide.OnRequest(command => $"operator got: {command}");

        Assert.Equal("host got: status", await operatorSide.RequestAsync("status", ct));

        // And the other way, on the same link: the host asks the operator.
        Assert.Equal("operator got: are you there", await host.RequestAsync("are you there", ct));

        // Each end has pinned the other, and knows it is up.
        Assert.False(host.Peer.IsZero);
        Assert.False(operatorSide.Peer.IsZero);
        Assert.True(host.IsConnected);
        Assert.True(operatorSide.IsConnected);
    }

    /// <summary>Messages that expect no answer still reach the peer's handler.</summary>
    [Fact]
    public async Task ANotificationNeedsNoAnswer()
    {
        using CancellationTokenSource cts = Deadline(TimeSpan.FromMinutes(2));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        FakeRelayGatewayFactory gateways = new(relay);
        ConcurrentQueue<string> heard = new();

        await using ILink host = await TailcatLink.HostAsync(
            "demo", OptionsFor(gateways, new InMemoryLinkStore()), ct);
        host.OnRequest(message =>
        {
            heard.Enqueue(message);
            return "";
        });

        await using ILink operatorSide = await TailcatLink.JoinAsync(
            "demo", host.InvitationCode.Value, OptionsFor(gateways, new InMemoryLinkStore()), ct);

        await operatorSide.NotifyAsync("disk is filling up", ct);

        await WaitUntilAsync(() => heard.Contains("disk is filling up"), "the notification should arrive", ct);
    }

    /// <summary>
    /// A handler that throws while dealing with a notification costs nothing:
    /// nobody is waiting for an answer, so the failure has nowhere to go, and
    /// the session must not go with it.
    /// </summary>
    [Fact]
    public async Task AFailingNotificationHandlerDoesNotCostTheLink()
    {
        using CancellationTokenSource cts = Deadline(TimeSpan.FromMinutes(2));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        FakeRelayGatewayFactory gateways = new(relay);

        await using ILink host = await TailcatLink.HostAsync(
            "demo", OptionsFor(gateways, new InMemoryLinkStore()), ct);
        host.OnRequest(message => message == "boom"
            ? throw new NotSupportedException("the handler cannot deal with this")
            : $"host got: {message}");

        await using ILink operatorSide = await TailcatLink.JoinAsync(
            "demo", host.InvitationCode.Value, OptionsFor(gateways, new InMemoryLinkStore()), ct);

        await operatorSide.NotifyAsync("boom", ct);

        Assert.Equal("host got: status", await operatorSide.RequestAsync("status", ct));
        Assert.True(operatorSide.IsConnected);
    }

    /// <summary>A payload far larger than one packet arrives whole.</summary>
    [Fact]
    public async Task ALargeMessageArrivesIntact()
    {
        using CancellationTokenSource cts = Deadline(TimeSpan.FromMinutes(2));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        FakeRelayGatewayFactory gateways = new(relay);

        await using ILink host = await TailcatLink.HostAsync(
            "demo", OptionsFor(gateways, new InMemoryLinkStore()), ct);
        host.OnRequest((request, _) => Task.FromResult<ReadOnlyMemory<byte>>(request));

        await using ILink operatorSide = await TailcatLink.JoinAsync(
            "demo", host.InvitationCode.Value, OptionsFor(gateways, new InMemoryLinkStore()), ct);

        byte[] payload = new byte[256 * 1024];
        Random.Shared.NextBytes(payload);

        Assert.Equal(payload, await operatorSide.RequestAsync(payload, ct));
    }

    /// <summary>
    /// A handler that throws is reported to the machine that asked, and the
    /// link carries on: one bad command must not cost the session.
    /// </summary>
    [Fact]
    public async Task AFailingHandlerIsReportedAndTheLinkSurvives()
    {
        using CancellationTokenSource cts = Deadline(TimeSpan.FromMinutes(2));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        FakeRelayGatewayFactory gateways = new(relay);

        await using ILink host = await TailcatLink.HostAsync(
            "demo", OptionsFor(gateways, new InMemoryLinkStore()), ct);
        host.OnRequest(command => command == "boom"
            ? throw new InvalidOperationException("the disk is on fire")
            : "fine");

        await using ILink operatorSide = await TailcatLink.JoinAsync(
            "demo", host.InvitationCode.Value, OptionsFor(gateways, new InMemoryLinkStore()), ct);

        RemoteHandlerException ex =
            await Assert.ThrowsAsync<RemoteHandlerException>(() => operatorSide.RequestAsync("boom", ct));
        Assert.Contains("the disk is on fire", ex.Message, StringComparison.Ordinal);

        Assert.Equal("fine", await operatorSide.RequestAsync("anything else", ct));
    }

    /// <summary>
    /// A handler slower than one request window is not a peer that has gone
    /// away: its answer still arrives, it runs once, and the session it shares
    /// with every other exchange is still standing afterwards.
    /// </summary>
    [Fact]
    public async Task AHandlerSlowerThanOneRequestWindowStillAnswers()
    {
        using CancellationTokenSource cts = Deadline(TimeSpan.FromMinutes(2));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        FakeRelayGatewayFactory gateways = new(relay);
        int runs = 0;

        await using ILink host = await TailcatLink.HostAsync(
            "demo", OptionsFor(gateways, new InMemoryLinkStore()), ct);
        LinkOptions options = OptionsFor(gateways, new InMemoryLinkStore());
        host.OnRequest(async (command, _) =>
        {
            Interlocked.Increment(ref runs);
            // Comfortably past RequestTimeout, well inside RequestDeadline.
            await Task.Delay(options.RequestTimeout * 2, ct);
            return $"eventually: {command}";
        });

        await using ILink operatorSide = await TailcatLink.JoinAsync(
            "demo", host.InvitationCode.Value, options, ct);

        Assert.Equal("eventually: slow", await operatorSide.RequestAsync("slow", ct));
        Assert.Equal(1, Volatile.Read(ref runs));
        Assert.True(operatorSide.IsConnected);
    }

    /// <summary>
    /// A request that outlives the session it was sent on is sent again, but
    /// it is not carried out again: the machine that ran it recognises the
    /// retry and answers from what it already produced.
    /// </summary>
    [Fact]
    public async Task ARequestThatOutlivesItsSessionIsNotRunTwice()
    {
        using CancellationTokenSource cts = Deadline(TimeSpan.FromMinutes(3));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        FakeRelayGatewayFactory gateways = new(relay);

        await using ILink host = await TailcatLink.HostAsync(
            "demo", OptionsFor(gateways, new InMemoryLinkStore()), ct);
        host.OnRequest(_ => "pong");
        await using ILink operatorSide = await TailcatLink.JoinAsync(
            "demo", host.InvitationCode.Value, OptionsFor(gateways, new InMemoryLinkStore()), ct);

        // Both ends must be paired before either knows whom to disconnect.
        Assert.Equal("pong", await operatorSide.RequestAsync("ping", ct));

        int runs = 0;
        host.OnRequest(_ =>
        {
            if (Interlocked.Increment(ref runs) == 1)
            {
                // The answer is lost with the session it was about to go out
                // on — exactly when a retry would run the command a second time.
                relay.DisconnectClient(host.Peer);
                relay.DisconnectClient(operatorSide.Peer);
            }
            return "restarted";
        });

        Assert.Equal("restarted", await operatorSide.RequestAsync("restart the service", ct));
        Assert.Equal(1, runs);
    }

    /// <summary>
    /// The same promise where it actually gets tested: the session dies while
    /// the handler is still working, which is how a link normally fails.
    /// Cancelling the handler with its session would leave its side effects
    /// half-done and let the retry start them again.
    /// </summary>
    [Fact]
    public async Task AHandlerStillRunningWhenItsSessionDiesIsNotRunTwice()
    {
        using CancellationTokenSource cts = Deadline(TimeSpan.FromMinutes(3));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        FakeRelayGatewayFactory gateways = new(relay);

        await using ILink host = await TailcatLink.HostAsync(
            "demo", OptionsFor(gateways, new InMemoryLinkStore()), ct);
        host.OnRequest(_ => "pong");
        await using ILink operatorSide = await TailcatLink.JoinAsync(
            "demo", host.InvitationCode.Value, OptionsFor(gateways, new InMemoryLinkStore()), ct);

        Assert.Equal("pong", await operatorSide.RequestAsync("ping", ct));

        int runs = 0;
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        host.OnRequest(async (_, handlerToken) =>
        {
            if (Interlocked.Increment(ref runs) == 1)
            {
                started.TrySetResult();
                await finished.Task.WaitAsync(handlerToken);
            }
            return "restarted";
        });

        Task<string> answer = operatorSide.RequestAsync("restart the service", ct);
        await started.Task.WaitAsync(ct);

        // Both machines lose the relay with the handler mid-flight.
        relay.DisconnectClient(host.Peer);
        relay.DisconnectClient(operatorSide.Peer);
        finished.SetResult();

        Assert.Equal("restarted", await answer);
        Assert.Equal(1, runs);
    }

    /// <summary>
    /// A payload over the frame cap is the caller's own mistake: no session
    /// would ever carry it, so it is refused at once and by its real name
    /// rather than retried until the deadline and reported as silence.
    /// </summary>
    [Fact]
    public async Task AMessageTooLargeToSendIsRefusedAtOnce()
    {
        using CancellationTokenSource cts = Deadline(TimeSpan.FromMinutes(2));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        FakeRelayGatewayFactory gateways = new(relay);

        await using ILink host = await TailcatLink.HostAsync(
            "demo", OptionsFor(gateways, new InMemoryLinkStore()), ct);
        host.OnRequest(_ => "pong");
        await using ILink operatorSide = await TailcatLink.JoinAsync(
            "demo", host.InvitationCode.Value, OptionsFor(gateways, new InMemoryLinkStore()), ct);
        Assert.Equal("pong", await operatorSide.RequestAsync("ping", ct));

        byte[] tooBig = new byte[LinkFrame.MaxPayloadBytes + 1];
        long startedAt = Stopwatch.GetTimestamp();

        LinkException refused = await Assert.ThrowsAsync<LinkException>(
            () => operatorSide.RequestAsync(tooBig, ct));
        Assert.Contains($"{LinkFrame.MaxPayloadBytes} bytes", refused.Message, StringComparison.Ordinal);

        LinkException refusedNotify = await Assert.ThrowsAsync<LinkException>(
            () => operatorSide.NotifyAsync(tooBig, ct));
        Assert.Contains($"{LinkFrame.MaxPayloadBytes} bytes", refusedNotify.Message, StringComparison.Ordinal);

        // Both refusals together must cost far less than the one request
        // deadline a retry loop would have burned through.
        Assert.True(Stopwatch.GetElapsedTime(startedAt) < TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// The code is a one-time thing. After the first pairing both machines
    /// know each other, so joining again needs nothing from a human.
    /// </summary>
    [Fact]
    public async Task TheCodeIsNeededOnlyOnce()
    {
        using CancellationTokenSource cts = Deadline(TimeSpan.FromMinutes(3));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        FakeRelayGatewayFactory gateways = new(relay);
        InMemoryLinkStore hostStore = new();
        InMemoryLinkStore operatorStore = new();

        await using ILink host = await TailcatLink.HostAsync("demo", OptionsFor(gateways, hostStore), ct);
        host.OnRequest(_ => "pong");

        ILink first = await TailcatLink.JoinAsync(
            "demo", host.InvitationCode.Value, OptionsFor(gateways, operatorStore), ct);
        Assert.Equal("pong", await first.RequestAsync("ping", ct));
        await first.DisposeAsync();

        // The operator's application restarts. Nobody types anything.
        await using ILink again = await TailcatLink.JoinAsync(
            "demo", invitationCode: null, OptionsFor(gateways, operatorStore), ct);

        Assert.Equal("pong", await again.RequestAsync("ping", ct));
        Assert.Equal(host.InvitationCode, again.InvitationCode);
    }

    /// <summary>
    /// Knowing where a host is does not get anybody in. The address half of a
    /// code is what the host hands to every relay it connects to, so it is
    /// not a secret it could keep; the token is, and without it a stranger
    /// cannot claim an unpaired machine before its operator does.
    /// </summary>
    [Fact]
    public async Task AStrangerWithTheAddressButNotTheTokenCannotPair()
    {
        using CancellationTokenSource cts = Deadline(TimeSpan.FromMinutes(3));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        FakeRelayGatewayFactory gateways = new(relay);

        await using ILink host = await TailcatLink.HostAsync(
            "demo", OptionsFor(gateways, new InMemoryLinkStore()), ct);
        host.OnRequest(_ => "pong");

        InvitationCode forged = InvitationCode.ForAddress(host.InvitationCode.Address, "a-guessed-token");
        LinkOptions impatient = OptionsFor(gateways, new InMemoryLinkStore()) with
        {
            RequestDeadline = TimeSpan.FromSeconds(10),
        };
        await using ILink stranger = await TailcatLink.JoinAsync("demo", forged.Value, impatient, ct);

        await Assert.ThrowsAsync<LinkException>(() => stranger.RequestAsync("status", ct));
        Assert.False(stranger.IsConnected);

        // And the machine is still there to be paired by whoever holds the
        // real code, however long the stranger keeps knocking.
        await using ILink operatorSide = await TailcatLink.JoinAsync(
            "demo", host.InvitationCode.Value, OptionsFor(gateways, new InMemoryLinkStore()), ct);

        Assert.Equal("pong", await operatorSide.RequestAsync("ping", ct));
    }

    /// <summary>
    /// A stranger that connects and then says nothing must not keep the peer
    /// from being heard. Knowing a host's address is enough to knock, and one
    /// candidate at a time would mean that knocking is all it takes to hold a
    /// host down — worst of all in the moment a dropped link is repairing.
    /// </summary>
    [Fact]
    public async Task StrangersThatConnectAndSayNothingDoNotStarveThePeer()
    {
        using CancellationTokenSource cts = Deadline(TimeSpan.FromMinutes(3));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        FakeRelayGatewayFactory gateways = new(relay);

        // The host is deliberately patient with a handshake, so a candidate
        // heard on its own would hold the machine for longer than the peer is
        // willing to wait.
        LinkOptions patient = OptionsFor(gateways, new InMemoryLinkStore()) with
        {
            RequestTimeout = TimeSpan.FromMinutes(1),
        };
        await using ILink host = await TailcatLink.HostAsync("demo", patient, ct);
        host.OnRequest(_ => "pong");

        await using INodeGateway firstStranger = await gateways.CreateAsync(
            NodePrivate.NewKey(), FakeRelayGatewayFactory.RegionId, ct);
        await using INodeGateway secondStranger = await gateways.CreateAsync(
            NodePrivate.NewKey(), FakeRelayGatewayFactory.RegionId, ct);
        await using TailcatConnection firstKnock =
            await firstStranger.ConnectAsync(host.InvitationCode.Address, ct);
        await using TailcatConnection secondKnock =
            await secondStranger.ConnectAsync(host.InvitationCode.Address, ct);

        LinkOptions impatient = OptionsFor(gateways, new InMemoryLinkStore()) with
        {
            RequestDeadline = TimeSpan.FromSeconds(30),
        };
        await using ILink operatorSide = await TailcatLink.JoinAsync(
            "demo", host.InvitationCode.Value, impatient, ct);

        Assert.Equal("pong", await operatorSide.RequestAsync("ping", ct));
    }

    /// <summary>Joining a machine that was never paired, with no code, says so plainly.</summary>
    [Fact]
    public async Task JoiningWithoutACodeOrAPairingIsRefused()
    {
        using CancellationTokenSource cts = Deadline(TimeSpan.FromMinutes(1));
        await using FakeDerpRelay relay = new();

        LinkException ex = await Assert.ThrowsAsync<LinkException>(() => TailcatLink.JoinAsync(
            "demo", invitationCode: null, OptionsFor(new FakeRelayGatewayFactory(relay), new InMemoryLinkStore()),
            cts.Token));

        Assert.Contains("invitation code", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The relay dropping both machines — a relay restart, or a NAT forgetting
    /// the connection — costs nothing but a moment.
    /// </summary>
    [Fact]
    public async Task TheLinkComesBackAfterTheRelayDropsBothMachines()
    {
        using CancellationTokenSource cts = Deadline(TimeSpan.FromMinutes(3));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        FakeRelayGatewayFactory gateways = new(relay);

        await using ILink host = await TailcatLink.HostAsync(
            "demo", OptionsFor(gateways, new InMemoryLinkStore()), ct);
        host.OnRequest(_ => "pong");
        await using ILink operatorSide = await TailcatLink.JoinAsync(
            "demo", host.InvitationCode.Value, OptionsFor(gateways, new InMemoryLinkStore()), ct);

        Assert.Equal("pong", await operatorSide.RequestAsync("ping", ct));

        relay.DisconnectClient(host.Peer);
        relay.DisconnectClient(operatorSide.Peer);

        Assert.Equal("pong", await operatorSide.RequestAsync("ping", ct));
    }

    /// <summary>
    /// An application's event handler is not part of the link's machinery, so
    /// one that throws must cost nothing: the supervision loop that raises
    /// Connected and Disconnected is the only thing keeping the link alive.
    /// </summary>
    [Fact]
    public async Task AThrowingEventHandlerDoesNotStopTheLink()
    {
        using CancellationTokenSource cts = Deadline(TimeSpan.FromMinutes(3));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        FakeRelayGatewayFactory gateways = new(relay);

        await using ILink host = await TailcatLink.HostAsync(
            "demo", OptionsFor(gateways, new InMemoryLinkStore()), ct);
        host.OnRequest(_ => "pong");
        await using ILink operatorSide = await TailcatLink.JoinAsync(
            "demo", host.InvitationCode.Value, OptionsFor(gateways, new InMemoryLinkStore()), ct);

        operatorSide.Connected += () => throw new InvalidOperationException("bad Connected handler");
        operatorSide.Disconnected += _ => throw new InvalidOperationException("bad Disconnected handler");

        Assert.Equal("pong", await operatorSide.RequestAsync("ping", ct));

        relay.DisconnectClient(host.Peer);
        relay.DisconnectClient(operatorSide.Peer);

        Assert.Equal("pong", await operatorSide.RequestAsync("ping", ct));
    }

    /// <summary>
    /// The machine nobody can reach reboots, and the code that was scanned
    /// once is still the right one — that address is the whole point of
    /// pinning the key and the region.
    /// </summary>
    [Fact]
    public async Task TheLinkComesBackAfterTheHostRestarts()
    {
        using CancellationTokenSource cts = Deadline(TimeSpan.FromMinutes(3));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        FakeRelayGatewayFactory gateways = new(relay);
        InMemoryLinkStore hostStore = new();

        ILink host = await TailcatLink.HostAsync("demo", OptionsFor(gateways, hostStore), ct);
        host.OnRequest(_ => "pong");
        InvitationCode published = host.InvitationCode;

        await using ILink operatorSide = await TailcatLink.JoinAsync(
            "demo", published.Value, OptionsFor(gateways, new InMemoryLinkStore()), ct);
        Assert.Equal("pong", await operatorSide.RequestAsync("ping", ct));

        await host.DisposeAsync();
        await using ILink restarted = await TailcatLink.HostAsync("demo", OptionsFor(gateways, hostStore), ct);
        restarted.OnRequest(_ => "pong again");

        Assert.Equal(published, restarted.InvitationCode);
        Assert.Equal("pong again", await operatorSide.RequestAsync("ping", ct));
    }

    /// <summary>
    /// Both machines lose their network stack outright — the case a node
    /// cannot repair from the inside — and the link rebuilds them from the
    /// stored identity.
    /// </summary>
    [Fact]
    public async Task TheLinkRebuildsItsNodeWhenTheNetworkVanishes()
    {
        using CancellationTokenSource cts = Deadline(TimeSpan.FromMinutes(3));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        FakeRelayGatewayFactory gateways = new(relay);
        LinkOptions options = OptionsFor(gateways, new InMemoryLinkStore()) with { RebuildNodeAfterFailures = 1 };

        await using ILink host = await TailcatLink.HostAsync("demo", options, ct);
        host.OnRequest(_ => "pong");
        await using ILink operatorSide = await TailcatLink.JoinAsync(
            "demo", host.InvitationCode.Value,
            OptionsFor(gateways, new InMemoryLinkStore()) with { RebuildNodeAfterFailures = 1 }, ct);

        Assert.Equal("pong", await operatorSide.RequestAsync("ping", ct));
        int nodesBefore = gateways.NodesCreated;

        await gateways.BreakEveryNodeAsync();

        Assert.Equal("pong", await operatorSide.RequestAsync("ping", ct));
        Assert.True(
            gateways.NodesCreated > nodesBefore,
            "the link should have built new nodes from the stored identity");
    }

    /// <summary>
    /// Once paired, the code is spent: a second machine holding it is turned
    /// away, and the machine that owns the pairing keeps working throughout.
    /// </summary>
    [Fact]
    public async Task AStrangerWithTheCodeIsTurnedAwayOncePaired()
    {
        using CancellationTokenSource cts = Deadline(TimeSpan.FromMinutes(3));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        FakeRelayGatewayFactory gateways = new(relay);

        await using ILink host = await TailcatLink.HostAsync(
            "demo", OptionsFor(gateways, new InMemoryLinkStore()), ct);
        host.OnRequest(_ => "pong");

        await using ILink paired = await TailcatLink.JoinAsync(
            "demo", host.InvitationCode.Value, OptionsFor(gateways, new InMemoryLinkStore()), ct);
        Assert.Equal("pong", await paired.RequestAsync("ping", ct));

        await using ILink stranger = await TailcatLink.JoinAsync(
            "demo",
            host.InvitationCode.Value,
            OptionsFor(gateways, new InMemoryLinkStore()) with { RequestDeadline = TimeSpan.FromSeconds(8) },
            ct);

        await Assert.ThrowsAsync<LinkException>(() => stranger.RequestAsync("ping", ct));
        Assert.Equal("pong", await paired.RequestAsync("ping", ct));
    }

    /// <summary>
    /// The machine nobody can reach is the one that must repair itself. A
    /// host waiting to be connected to cannot tell a peer that is away from a
    /// relay socket that died without saying so, so it stops waiting after a
    /// while and rebuilds the node — keeping the address its published code
    /// points at.
    /// </summary>
    [Fact]
    public async Task AHostThatHearsNothingRebuildsTheNodeItListensWith()
    {
        using CancellationTokenSource cts = Deadline(TimeSpan.FromMinutes(3));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        FakeRelayGatewayFactory gateways = new(relay);
        LinkOptions options = OptionsFor(gateways, new InMemoryLinkStore()) with
        {
            // Impatient compared to the default, but still longer than a
            // handshake: a rebuild tears down the node a peer may be dialling
            // at that moment, so a window shorter than one join is a machine
            // that rebuilds itself out of ever being reached.
            ListenSilenceTimeout = TimeSpan.FromSeconds(15),
            RebuildNodeAfterFailures = 1,
        };

        await using ILink host = await TailcatLink.HostAsync("demo", options, ct);
        host.OnRequest(_ => "pong");
        InvitationCode published = host.InvitationCode;
        int nodesBefore = gateways.NodesCreated;

        await WaitUntilAsync(
            () => gateways.NodesCreated > nodesBefore,
            "a host nobody has reached should stop trusting the node it is listening with",
            ct);

        // And it is still the same machine: the code that was published
        // before the rebuild reaches it.
        await using ILink operatorSide = await TailcatLink.JoinAsync(
            "demo", published.Value, OptionsFor(gateways, new InMemoryLinkStore()), ct);

        Assert.Equal("pong", await operatorSide.RequestAsync("ping", ct));
    }

    /// <summary>
    /// A host whose pairing window closed while it was running is not stuck
    /// showing a code it would refuse: it can mint another without the
    /// process being restarted, which is the only thing a machine nobody is
    /// standing next to could otherwise do.
    /// </summary>
    [Fact]
    public async Task AHostPastItsPairingWindowCanOfferAFreshCode()
    {
        using CancellationTokenSource cts = Deadline(TimeSpan.FromMinutes(3));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        FakeRelayGatewayFactory gateways = new(relay);

        // The host starts with an offer that runs out shortly after it comes
        // up, which is what a service started at boot looks like by the time
        // an operator gets to it.
        InMemoryLinkStore hostStore = new();
        PairingOffer expiring = new("written-down-token", DateTimeOffset.UtcNow + TimeSpan.FromSeconds(2));
        await hostStore.SaveAsync(
            "demo", new LinkState { PrivateKey = NodePrivate.NewKey(), Pairing = expiring }, ct);

        await using ILink host = await TailcatLink.HostAsync("demo", OptionsFor(gateways, hostStore), ct);
        host.OnRequest(_ => "pong");
        InvitationCode stale = host.InvitationCode;
        Assert.Equal(expiring.ExpiresAt, host.InvitationExpiresAt);

        await Task.Delay(TimeSpan.FromSeconds(3), ct);

        await using ILink late = await TailcatLink.JoinAsync(
            "demo",
            stale.Value,
            OptionsFor(gateways, new InMemoryLinkStore()) with { RequestDeadline = TimeSpan.FromSeconds(8) },
            ct);
        await Assert.ThrowsAsync<LinkException>(() => late.RequestAsync("ping", ct));

        InvitationCode fresh = await host.RenewInvitationAsync(ct);
        Assert.NotEqual(stale.Value, fresh.Value);
        // The same machine, with a new secret: the address is pinned for good.
        Assert.Equal(stale.Address, fresh.Address);

        await using ILink operatorSide = await TailcatLink.JoinAsync(
            "demo", fresh.Value, OptionsFor(gateways, new InMemoryLinkStore()), ct);

        Assert.Equal("pong", await operatorSide.RequestAsync("ping", ct));
        // Paired: there is nothing left for the code to buy, and so nothing
        // left to expire.
        Assert.Null(host.InvitationExpiresAt);
    }

    /// <summary>Closing a link twice is harmless, as it is everywhere else in this repository.</summary>
    [Fact]
    public async Task DisposingTwiceIsHarmless()
    {
        using CancellationTokenSource cts = Deadline(TimeSpan.FromMinutes(1));
        await using FakeDerpRelay relay = new();

        ILink host = await TailcatLink.HostAsync(
            "demo", OptionsFor(new FakeRelayGatewayFactory(relay), new InMemoryLinkStore()), cts.Token);

        await host.DisposeAsync();
        await host.DisposeAsync();
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string because, CancellationToken ct)
    {
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        while (!condition())
        {
            try
            {
                await Task.Delay(50, cts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                Assert.Fail(because);
            }
        }
    }
}

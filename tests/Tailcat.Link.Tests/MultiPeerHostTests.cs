// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Tailcat.Link.Storage;

namespace Tailcat.Link.Tests;

using static LinkHarness;

/// <summary>
/// Covers a host that holds more than one machine: who gets in, how many, what
/// they are called, and what unpairing one of them does to the others.
/// </summary>
/// <remarks>
/// One identity and one node underneath all of it — the thing an application
/// with several clients would otherwise fake with one link per client.
/// </remarks>
public class MultiPeerHostTests
{
    private static LinkOptions HostOptions(FakeRelayGatewayFactory gateways, ILinkStore store, int maxPeers) =>
        OptionsFor(gateways, store) with { MaxPeers = maxPeers };

    /// <summary>
    /// Two machines pair with one host, and the host answers both — from one
    /// handler, told which of them is asking.
    /// </summary>
    [Fact]
    public async Task TwoMachinesPairWithOneHost()
    {
        using CancellationTokenSource cts = Deadline(TimeSpan.FromMinutes(2));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        FakeRelayGatewayFactory gateways = new(relay);

        await using ILinkHost host = await TailcatLink.HostManyAsync(
            "demo", HostOptions(gateways, new InMemoryLinkStore(), maxPeers: 2), ct);
        host.SetRequestHandler((peer, _, _) =>
            Task.FromResult<ReadOnlyMemory<byte>>(System.Text.Encoding.UTF8.GetBytes(peer.Name ?? "unnamed")));

        await using ILink kitchen = await JoinAsync(gateways, host, "kitchen phone", ct);
        await using ILink hallway = await JoinAsync(gateways, host, "hallway phone", ct);

        Assert.Equal("kitchen phone", await kitchen.RequestAsync("who am I", ct));
        Assert.Equal("hallway phone", await hallway.RequestAsync("who am I", ct));

        Assert.Equal(2, host.Peers.Count);
        Assert.Equal(
            ["hallway phone", "kitchen phone"],
            host.Peers.Select(peer => peer.Name).Order(StringComparer.Ordinal));
        Assert.All(host.Peers, peer => Assert.True(peer.IsConnected));
    }

    /// <summary>
    /// The bound is a bound. A third machine holding a perfectly good code is
    /// refused, because who may reach the handler is not a resource question.
    /// </summary>
    [Fact]
    public async Task AMachineBeyondTheBoundIsRefused()
    {
        using CancellationTokenSource cts = Deadline(TimeSpan.FromMinutes(2));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        FakeRelayGatewayFactory gateways = new(relay);

        await using ILinkHost host = await TailcatLink.HostManyAsync(
            "demo", HostOptions(gateways, new InMemoryLinkStore(), maxPeers: 1), ct);
        host.SetRequestHandler((_, _, _) => Task.FromResult<ReadOnlyMemory<byte>>("pong"u8.ToArray()));

        await using ILink first = await JoinAsync(gateways, host, "the one that fits", ct);
        Assert.Equal("pong", await first.RequestAsync("ping", ct));

        await using ILink extra = await TailcatLink.JoinAsync(
            "demo",
            host.InvitationCode.Value,
            OptionsFor(gateways, new InMemoryLinkStore()) with { RequestDeadline = TimeSpan.FromSeconds(8) },
            ct);
        ConcurrentQueue<LinkDisconnectReason> refusals = [];
        extra.SessionEnded += (_, e) => refusals.Enqueue(e.Reason);

        await Assert.ThrowsAsync<LinkTimeoutException>(() => extra.RequestAsync("ping", ct));
        Assert.Single(host.Peers);

        // The refused machine is told which "no" it got, because a fresh
        // invitation is the only thing that answers this one and an interface
        // has to know to ask for it. That is an attempt which never became a
        // session, so it is also the case the reason code exists for.
        Assert.Contains(LinkDisconnectReason.Refused, refusals);

        // And it is still connecting rather than reconnecting: nothing was
        // ever up here, however many attempts it has made.
        Assert.Equal(LinkConnectionState.Connecting, extra.State);
    }

    /// <summary>
    /// A host with no room left refuses to mint an invitation, rather than
    /// handing the operator a code that admission will turn away without ever
    /// looking at it.
    /// </summary>
    [Fact]
    public async Task AFullHostRefusesToMintAnInvitation()
    {
        using CancellationTokenSource cts = Deadline(TimeSpan.FromMinutes(2));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        FakeRelayGatewayFactory gateways = new(relay);

        await using ILinkHost host = await TailcatLink.HostManyAsync(
            "demo", HostOptions(gateways, new InMemoryLinkStore(), maxPeers: 1), ct);
        host.SetRequestHandler((_, _, _) => Task.FromResult<ReadOnlyMemory<byte>>("pong"u8.ToArray()));

        await using ILink only = await JoinAsync(gateways, host, "the one that fits", ct);
        Assert.Equal("pong", await only.RequestAsync("ping", ct));

        LinkException refused = await Assert.ThrowsAsync<LinkException>(
            () => host.InviteAsync(new InvitationRequest { Label = "kitchen phone" }, ct));
        Assert.Contains("forget one", refused.Message, StringComparison.Ordinal);

        // And nothing was minted: the operator's list is not left showing a
        // code that pairs with nobody.
        Assert.DoesNotContain(host.Invitations, invitation => invitation.Label == "kitchen phone");

        // Room made is room enough: the same call works once the machine that
        // filled the host is unpaired.
        await host.ForgetPeerAsync(host.Peers[0], ct);
        LinkInvitation minted = await host.InviteAsync(new InvitationRequest { Label = "kitchen phone" }, ct);
        Assert.Equal("kitchen phone", Assert.Single(host.Invitations, i => i.Id == minted.Id).Label);
    }

    /// <summary>
    /// A single-use invitation is spent by the machine that takes it, so the
    /// code left on a screen buys nothing afterwards.
    /// </summary>
    [Fact]
    public async Task ASingleUseInvitationIsSpentByTheMachineThatTakesIt()
    {
        using CancellationTokenSource cts = Deadline(TimeSpan.FromMinutes(2));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        FakeRelayGatewayFactory gateways = new(relay);

        await using ILinkHost host = await TailcatLink.HostManyAsync(
            "demo", HostOptions(gateways, new InMemoryLinkStore(), maxPeers: 4), ct);
        host.SetRequestHandler((_, _, _) => Task.FromResult<ReadOnlyMemory<byte>>("pong"u8.ToArray()));

        LinkInvitation invitation = await host.InviteAsync(
            new InvitationRequest { Label = "kitchen phone", SingleUse = true }, ct);
        Assert.Equal("kitchen phone", Assert.Single(host.Invitations, i => i.Id == invitation.Id).Label);

        await using ILink kitchen = await TailcatLink.JoinAsync(
            "demo", invitation.Code.Value, OptionsFor(gateways, new InMemoryLinkStore()), ct);
        Assert.Equal("pong", await kitchen.RequestAsync("ping", ct));

        Assert.DoesNotContain(host.Invitations, i => i.Id == invitation.Id);
        await using ILink shoulderSurfer = await TailcatLink.JoinAsync(
            "demo",
            invitation.Code.Value,
            OptionsFor(gateways, new InMemoryLinkStore()) with { RequestDeadline = TimeSpan.FromSeconds(8) },
            ct);
        await Assert.ThrowsAsync<LinkTimeoutException>(() => shoulderSurfer.RequestAsync("ping", ct));
    }

    /// <summary>
    /// An invitation that ran out is refused in exactly the words a wrong
    /// token gets — and said plainly to the operator, who is the only one who
    /// can do anything about it.
    /// </summary>
    /// <remarks>
    /// It is the one refusal with an obvious cure, and the cure is a fresh
    /// invitation. A line that reads like every other failed handshake leaves
    /// whoever is watching with nothing to act on, which is why the level is
    /// asserted here and not only the words.
    /// </remarks>
    [Fact]
    public async Task AnExpiredInvitationIsTheOneRefusalTheOperatorIsToldAbout()
    {
        using CancellationTokenSource cts = Deadline(TimeSpan.FromMinutes(2));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        FakeRelayGatewayFactory gateways = new(relay);
        using CapturingLoggerFactory logs = new();

        await using ILinkHost host = await TailcatLink.HostManyAsync(
            "demo",
            HostOptions(gateways, new InMemoryLinkStore(), maxPeers: 4) with { LoggerFactory = logs },
            ct);
        host.SetRequestHandler((_, _, _) => Task.FromResult<ReadOnlyMemory<byte>>("pong"u8.ToArray()));

        // Minted with a window rather than waited out, so the test spends no
        // time being sure the moment has passed.
        LinkInvitation invitation = await host.InviteAsync(
            new InvitationRequest { Label = "kitchen phone", Lifetime = TimeSpan.FromMilliseconds(1) }, ct);
        await Task.Delay(TimeSpan.FromMilliseconds(50), ct);

        await using ILink late = await TailcatLink.JoinAsync(
            "demo",
            invitation.Code.Value,
            OptionsFor(gateways, new InMemoryLinkStore()) with { RequestDeadline = TimeSpan.FromSeconds(8) },
            ct);
        await Assert.ThrowsAsync<LinkTimeoutException>(() => late.RequestAsync("ping", ct));

        Assert.Empty(host.Peers);
        Assert.True(
            logs.Said(LogLevel.Warning, "invitation whose window has closed"),
            $"the host never said the invitation had run out; it said: {logs}");
    }

    /// <summary>A withdrawn invitation pairs nobody, even before it expires.</summary>
    [Fact]
    public async Task ARevokedInvitationPairsNobody()
    {
        using CancellationTokenSource cts = Deadline(TimeSpan.FromMinutes(2));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        FakeRelayGatewayFactory gateways = new(relay);

        await using ILinkHost host = await TailcatLink.HostManyAsync(
            "demo", HostOptions(gateways, new InMemoryLinkStore(), maxPeers: 4), ct);
        host.SetRequestHandler((_, _, _) => Task.FromResult<ReadOnlyMemory<byte>>("pong"u8.ToArray()));

        LinkInvitation invitation = await host.InviteAsync(new InvitationRequest { Label = "withdrawn" }, ct);
        Assert.True(await host.RevokeInvitationAsync(invitation.Id, ct));
        Assert.False(await host.RevokeInvitationAsync(invitation.Id, ct));

        await using ILink turnedAway = await TailcatLink.JoinAsync(
            "demo",
            invitation.Code.Value,
            OptionsFor(gateways, new InMemoryLinkStore()) with { RequestDeadline = TimeSpan.FromSeconds(8) },
            ct);
        await Assert.ThrowsAsync<LinkTimeoutException>(() => turnedAway.RequestAsync("ping", ct));
        Assert.Empty(host.Peers);
    }

    /// <summary>
    /// Unpairing one device drops it and leaves the other paired: this is the
    /// "unpair this device" every application with a device list needs, and it
    /// is not <see cref="TailcatLink.ForgetAsync"/>, which takes the machine's
    /// own identity with it.
    /// </summary>
    [Fact]
    public async Task ForgettingOnePeerLeavesTheOthers()
    {
        using CancellationTokenSource cts = Deadline(TimeSpan.FromMinutes(2));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        FakeRelayGatewayFactory gateways = new(relay);

        await using ILinkHost host = await TailcatLink.HostManyAsync(
            "demo", HostOptions(gateways, new InMemoryLinkStore(), maxPeers: 2), ct);
        host.SetRequestHandler((_, _, _) => Task.FromResult<ReadOnlyMemory<byte>>("pong"u8.ToArray()));

        await using ILink kitchen = await JoinAsync(gateways, host, "kitchen phone", ct);
        await using ILink hallway = await JoinAsync(gateways, host, "hallway phone", ct);

        ILinkPeer unwanted = host.Peers.Single(peer => peer.Name == "kitchen phone");
        await host.ForgetPeerAsync(unwanted, ct);

        Assert.Equal("hallway phone", Assert.Single(host.Peers).Name);
        Assert.Equal("pong", await hallway.RequestAsync("ping", ct));
    }

    /// <summary>
    /// A host tells the application which machines come and go, so a device
    /// list can be drawn without polling.
    /// </summary>
    [Fact]
    public async Task TheHostSaysWhenAPeerArrives()
    {
        using CancellationTokenSource cts = Deadline(TimeSpan.FromMinutes(2));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        FakeRelayGatewayFactory gateways = new(relay);

        await using ILinkHost host = await TailcatLink.HostManyAsync(
            "demo", HostOptions(gateways, new InMemoryLinkStore(), maxPeers: 2), ct);
        host.SetRequestHandler((_, _, _) => Task.FromResult<ReadOnlyMemory<byte>>("pong"u8.ToArray()));

        ConcurrentQueue<string> joined = [];
        TaskCompletionSource arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        host.PeerJoined += (_, e) =>
        {
            joined.Enqueue(e.Peer.Name ?? "unnamed");
            arrived.TrySetResult();
        };

        await using ILink kitchen = await JoinAsync(gateways, host, "kitchen phone", ct);
        await arrived.Task.WaitAsync(ct);

        Assert.Contains("kitchen phone", joined);
        ILinkPeer peer = Assert.Single(host.Peers);
        Assert.Equal(LinkConnectionState.Connected, peer.State);
        Assert.NotEqual(default, peer.PairedAt);
    }

    /// <summary>
    /// A host restarted under a lower bound does not quietly keep the peers
    /// it no longer has room for. They were admitted to the application's
    /// handler under the old bound, and a bound that only applies to machines
    /// arriving from now on is not a security bound at all.
    /// </summary>
    [Fact]
    public async Task RestartingUnderALowerBoundDropsThePeersItNoLongerHasRoomFor()
    {
        using CancellationTokenSource cts = Deadline(TimeSpan.FromMinutes(2));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        FakeRelayGatewayFactory gateways = new(relay);
        InMemoryLinkStore store = new();

        await using (ILinkHost roomForTwo = await TailcatLink.HostManyAsync(
            "demo", HostOptions(gateways, store, maxPeers: 2), ct))
        {
            roomForTwo.SetRequestHandler((_, _, _) => Task.FromResult<ReadOnlyMemory<byte>>("pong"u8.ToArray()));
            await using ILink kitchen = await JoinAsync(gateways, roomForTwo, "kitchen phone", ct);
            await using ILink hallway = await JoinAsync(gateways, roomForTwo, "hallway phone", ct);
            // Answered rather than merely connected, so both are certainly
            // written down before the host is closed.
            Assert.Equal("pong", await kitchen.RequestAsync("ping", ct));
            Assert.Equal("pong", await hallway.RequestAsync("ping", ct));
            Assert.Equal(2, roomForTwo.Peers.Count);
        }

        // Whichever of them was written down last is the one that survives the
        // narrowing: the store is what the next start reads, and the running
        // host's own view of when it last saw them goes with it.
        LinkState stored = await store.LoadAsync("demo", ct)
            ?? throw new InvalidOperationException("the host stored nothing");
        string kept = stored.Peers.MaxBy(peer => peer.LastSeen)!.Name!;

        await using ILinkHost roomForOne = await TailcatLink.HostManyAsync(
            "demo", HostOptions(gateways, store, maxPeers: 1), ct);

        Assert.Equal(kept, Assert.Single(roomForOne.Peers).Name);
    }

    /// <summary>
    /// Hosting one machine with options that ask for several is refused
    /// rather than narrowed. Narrowing would run the bound against the store
    /// as well as the process, so a caller reusing the options of a
    /// <see cref="TailcatLink.HostManyAsync"/> host would unpair its machines
    /// and be told in a log line.
    /// </summary>
    [Fact]
    public async Task HostingOneMachineRefusesOptionsThatAskForSeveral()
    {
        using CancellationTokenSource cts = Deadline(TimeSpan.FromMinutes(2));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        FakeRelayGatewayFactory gateways = new(relay);
        InMemoryLinkStore store = new();

        await using (ILinkHost roomForTwo = await TailcatLink.HostManyAsync(
            "demo", HostOptions(gateways, store, maxPeers: 2), ct))
        {
            roomForTwo.SetRequestHandler((_, _, _) => Task.FromResult<ReadOnlyMemory<byte>>("pong"u8.ToArray()));
            await using ILink kitchen = await JoinAsync(gateways, roomForTwo, "kitchen phone", ct);
            await using ILink hallway = await JoinAsync(gateways, roomForTwo, "hallway phone", ct);
            Assert.Equal("pong", await kitchen.RequestAsync("ping", ct));
            Assert.Equal("pong", await hallway.RequestAsync("ping", ct));
        }

        await Assert.ThrowsAsync<ArgumentException>(
            () => TailcatLink.HostAsync("demo", HostOptions(gateways, store, maxPeers: 2), ct));

        // Both machines are still paired: nothing was written down on the way
        // to being refused.
        LinkState stored = await store.LoadAsync("demo", ct)
            ?? throw new InvalidOperationException("the host stored nothing");
        Assert.Equal(2, stored.Peers.Count);
    }

    /// <summary>
    /// Closing a host twice is not an error, as it is for everything else
    /// disposable here: a using block inside a finally clause is ordinary.
    /// </summary>
    [Fact]
    public async Task DisposingTwiceIsHarmless()
    {
        using CancellationTokenSource cts = Deadline(TimeSpan.FromMinutes(1));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        FakeRelayGatewayFactory gateways = new(relay);

        ILinkHost host = await TailcatLink.HostManyAsync(
            "demo", HostOptions(gateways, new InMemoryLinkStore(), maxPeers: 2), ct);

        await host.DisposeAsync();
        await host.DisposeAsync();
    }

    /// <summary>
    /// A display name that can never fit on the wire is refused by the call
    /// that took it. Left to the supervision loop it would look like a relay
    /// having a bad minute and be retried for ever, so the caller would hold a
    /// link that never connects and never says why.
    /// </summary>
    [Fact]
    public async Task JoiningWithAnOversizedDisplayNameIsRefusedAtTheCall()
    {
        using CancellationTokenSource cts = Deadline(TimeSpan.FromMinutes(1));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        FakeRelayGatewayFactory gateways = new(relay);

        await using ILinkHost host = await TailcatLink.HostManyAsync(
            "demo", HostOptions(gateways, new InMemoryLinkStore(), maxPeers: 2), ct);

        await Assert.ThrowsAsync<ArgumentException>(() => TailcatLink.JoinAsync(
            "demo",
            host.InvitationCode.Value,
            new JoinRequest { DisplayName = new string('n', 257) },
            OptionsFor(gateways, new InMemoryLinkStore()),
            ct));
    }

    private static async Task<ILink> JoinAsync(
        FakeRelayGatewayFactory gateways,
        ILinkHost host,
        string displayName,
        CancellationToken ct)
    {
        ILink link = await TailcatLink.JoinAsync(
            "demo",
            host.InvitationCode.Value,
            new JoinRequest { DisplayName = displayName },
            OptionsFor(gateways, new InMemoryLinkStore()),
            ct);
        await link.WaitUntilConnectedAsync(ct);
        return link;
    }
}

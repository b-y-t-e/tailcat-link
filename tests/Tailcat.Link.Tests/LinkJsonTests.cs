// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Text.Json;
using System.Text.Json.Serialization;
using Tailcat.Link.Json;
using Tailcat.Link.Storage;

namespace Tailcat.Link.Tests;

using static LinkHarness;

/// <summary>What an application asks for, as an object rather than bytes.</summary>
public sealed record Greeting(string Name);

/// <summary>What it gets back.</summary>
public sealed record Greeted(string Text, int Length);

/// <summary>
/// Source-generated metadata, which is the only way <c>Tailcat.Link.Json</c>
/// lets anything be serialised: an application that trims or compiles ahead
/// of time must behave exactly like one that does not.
/// </summary>
[JsonSerializable(typeof(Greeting))]
[JsonSerializable(typeof(Greeted))]
internal sealed partial class GreetingContext : JsonSerializerContext;

/// <summary>
/// Covers the <c>Tailcat.Link.Json</c> package end to end: objects go over a
/// real pair of linked machines, and what a handler does wrong arrives as the
/// exception the byte-level API would have raised.
/// </summary>
public class LinkJsonTests
{
    /// <summary>
    /// The whole point of the package: both ends name their types and neither
    /// writes a serializer.
    /// </summary>
    [Fact]
    public async Task ObjectsGoOverTheLinkInBothDirections()
    {
        using CancellationTokenSource cts = Deadline(TimeSpan.FromMinutes(2));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        FakeRelayGatewayFactory gateways = new(relay);

        await using ILink host = await TailcatLink.HostAsync(
            "demo", OptionsFor(gateways, new InMemoryLinkStore()), ct);
        host.SetRequestHandler(
            GreetingContext.Default.Greeting,
            GreetingContext.Default.Greeted,
            (greeting, _) => Task.FromResult(new Greeted($"hello {greeting.Name}", greeting.Name.Length)));

        TaskCompletionSource<Greeting> noticed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using ILink guest = await TailcatLink.JoinAsync(
            "demo", host.InvitationCode.Value, OptionsFor(gateways, new InMemoryLinkStore()), ct);
        guest.SetRequestHandler(
            GreetingContext.Default.Greeting,
            GreetingContext.Default.Greeted,
            (greeting, _) =>
            {
                noticed.TrySetResult(greeting);
                return Task.FromResult(new Greeted(greeting.Name, greeting.Name.Length));
            });

        Greeted answer = await guest.RequestAsync(
            new Greeting("world"),
            GreetingContext.Default.Greeting,
            GreetingContext.Default.Greeted,
            ct);
        Assert.Equal(new Greeted("hello world", 5), answer);

        // A notification reaches the same typed handler, whose answer is
        // thrown away.
        await host.NotifyAsync(new Greeting("nobody"), GreetingContext.Default.Greeting, ct);
        Assert.Equal(new Greeting("nobody"), await noticed.Task.WaitAsync(ct));
    }

    /// <summary>
    /// A typed handler that throws is still the other machine's failure, and
    /// reads as one rather than as a serialization problem.
    /// </summary>
    [Fact]
    public async Task AHandlerThatThrowsArrivesAsTheRemoteFailureItIs()
    {
        using CancellationTokenSource cts = Deadline(TimeSpan.FromMinutes(2));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        FakeRelayGatewayFactory gateways = new(relay);

        await using ILink host = await TailcatLink.HostAsync(
            "demo", OptionsFor(gateways, new InMemoryLinkStore()), ct);
        host.SetRequestHandler<Greeting, Greeted>(
            GreetingContext.Default.Greeting,
            GreetingContext.Default.Greeted,
            (_, _) => throw new InvalidOperationException("the greeter is out"));

        await using ILink guest = await TailcatLink.JoinAsync(
            "demo", host.InvitationCode.Value, OptionsFor(gateways, new InMemoryLinkStore()), ct);

        RemoteHandlerException thrown = await Assert.ThrowsAsync<RemoteHandlerException>(
            () => guest.RequestAsync(
                new Greeting("world"),
                GreetingContext.Default.Greeting,
                GreetingContext.Default.Greeted,
                ct));
        Assert.Contains("the greeter is out", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An answer that is not the shape asked for is the other machine's fault
    /// and reads as one, rather than as a <see cref="JsonException"/> nobody
    /// expected from a link.
    /// </summary>
    [Fact]
    public async Task AnAnswerOfTheWrongShapeReadsAsALinkFailure()
    {
        using CancellationTokenSource cts = Deadline(TimeSpan.FromMinutes(2));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        FakeRelayGatewayFactory gateways = new(relay);

        await using ILink host = await TailcatLink.HostAsync(
            "demo", OptionsFor(gateways, new InMemoryLinkStore()), ct);
        host.OnRequest(_ => "not json at all");

        await using ILink guest = await TailcatLink.JoinAsync(
            "demo", host.InvitationCode.Value, OptionsFor(gateways, new InMemoryLinkStore()), ct);

        LinkException thrown = await Assert.ThrowsAsync<LinkException>(
            () => guest.RequestAsync(
                new Greeting("world"),
                GreetingContext.Default.Greeting,
                GreetingContext.Default.Greeted,
                ct));
        Assert.IsType<JsonException>(thrown.InnerException);
    }

    /// <summary>
    /// The host's overloads: one typed handler, told which machine is asking,
    /// and a typed request sent back to that same machine.
    /// </summary>
    [Fact]
    public async Task AHostAnswersEveryMachineFromOneTypedHandler()
    {
        using CancellationTokenSource cts = Deadline(TimeSpan.FromMinutes(2));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        FakeRelayGatewayFactory gateways = new(relay);

        await using ILinkHost host = await TailcatLink.HostManyAsync(
            "demo", OptionsFor(gateways, new InMemoryLinkStore()) with { MaxPeers = 2 }, ct);
        host.SetRequestHandler(
            GreetingContext.Default.Greeting,
            GreetingContext.Default.Greeted,
            (peer, greeting, _) =>
                Task.FromResult(new Greeted($"{peer.Name} says {greeting.Name}", greeting.Name.Length)));

        await using ILink kitchen = await JoinAsync(gateways, host, "kitchen phone", ct);
        await using ILink hallway = await JoinAsync(gateways, host, "hallway phone", ct);

        Assert.Equal(
            new Greeted("kitchen phone says hi", 2),
            await kitchen.RequestAsync(
                new Greeting("hi"), GreetingContext.Default.Greeting, GreetingContext.Default.Greeted, ct));
        Assert.Equal(
            new Greeted("hallway phone says hi", 2),
            await hallway.RequestAsync(
                new Greeting("hi"), GreetingContext.Default.Greeting, GreetingContext.Default.Greeted, ct));

        // And the same call from the host's side, which goes through the peer
        // rather than through a link.
        TaskCompletionSource<Greeting> notified = new(TaskCreationOptions.RunContinuationsAsynchronously);
        kitchen.SetRequestHandler(
            GreetingContext.Default.Greeting,
            GreetingContext.Default.Greeted,
            (greeting, _) =>
            {
                if (greeting.Name == "again")
                {
                    notified.TrySetResult(greeting);
                }
                return Task.FromResult(new Greeted($"kitchen heard {greeting.Name}", greeting.Name.Length));
            });

        ILinkPeer peer = host.Peers.Single(candidate => candidate.Name == "kitchen phone");
        Assert.Equal(
            new Greeted("kitchen heard knock", 5),
            await peer.RequestAsync(
                new Greeting("knock"), GreetingContext.Default.Greeting, GreetingContext.Default.Greeted, ct));

        await peer.NotifyAsync(new Greeting("again"), GreetingContext.Default.Greeting, ct);
        Assert.Equal(new Greeting("again"), await notified.Task.WaitAsync(ct));
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

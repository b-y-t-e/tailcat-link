// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Net.Quic;
using Tailcat.Link.Storage;
using Tailcat.Net;

namespace Tailcat.Link.Tests;

using static LinkHarness;

/// <summary>
/// Covers <see cref="ILink.Route"/>: an application can tell a link that
/// punched through from one still crossing the relay, which is the first
/// thing to ask about a slow transfer.
/// </summary>
public class LinkRouteTests
{
    [Fact]
    public async Task ALinkSaysWhenItHasGoneDirect()
    {
        if (!QuicListener.IsSupported)
        {
            Assert.Skip("this machine has no QUIC; Windows 10 has none and Linux needs libmsquic");
        }

        using CancellationTokenSource cts = Deadline(TimeSpan.FromMinutes(2));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        FakeRelayGatewayFactory gateways = new(relay);

        await using ILink host = await TailcatLink.HostAsync(
            "demo", OptionsFor(gateways, new InMemoryLinkStore()), ct);
        host.OnRequest(command => $"host got: {command}");
        Assert.Equal(LinkRoute.None, host.Route);

        await using ILink joiner = await TailcatLink.JoinAsync(
            "demo", host.InvitationCode.Value, OptionsFor(gateways, new InMemoryLinkStore()), ct);
        Task direct = RouteReachedAsync(joiner, LinkRoute.Direct, ct);
        Assert.Equal("host got: status", await joiner.RequestAsync("status", ct));

        // Loopback always punches, so both ends move off the relay.
        await direct;
        await RouteReachedAsync(host, LinkRoute.Direct, ct);
    }

    [Fact]
    public async Task ALinkThatCannotPunchSaysItIsRelayed()
    {
        using CancellationTokenSource cts = Deadline(TimeSpan.FromMinutes(2));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        FakeRelayGatewayFactory gateways = new(relay) { DirectPaths = false };

        await using ILink host = await TailcatLink.HostAsync(
            "demo", OptionsFor(gateways, new InMemoryLinkStore()), ct);
        host.OnRequest(command => $"host got: {command}");

        await using ILink joiner = await TailcatLink.JoinAsync(
            "demo", host.InvitationCode.Value, OptionsFor(gateways, new InMemoryLinkStore()), ct);
        Assert.Equal("host got: status", await joiner.RequestAsync("status", ct));

        await RouteReachedAsync(joiner, LinkRoute.Relayed, ct);
        await RouteReachedAsync(host, LinkRoute.Relayed, ct);
    }

    /// <summary>
    /// <c>relay1</c> has no direct path to move to, however reachable the two
    /// machines are, and must not claim one.
    /// </summary>
    [Fact]
    public async Task TheRelayedTransportIsAlwaysRelayed()
    {
        using CancellationTokenSource cts = Deadline(TimeSpan.FromMinutes(2));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        FakeRelayGatewayFactory gateways = new(relay, [PeerTransport.Relay1]);

        await using ILink host = await TailcatLink.HostAsync(
            "demo", OptionsFor(gateways, new InMemoryLinkStore()), ct);
        host.OnRequest(command => $"host got: {command}");

        await using ILink joiner = await TailcatLink.JoinAsync(
            "demo", host.InvitationCode.Value, OptionsFor(gateways, new InMemoryLinkStore()), ct);
        Assert.Equal("host got: status", await joiner.RequestAsync("status", ct));

        await RouteReachedAsync(joiner, LinkRoute.Relayed, ct);
        Assert.Equal(LinkRoute.Relayed, joiner.Route);
    }

    private static async Task RouteReachedAsync(ILink link, LinkRoute want, CancellationToken ct)
    {
        TaskCompletionSource reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        link.RouteChanged += (_, changed) =>
        {
            if (changed.Route == want)
            {
                reached.TrySetResult();
            }
        };
        if (link.Route != want)
        {
            await reached.Task.WaitAsync(ct);
        }
    }
}

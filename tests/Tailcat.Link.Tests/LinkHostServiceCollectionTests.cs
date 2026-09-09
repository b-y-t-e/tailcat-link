// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Tailcat.Link.Extensions.DependencyInjection;
using Tailcat.Link.Storage;

namespace Tailcat.Link.Tests;

using static LinkHarness;

/// <summary>
/// Covers the host with the application's lifetime: that it is built once,
/// that it is what gets injected, and that shutting the container down the
/// way most applications do takes it with it.
/// </summary>
public class LinkHostServiceCollectionTests
{
    /// <summary>
    /// <c>using IHost host = builder.Build()</c> is how nearly every
    /// application is written, and it disposes the container synchronously —
    /// which a singleton that is only <see cref="IAsyncDisposable"/> refuses.
    /// The assertion is the <c>using</c> itself: if disposing throws, the
    /// test fails, which is the shutdown this package exists to make dull.
    /// </summary>
    [Fact]
    public async Task TheContainerCanBeDisposedSynchronously()
    {
        using CancellationTokenSource cts = Deadline(TimeSpan.FromMinutes(2));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        FakeRelayGatewayFactory gateways = new(relay);

        ServiceCollection services = new();
        services.AddTailcatLinkHost("demo", _ => OptionsFor(gateways, new InMemoryLinkStore()));

        using ServiceProvider provider = services.BuildServiceProvider();
        LinkHostSource source = provider.GetRequiredService<LinkHostSource>();
        await source.StartAsync(ct);

        ILinkHost host = provider.GetRequiredService<ILinkHost>();
        Assert.Equal(source.Host.InvitationCode.Value, host.InvitationCode.Value);
        Assert.False(host.InvitationCode.IsEmpty);
    }

    /// <summary>
    /// A worker taking <see cref="ILinkHost"/> in its constructor is built
    /// before any hosted service runs, so resolving one must not need the
    /// link to be up yet — otherwise the most obvious use of this package
    /// fails at start-up.
    /// </summary>
    [Fact]
    public void TheHostResolvesBeforeTheApplicationStarts()
    {
        ServiceCollection services = new();
        services.AddTailcatLinkHost("demo");

        using ServiceProvider provider = services.BuildServiceProvider();

        ILinkHost host = provider.GetRequiredService<ILinkHost>();
        Assert.Throws<InvalidOperationException>(() => host.MaxPeers);
    }

    /// <summary>
    /// Stopping the application takes the host with it, rather than leaving a
    /// node and its relay connections up until the container is disposed —
    /// admitting peers into handlers whose dependencies are already stopping.
    /// </summary>
    [Fact]
    public async Task StoppingTheApplicationClosesTheHost()
    {
        using CancellationTokenSource cts = Deadline(TimeSpan.FromMinutes(2));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        FakeRelayGatewayFactory gateways = new(relay);

        ServiceCollection services = new();
        services.AddTailcatLinkHost("demo", _ => OptionsFor(gateways, new InMemoryLinkStore()));

        await using ServiceProvider provider = services.BuildServiceProvider();
        IHostedService lifetime = Assert.Single(provider.GetServices<IHostedService>());

        await lifetime.StartAsync(ct);
        Assert.False(provider.GetRequiredService<ILinkHost>().InvitationCode.IsEmpty);

        await lifetime.StopAsync(ct);

        LinkHostSource source = provider.GetRequiredService<LinkHostSource>();
        Assert.Throws<ObjectDisposedException>(() => source.Host);
    }

    /// <summary>
    /// Starting twice is one host, because the hosted service and anything
    /// that resolved it early must not each stand a node up.
    /// </summary>
    [Fact]
    public async Task StartingTwiceBuildsOneHost()
    {
        using CancellationTokenSource cts = Deadline(TimeSpan.FromMinutes(2));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        FakeRelayGatewayFactory gateways = new(relay);

        ServiceCollection services = new();
        services.AddTailcatLinkHost("demo", _ => OptionsFor(gateways, new InMemoryLinkStore()));

        await using ServiceProvider provider = services.BuildServiceProvider();
        LinkHostSource source = provider.GetRequiredService<LinkHostSource>();

        Assert.Same(await source.StartAsync(ct), await source.StartAsync(ct));
    }

    /// <summary>
    /// A worker unsubscribes in its <c>Dispose</c>, which the container runs
    /// after the hosted service has already taken the host down. Unsubscribing
    /// from what is gone is nothing to do, not an error that takes the
    /// application's shutdown with it.
    /// </summary>
    [Fact]
    public async Task UnsubscribingAfterShutdownIsHarmless()
    {
        using CancellationTokenSource cts = Deadline(TimeSpan.FromMinutes(2));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        FakeRelayGatewayFactory gateways = new(relay);

        ServiceCollection services = new();
        services.AddTailcatLinkHost("demo", _ => OptionsFor(gateways, new InMemoryLinkStore()));

        await using ServiceProvider provider = services.BuildServiceProvider();
        ILinkHost injected = provider.GetRequiredService<ILinkHost>();
        void OnJoined(object? sender, PeerEventArgs e) { }
        void OnLeft(object? sender, PeerLeftEventArgs e) { }
        injected.PeerJoined += OnJoined;
        injected.PeerLeft += OnLeft;

        IHostedService lifetime = Assert.Single(provider.GetServices<IHostedService>());
        await lifetime.StartAsync(ct);
        await lifetime.StopAsync(ct);

        injected.PeerJoined -= OnJoined;
        injected.PeerLeft -= OnLeft;

        // Setting one still says so: only taking a subscription back is quiet.
        Assert.Throws<ObjectDisposedException>(() => injected.PeerJoined += OnJoined);
    }

    /// <summary>
    /// An application that shuts down while the node is still coming up gets
    /// the host it built taken down again, rather than a node and its relay
    /// connections left running to the end of the process. The shutdown is
    /// started from inside the options factory, which is the one moment the
    /// build is provably still in flight.
    /// </summary>
    [Fact]
    public async Task ShuttingDownWhileTheHostIsComingUpClosesIt()
    {
        using CancellationTokenSource cts = Deadline(TimeSpan.FromMinutes(2));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        FakeRelayGatewayFactory gateways = new(relay);

        LinkHostSource? source = null;
        ServiceCollection services = new();
        services.AddTailcatLinkHost("demo", _ =>
        {
            source!.DisposeAsync().AsTask().GetAwaiter().GetResult();
            return OptionsFor(gateways, new InMemoryLinkStore());
        });

        await using ServiceProvider provider = services.BuildServiceProvider();
        source = provider.GetRequiredService<LinkHostSource>();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => source.StartAsync(ct));
        Assert.Throws<ObjectDisposedException>(() => source.Host);
    }

    /// <summary>
    /// A second host in one container is refused rather than dropped. The
    /// registration is by type, so keeping the first one would leave an
    /// application that asked for two pairings hosting one and told nothing.
    /// </summary>
    [Fact]
    public void RegisteringASecondHostIsRefused()
    {
        ServiceCollection services = new();
        services.AddTailcatLinkHost("app-a");

        InvalidOperationException refused =
            Assert.Throws<InvalidOperationException>(() => services.AddTailcatLinkHost("app-b"));
        Assert.Contains("app-b", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A worker subscribing in its constructor, or in a <c>StartAsync</c> that
    /// runs before this package's own, is what the injected host exists for.
    /// The registration is held until the link is up rather than refused, so
    /// the very first peer to join is still seen and still answered.
    /// </summary>
    [Fact]
    public async Task HandlersSetBeforeTheApplicationStartsReachTheHost()
    {
        using CancellationTokenSource cts = Deadline(TimeSpan.FromMinutes(2));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        FakeRelayGatewayFactory gateways = new(relay);

        ServiceCollection services = new();
        services.AddTailcatLinkHost("demo", _ => OptionsFor(gateways, new InMemoryLinkStore()));

        await using ServiceProvider provider = services.BuildServiceProvider();

        // Everything a worker would do while the container is still being built.
        ILinkHost injected = provider.GetRequiredService<ILinkHost>();
        TaskCompletionSource<string> joined = new(TaskCreationOptions.RunContinuationsAsynchronously);
        injected.PeerJoined += (_, e) => joined.TrySetResult(e.Peer.Name ?? "unnamed");
        injected.SetRequestHandler((_, _, _) => Task.FromResult<ReadOnlyMemory<byte>>("pong"u8.ToArray()));

        LinkHostSource source = provider.GetRequiredService<LinkHostSource>();
        await source.StartAsync(ct);

        await using ILink phone = await TailcatLink.JoinAsync(
            "demo",
            injected.InvitationCode.Value,
            new JoinRequest { DisplayName = "kitchen phone" },
            OptionsFor(gateways, new InMemoryLinkStore()),
            ct);
        await phone.WaitUntilConnectedAsync(ct);

        Assert.Equal("pong", await phone.RequestAsync("ping", ct));
        Assert.Equal("kitchen phone", await joined.Task.WaitAsync(ct));
    }
}

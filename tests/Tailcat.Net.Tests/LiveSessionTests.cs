// Copyright (c) Tailscale Inc & contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Text;

namespace Tailcat.Net.Tests;

/// <summary>
/// End-to-end tests over a real DERP relay. They need outbound internet
/// access and they use Tailscale's public, rate-limited relays, so they run
/// only when <c>TAILCAT_LIVE_TESTS=1</c> is set.
/// </summary>
[Trait("Category", "Live")]
public class LiveSessionTests
{
    // A public relay is rate limited and shared, and these tests run
    // alongside every other test project, so the handshake gets a margin well
    // beyond what it needs on an idle machine. The point is to verify that a
    // session forms at all, not how fast this machine is under load.
    private static readonly TailcatNodeOptions LiveOptions = new()
    {
        HandshakeTimeout = TimeSpan.FromSeconds(60),
    };

    private static void RequireLiveTests()
    {
        if (Environment.GetEnvironmentVariable("TAILCAT_LIVE_TESTS") != "1")
        {
            Assert.Skip("set TAILCAT_LIVE_TESTS=1 to run tests against a public DERP relay");
        }
    }

    /// <summary>
    /// Two nodes that know only each other's public keys establish a session
    /// through a relay and exchange a stream over it.
    /// </summary>
    [Fact]
    public async Task TwoNodesExchangeAStream()
    {
        RequireLiveTests();
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromMinutes(3));
        CancellationToken ct = cts.Token;

        await using TailcatNode listener = await TailcatNode.CreateAsync(LiveOptions, ct);
        await using TailcatNode dialer = await TailcatNode.CreateAsync(LiveOptions, ct);

        Task<string> served = Task.Run(async () =>
        {
            await using TailcatConnection conn = await listener.AcceptConnectionAsync(ct);
            await using Stream stream = await conn.AcceptStreamAsync(ct);
            byte[] buf = new byte[256];
            int n = await stream.ReadAsync(buf, ct);
            string got = Encoding.UTF8.GetString(buf, 0, n);
            await stream.WriteAsync(Encoding.UTF8.GetBytes(got.ToUpperInvariant()), ct);
            await stream.FlushAsync(ct);
            return got;
        }, ct);

        await using TailcatConnection client = await dialer.ConnectAsync(listener.PublicKey, ct);
        await using Stream s = await client.OpenStreamAsync(ct);
        await s.WriteAsync("hello peer"u8.ToArray(), ct);
        await s.FlushAsync(ct);

        byte[] reply = new byte[256];
        int read = await s.ReadAsync(reply, ct);

        Assert.Equal("HELLO PEER", Encoding.UTF8.GetString(reply, 0, read));
        Assert.Equal("hello peer", await served);
        Assert.Equal(listener.PublicKey, client.Peer);
    }

    /// <summary>
    /// A session starts on the relay and moves to a direct path once one is
    /// punched open. Between two hostile NATs there may be none, so a relayed
    /// session is a valid outcome — what must hold is that traffic keeps
    /// working either way.
    /// </summary>
    [Fact]
    public async Task SessionPrefersADirectPathWhenOneExists()
    {
        RequireLiveTests();
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromMinutes(3));
        CancellationToken ct = cts.Token;

        await using TailcatNode listener = await TailcatNode.CreateAsync(LiveOptions, ct);
        await using TailcatNode dialer = await TailcatNode.CreateAsync(LiveOptions, ct);

        Task serve = Task.Run(async () =>
        {
            await using TailcatConnection conn = await listener.AcceptConnectionAsync(ct);
            await using Stream stream = await conn.AcceptStreamAsync(ct);
            byte[] hello = new byte[4];
            await stream.ReadExactlyAsync(hello, ct);
            await stream.WriteAsync("ok"u8.ToArray(), ct);
            await stream.FlushAsync(ct);
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }, ct);

        await using TailcatConnection client = await dialer.ConnectAsync(listener.PublicKey, ct);
        await using Stream s = await client.OpenStreamAsync(ct);

        // QUIC opens a stream lazily: until the opener writes, the peer never
        // sees it. So the client speaks first, then reads the answer.
        await s.WriteAsync("ping"u8.ToArray(), ct);
        await s.FlushAsync(ct);
        byte[] buf = new byte[8];
        await s.ReadExactlyAsync(buf.AsMemory(0, 2), ct);

        bool direct = await client.WaitForDirectPathAsync(TimeSpan.FromSeconds(30), ct);

        // Both nodes are on this machine, so a direct path must be found here.
        Assert.True(direct, $"no direct path; paths were: {string.Join(", ", client.Paths)}");
        Assert.Equal(PeerPathKind.Direct, client.CurrentPath.Kind);
        Assert.NotNull(client.CurrentPath.Rtt);

        // The relay stays a candidate, so the session survives losing the
        // direct path.
        Assert.Contains(client.Paths, p => p.Kind == PeerPathKind.Relay);

        await serve;
    }

    /// <summary>
    /// Disposing a node twice must be harmless: callers wrap it in `await
    /// using` and may also close it explicitly.
    /// </summary>
    [Fact]
    public async Task DisposingTwiceIsHarmless()
    {
        RequireLiveTests();
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromMinutes(2));

        TailcatNode node = await TailcatNode.CreateAsync(LiveOptions, cts.Token);

        await node.DisposeAsync();
        await node.DisposeAsync();
    }

    /// <summary>A node learns at least one address peers could reach it at.</summary>
    [Fact]
    public async Task NodeDiscoversItsOwnEndpoints()
    {
        RequireLiveTests();
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromMinutes(2));

        await using TailcatNode node = await TailcatNode.CreateAsync(LiveOptions, cts.Token);

        IReadOnlyList<System.Net.IPEndPoint> endpoints = await node.LocalEndpointsAsync(cts.Token);

        Assert.NotEmpty(endpoints);
        Assert.All(endpoints, ep => Assert.NotEqual(0, ep.Port));
    }
}

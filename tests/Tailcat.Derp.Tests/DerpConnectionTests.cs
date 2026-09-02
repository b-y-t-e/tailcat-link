// Copyright (c) Tailscale Inc & contributors
// SPDX-License-Identifier: BSD-3-Clause

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

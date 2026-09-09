// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using Tailcat.Link.Storage;

namespace Tailcat.Link.Tests;

/// <summary>
/// What every end-to-end test in this project needs: options aimed at the
/// in-memory relay, and a deadline so a test that hangs fails rather than
/// running until the suite is killed.
/// </summary>
internal static class LinkHarness
{
    /// <summary>
    /// Deliberately impatient compared to the defaults: a test should spend
    /// its time reconnecting, not waiting to notice that it must.
    /// </summary>
    public static LinkOptions OptionsFor(FakeRelayGatewayFactory gateways, ILinkStore store) => new()
    {
        Store = store,
        Gateway = gateways,
        RequestTimeout = TimeSpan.FromSeconds(5),
        RequestDeadline = TimeSpan.FromSeconds(45),
        HeartbeatInterval = TimeSpan.FromSeconds(1),
        MinReconnectDelay = TimeSpan.FromMilliseconds(200),
        MaxReconnectDelay = TimeSpan.FromSeconds(2),
    };

    /// <summary>Gives the test <paramref name="limit"/> to finish in.</summary>
    public static CancellationTokenSource Deadline(TimeSpan limit)
    {
        CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        cts.CancelAfter(limit);
        return cts;
    }
}

// Copyright (c) Tailscale Inc & contributors
// SPDX-License-Identifier: BSD-3-Clause

using Tailcat.Link.Protocol;

namespace Tailcat.Link.Tests;

/// <summary>
/// A value that cannot work must be refused where it is set. Accepted here, it
/// would surface much later as an argument error thrown deep inside the
/// supervision loop, which treats what it cannot recognise as unrecoverable —
/// so a mistyped option would kill the link for good instead of failing at the
/// line that made it.
/// </summary>
public sealed class LinkOptionsTests
{
    public static TheoryData<string, Func<LinkOptions>> ImpossibleValues() => new()
    {
        { nameof(LinkOptions.PairingWindow), () => new LinkOptions { PairingWindow = TimeSpan.Zero } },
        { nameof(LinkOptions.RequestDeadline), () => new LinkOptions { RequestDeadline = TimeSpan.Zero } },
        {
            // A deadline stretched to the whole retention window leaves its
            // last retry no time to travel: it would land on the other machine
            // after that machine forgot the exchange, and run the handler
            // again.
            $"{nameof(LinkOptions.RequestDeadline)} spanning the whole retention window",
            () => new LinkOptions { RequestDeadline = LinkProtocol.ExchangeRetention }
        },
        {
            nameof(LinkOptions.ListenSilenceTimeout),
            () => new LinkOptions { ListenSilenceTimeout = TimeSpan.FromSeconds(-1) }
        },
        {
            nameof(LinkOptions.HeartbeatInterval),
            () => new LinkOptions { HeartbeatInterval = TimeSpan.Zero }
        },
        {
            nameof(LinkOptions.MinReconnectDelay),
            () => new LinkOptions { MinReconnectDelay = TimeSpan.FromSeconds(-1) }
        },
        {
            nameof(LinkOptions.MaxReconnectDelay),
            () => new LinkOptions { MaxReconnectDelay = TimeSpan.Zero }
        },
        { nameof(LinkOptions.RebuildNodeAfterFailures), () => new LinkOptions { RebuildNodeAfterFailures = 0 } },
    };

    [Theory]
    [MemberData(nameof(ImpossibleValues))]
    public void AnImpossibleValueIsRefusedWhereItIsSet(string option, Func<LinkOptions> set)
    {
        ArgumentOutOfRangeException refused = Assert.Throws<ArgumentOutOfRangeException>(() => set());

        // The setter's parameter is the record initialiser's `value`, so which
        // option was refused is carried by the test's name, not the message.
        Assert.True(refused.ParamName == "value", $"{option} should refuse a value it cannot work with");
    }

    /// <summary>
    /// A maximum below the minimum is not a mistake this can catch: the two are
    /// set in whatever order the initialiser lists them, so the pair is only
    /// whole once it is built. It caps the wait, which is what was asked for.
    /// </summary>
    [Fact]
    public void AMaximumBelowTheMinimumIsAccepted()
    {
        LinkOptions options = new()
        {
            MinReconnectDelay = TimeSpan.FromSeconds(10),
            MaxReconnectDelay = TimeSpan.FromSeconds(1),
        };

        Assert.Equal(TimeSpan.FromSeconds(1), options.MaxReconnectDelay);
    }
}

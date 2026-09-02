// Copyright (c) Tailscale Inc & contributors
// SPDX-License-Identifier: BSD-3-Clause

using Tailcat.Cli;

namespace Tailcat.Cli.Tests;

/// <summary>Port of TestNormalizeListenAddrPort from cmd/tailcat/socks_test.go.</summary>
public class ListenAddrTests
{
    [Theory]
    [InlineData("integer only", "1234", "127.0.0.1:1234")]
    [InlineData("omit address means all interfaces", ":1234", ":1234")]
    [InlineData("omit port with IPv4 address", "127.0.0.1", "127.0.0.1:0")]
    [InlineData("omit port with IPv6 address", "[2001:db8::1]", "[2001:db8::1]:0")]
    [InlineData("others", "foo", "foo:0")]
    public void NormalizeFillsInTheMissingHalf(string name, string input, string want) =>
        Assert.True(ListenAddr.Normalize(input) == want, $"{name}: got {ListenAddr.Normalize(input)}, want {want}");

    /// <summary>
    /// An address that already names both halves is left alone, and an empty
    /// port gets the OS-assigned one.
    /// </summary>
    [Theory]
    [InlineData("127.0.0.1:1234", "127.0.0.1:1234")]
    [InlineData("[2001:db8::1]:1234", "[2001:db8::1]:1234")]
    [InlineData("localhost:", "localhost:0")]
    [InlineData(":", ":0")]
    public void NormalizeKeepsCompleteAddresses(string input, string want) =>
        Assert.Equal(want, ListenAddr.Normalize(input));
}

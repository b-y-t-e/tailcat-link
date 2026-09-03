// Copyright (c) Tailscale Inc & contributors
// Copyright (c) Andrzej Ból and contributors (.NET port)
// SPDX-License-Identifier: BSD-3-Clause

using System.Net;
using Tailcat.Cli;

namespace Tailcat.Cli.Tests;

/// <summary>Port of TestClassifySOCKSAddr from cmd/tailcat/socks_test.go.</summary>
public class SocksAddrTests
{
    // NoLookup fails the test if DNS is consulted at all.
    private static LookupAddresses NoLookup => (host, _) =>
        throw new InvalidOperationException($"unexpected lookup of {host}");

    private static LookupAddresses LookupOf(params string[] ips) =>
        (_, _) => Task.FromResult<IReadOnlyList<IPAddress>>([.. ips.Select(IPAddress.Parse)]);

    private static IPEndPoint Ap(string s) => IPEndPoint.Parse(s);

    public static TheoryData<string, string, LookupAddresses, SocksTarget> Cases() => new()
    {
        {
            "server_magic_name", "server.tailcat:8081", NoLookup,
            new SocksTarget(ToServer: true, Port: 8081)
        },
        {
            "empty_host", ":80", NoLookup,
            new SocksTarget(ToServer: true, Port: 80)
        },
        {
            "blob_host",
            "tcomFwWCCcjS5nKNqAod034nWoJZW0LZqDhhC8U_dKdnDRYQ8uNGFpGQEu:8081", NoLookup,
            new SocksTarget(
                Blob: new ConnBlob("tcomFwWCCcjS5nKNqAod034nWoJZW0LZqDhhC8U_dKdnDRYQ8uNGFpGQEu"),
                Port: 8081)
        },
        {
            "tc_prefixed_non_blob_host_uses_lookup", "tcserver:80", LookupOf("192.0.2.1"),
            new SocksTarget(Dst: Ap("192.0.2.1:80"))
        },
        {
            "ipv4_literal", "10.1.2.3:80", NoLookup,
            new SocksTarget(Dst: Ap("10.1.2.3:80"))
        },
        {
            "ipv6_literal", "[2001:db8::1]:443", NoLookup,
            new SocksTarget(Dst: Ap("[2001:db8::1]:443"))
        },
        {
            "ipv4_mapped_literal_unmapped", "[::ffff:1.2.3.4]:80", NoLookup,
            new SocksTarget(Dst: Ap("1.2.3.4:80"))
        },
        {
            "hostname_prefers_ipv4", "example.com:80", LookupOf("2001:db8::1", "192.0.2.1"),
            new SocksTarget(Dst: Ap("192.0.2.1:80"))
        },
        {
            "hostname_ipv6_only", "example.com:80", LookupOf("2001:db8::1"),
            new SocksTarget(Dst: Ap("[2001:db8::1]:80"))
        },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task ClassifySocksAddr(string name, string addr, LookupAddresses lookup, SocksTarget want)
    {
        SocksTarget got = await SocksAddr.ClassifyAsync(addr, lookup, TestContext.Current.CancellationToken);

        Assert.True(got == want, $"{name}: ClassifyAsync({addr}) = {got}; want {want}");
    }

    public static TheoryData<string, string, LookupAddresses> ErrorCases() => new()
    {
        {
            "hostname_lookup_error", "example.com:80",
            (_, _) => throw new TailcatException("nope")
        },
        { "hostname_no_addresses", "example.com:80", LookupOf() },
        { "missing_port", "example.com", NoLookup },
        { "bad_port", "example.com:99999", NoLookup },
    };

    [Theory]
    [MemberData(nameof(ErrorCases))]
    public async Task ClassifySocksAddrRejectsBadInput(string name, string addr, LookupAddresses lookup)
    {
        Exception? ex = await Record.ExceptionAsync(
            () => SocksAddr.ClassifyAsync(addr, lookup, TestContext.Current.CancellationToken));

        Assert.True(ex is not null, $"{name}: ClassifyAsync({addr}) succeeded; want error");
    }
}

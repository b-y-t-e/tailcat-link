// Copyright (c) Tailscale Inc & contributors
// Copyright (c) Andrzej Ból and contributors (.NET port)
// SPDX-License-Identifier: BSD-3-Clause

using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Tailcat.Cli;

/// <summary>
/// Resolves a host name to IP addresses, for
/// <see cref="SocksAddr.ClassifyAsync"/>. It is the seam Go passes as the
/// <c>lookup</c> function argument, so tests can answer without DNS.
/// </summary>
/// <param name="host">The host name to resolve.</param>
/// <param name="cancellationToken">Cancels the lookup.</param>
public delegate Task<IReadOnlyList<IPAddress>> LookupAddresses(string host, CancellationToken cancellationToken);

/// <summary>Classifies SOCKS5 destination addresses for "tailcat socks".</summary>
public static class SocksAddr
{
    /// <summary>The magic host name meaning the tailcat server itself.</summary>
    public const string ServerHostName = "server.tailcat";

    /// <summary>Resolves a host with the local resolver, the default lookup.</summary>
    public static async Task<IReadOnlyList<IPAddress>> LookupNetIPAsync(string host, CancellationToken cancellationToken = default) =>
        await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Decides where the SOCKS5 destination <paramref name="addr"/> should be
    /// dialed.
    /// </summary>
    /// <remarks>
    /// The magic hostname "server.tailcat" (or an empty host) means the
    /// tailcat server itself. A hostname that is a valid address blob (which
    /// can never contain a dot) means the server that blob names, letting
    /// blobs be used directly in URLs. IP literals and hostnames resolved
    /// with <paramref name="lookup"/> are reached through the server acting
    /// as an exit node, preferring IPv4 addresses because they ride the NAT64
    /// mapping and the server may not have IPv6 connectivity.
    /// </remarks>
    /// <exception cref="FormatException">If the address has no port, or a bad one.</exception>
    /// <exception cref="TailcatException">If the host resolves to no addresses.</exception>
    public static async Task<SocksTarget> ClassifyAsync(
        string addr,
        LookupAddresses lookup,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(addr);
        ArgumentNullException.ThrowIfNull(lookup);

        (string host, string port) = HostPort.Split(addr);
        if (!ushort.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out ushort portNum))
        {
            throw new FormatException($"invalid port {port} in address {addr}");
        }

        if (host is ServerHostName or "")
        {
            return new SocksTarget(ToServer: true, Port: portNum);
        }

        if (host.StartsWith(ConnBlob.Prefix, StringComparison.Ordinal) && !host.Contains('.', StringComparison.Ordinal))
        {
            if (new ConnBlob(host).TryParse(out _))
            {
                return new SocksTarget(Blob: new ConnBlob(host), Port: portNum);
            }
        }

        if (!IPAddress.TryParse(host, out IPAddress? ip))
        {
            IReadOnlyList<IPAddress> ips = await lookup(host, cancellationToken).ConfigureAwait(false);
            if (ips.Count == 0)
            {
                throw new TailcatException($"no addresses found for {host}");
            }
            ip = ips[0];
            foreach (IPAddress a in ips)
            {
                if (Unmap(a).AddressFamily == AddressFamily.InterNetwork)
                {
                    ip = a;
                    break;
                }
            }
        }

        return new SocksTarget(Dst: new IPEndPoint(Unmap(ip), portNum));
    }

    // Unmap turns an IPv4-mapped IPv6 address back into a plain IPv4 one, as
    // Go's netip.Addr.Unmap does. Other addresses are returned unchanged.
    private static IPAddress Unmap(IPAddress ip) =>
        ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
}

// Copyright (c) Tailscale Inc & contributors
// SPDX-License-Identifier: BSD-3-Clause

namespace Tailcat.Cli;

/// <summary>
/// Splits and joins "host:port" strings the way Go's <c>net.SplitHostPort</c>
/// and <c>net.JoinHostPort</c> do, which is the shape the CLI's address
/// arguments are specified in.
/// </summary>
internal static class HostPort
{
    /// <summary>
    /// Splits "host:port", "[ipv6]:port" or ":port". A missing port is an
    /// error, and a bracketed host must be an IPv6 literal. The port may be
    /// empty, as in "host:".
    /// </summary>
    /// <exception cref="FormatException">If <paramref name="addr"/> has no port.</exception>
    public static (string Host, string Port) Split(string addr)
    {
        if (addr.StartsWith('['))
        {
            int end = addr.IndexOf(']', StringComparison.Ordinal);
            if (end < 0)
            {
                throw new FormatException($"missing ']' in address {addr}");
            }
            if (end + 1 >= addr.Length || addr[end + 1] != ':')
            {
                throw new FormatException($"missing port in address {addr}");
            }
            return (addr[1..end], addr[(end + 2)..]);
        }

        int colon = addr.LastIndexOf(':');
        if (colon < 0)
        {
            throw new FormatException($"missing port in address {addr}");
        }
        string host = addr[..colon];
        if (host.Contains(':', StringComparison.Ordinal))
        {
            // An unbracketed IPv6 literal: Go rejects it here too.
            throw new FormatException($"too many colons in address {addr}");
        }
        return (host, addr[(colon + 1)..]);
    }

    /// <summary>
    /// Splits as <see cref="Split"/> does, returning false instead of throwing
    /// when <paramref name="addr"/> carries no port.
    /// </summary>
    public static bool TrySplit(string addr, out string host, out string port)
    {
        try
        {
            (host, port) = Split(addr);
            return true;
        }
        catch (FormatException)
        {
            (host, port) = ("", "");
            return false;
        }
    }

    /// <summary>
    /// Joins a host and port, bracketing a host that is an IPv6 literal, as
    /// Go's <c>net.JoinHostPort</c> does.
    /// </summary>
    public static string Join(string host, string port) =>
        host.Contains(':', StringComparison.Ordinal) ? $"[{host}]:{port}" : $"{host}:{port}";
}

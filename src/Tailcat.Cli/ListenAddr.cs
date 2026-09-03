// Copyright (c) Tailscale Inc & contributors
// Copyright (c) Andrzej Ból and contributors (.NET port)
// SPDX-License-Identifier: BSD-3-Clause

using System.Globalization;

namespace Tailcat.Cli;

/// <summary>The listen address of "tailcat socks --listen".</summary>
public static class ListenAddr
{
    /// <summary>
    /// Fills in the missing parts of a <c>--listen</c> flag value so it names
    /// both a host and a port. A bare port means localhost on that port; a
    /// bare host means an OS-assigned port; an empty host (as in ":1234") is
    /// left alone, meaning all interfaces.
    /// </summary>
    /// <remarks>
    /// It doesn't validate the result, leaving that to whoever binds the
    /// socket, which is where a bad address gets its error message anyway.
    /// Rewriting an empty host to "0.0.0.0" would look equivalent but is not:
    /// it silently downgrades ":1234" from a dual-stack listen to IPv4-only.
    /// </remarks>
    public static string Normalize(string s)
    {
        ArgumentNullException.ThrowIfNull(s);

        if (HostPort.TrySplit(s, out string host, out string port))
        {
            return HostPort.Join(host, port.Length == 0 ? "0" : port);
        }
        if (ushort.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out ushort bare))
        {
            return HostPort.Join("127.0.0.1", bare.ToString(CultureInfo.InvariantCulture));
        }

        // Assume it's a hostname or IP without a port.
        return s + ":0";
    }
}

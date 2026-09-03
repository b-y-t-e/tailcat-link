// Copyright (c) Tailscale Inc & contributors
// Copyright (c) Andrzej Ból and contributors (.NET port)
// SPDX-License-Identifier: BSD-3-Clause

using System.Net;
using Tailcat.Keys;

namespace Tailcat;

/// <summary>
/// Derives the tunnel IP addresses tailcat nodes give themselves. There is
/// no control plane to assign them, so a node's address is a function of its
/// public key.
/// </summary>
public static class TcAddr
{
    /// <summary>
    /// Returns the IPv6 address a node with public key <paramref name="key"/>
    /// uses inside the tunnel.
    /// </summary>
    /// <remarks>
    /// It uses Tailscale's ULA range fd7a:115c:a1e0::/48, filling the
    /// remaining 80 bits from the node key.
    /// </remarks>
    public static IPAddress ForKey(NodePublic key)
    {
        Span<byte> a = stackalloc byte[16];
        byte[] r = key.Raw32();
        a[0] = 0xfd;
        a[1] = 0x7a;
        a[2] = 0x11;
        a[3] = 0x5c;
        a[4] = 0xa1;
        a[5] = 0xe0;
        r.AsSpan(0, 10).CopyTo(a[6..]);
        return new IPAddress(a);
    }
}

// Copyright (c) Tailscale Inc & contributors
// Copyright (c) Andrzej Ból and contributors (.NET port)
// SPDX-License-Identifier: BSD-3-Clause

using System.Net;

namespace Tailcat.Cli;

/// <summary>Where a SOCKS5 destination address should be dialed.</summary>
/// <param name="ToServer">Dial the tailcat server from the command line.</param>
/// <param name="Blob">If non-empty, the address blob hostname to dial.</param>
/// <param name="Port">The port to dial, if <paramref name="ToServer"/> or <paramref name="Blob"/> is set.</param>
/// <param name="Dst">
/// The IP:port to dial through the server as an exit node, otherwise.
/// </param>
public readonly record struct SocksTarget(
    bool ToServer = false,
    ConnBlob Blob = default,
    ushort Port = 0,
    IPEndPoint? Dst = null)
{
    /// <inheritdoc/>
    public bool Equals(SocksTarget other) =>
        ToServer == other.ToServer &&
        Blob == other.Blob &&
        Port == other.Port &&
        Equals(Dst, other.Dst);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(ToServer, Blob, Port, Dst);
}

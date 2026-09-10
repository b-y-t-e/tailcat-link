// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Net.Quic;
using System.Net.Sockets;
using Tailcat.Net;

namespace Tailcat.Link.Protocol;

/// <summary>What "the session is gone" looks like from underneath.</summary>
/// <remarks>
/// One list, because a session and the channels riding on it end together and
/// two lists would end them differently: the copy that missed
/// <see cref="TailcatException"/> let a channel reach the application as a raw
/// transport error instead of closing with
/// <see cref="ILinkChannel.Closed"/>.
/// </remarks>
internal static class SessionFailure
{
    /// <summary>
    /// Whether the failure means the session is over: QUIC, the socket, a
    /// disposed connection, a session the node took away and said why, or a
    /// timeout that has run out.
    /// </summary>
    /// <remarks>
    /// <see cref="TailcatException"/> is here rather than reaching a caller
    /// because that is what the reason would otherwise cost: a peer that
    /// re-dials replaces the session, and the request that was in flight must
    /// be repeated on the new one rather than reported as a failure.
    /// </remarks>
    public static bool EndsTheSession(Exception ex) =>
        ex is QuicException or IOException or SocketException or ObjectDisposedException
            or OperationCanceledException or InvalidOperationException or TailcatException;
}

// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

namespace Tailcat.Net;

/// <summary>
/// A write refused because the peer let go of the stream it was writing into,
/// on a transport that is not QUIC.
/// </summary>
/// <remarks>
/// What QUIC says with <c>StreamAborted</c>: the other end stopped reading, and
/// no more than that. Kept apart from the session ending, which is a different
/// thing to report to whoever was writing.
/// </remarks>
public sealed class PeerReleasedStreamException : IOException
{
    /// <summary>Creates the exception with a message saying what the peer said.</summary>
    public PeerReleasedStreamException(string message)
        : base(message)
    {
    }
}

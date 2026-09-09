// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

namespace Tailcat.Link;

/// <summary>Where a peer stands, as one value a user interface can bind to.</summary>
/// <remarks>
/// A boolean cannot say "trying", which is the state a link spends most of a
/// bad afternoon in and the one an operator most wants to see.
/// </remarks>
public enum LinkConnectionState
{
    /// <summary>Nothing has been attempted yet.</summary>
    Idle,

    /// <summary>Building the first session.</summary>
    Connecting,

    /// <summary>A session is up.</summary>
    Connected,

    /// <summary>A session was up, is not now, and another is being built.</summary>
    Reconnecting,

    /// <summary>Stopped for a reason waiting will not fix.</summary>
    Faulted,
}

/// <summary>Why a session ended, in a form worth branching on.</summary>
/// <remarks>
/// A user interface that shows a fresh invitation code on
/// <see cref="Refused"/> and nothing at all on <see cref="NetworkLost"/> is
/// the ordinary case; matching on a message to tell them apart is what this
/// exists to replace.
/// </remarks>
public enum LinkDisconnectReason
{
    /// <summary>The session stopped and the reason is not one of the others.</summary>
    Unknown,

    /// <summary>The network, the relay, or the peer's machine went away.</summary>
    NetworkLost,

    /// <summary>The other machine would not have this one.</summary>
    Refused,

    /// <summary>This end closed the link.</summary>
    Closed,

    /// <summary>Something waiting will not fix; the link has stopped for good.</summary>
    Faulted,
}

/// <summary>Why the session went down, and what said so.</summary>
/// <param name="reason">The reason, for code to branch on.</param>
/// <param name="detail">The same thing in words, for a log or a tooltip.</param>
/// <param name="error">What was thrown, if anything was.</param>
public sealed class DisconnectedEventArgs(LinkDisconnectReason reason, string detail, Exception? error = null)
    : EventArgs
{
    /// <summary>The reason, for code to branch on.</summary>
    public LinkDisconnectReason Reason { get; } = reason;

    /// <summary>The same thing in words, for a log or a tooltip.</summary>
    public string Detail { get; } = detail;

    /// <summary>What was thrown, if anything was.</summary>
    public Exception? Error { get; } = error;
}

/// <summary>Where the link now stands.</summary>
/// <param name="state">The state it has just moved into.</param>
public sealed class LinkStateChangedEventArgs(LinkConnectionState state) : EventArgs
{
    /// <summary>The state the link has just moved into.</summary>
    public LinkConnectionState State { get; } = state;
}

/// <summary>Which peer this is about.</summary>
/// <param name="peer">The peer.</param>
public class PeerEventArgs(ILinkPeer peer) : EventArgs
{
    /// <summary>The peer.</summary>
    public ILinkPeer Peer { get; } = peer;
}

/// <summary>Which peer went away, and why.</summary>
/// <param name="peer">The peer.</param>
/// <param name="reason">Why its session ended.</param>
/// <param name="detail">The same thing in words.</param>
public sealed class PeerLeftEventArgs(ILinkPeer peer, LinkDisconnectReason reason, string detail)
    : PeerEventArgs(peer)
{
    /// <summary>Why its session ended.</summary>
    public LinkDisconnectReason Reason { get; } = reason;

    /// <summary>The same thing in words.</summary>
    public string Detail { get; } = detail;
}

// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using Tailcat.Keys;

namespace Tailcat.Derp;

/// <summary>
/// Something a relay said that is not a packet: about a peer, about this
/// connection, or about itself.
/// </summary>
/// <remarks>
/// These used to be read and dropped, and they are the only account there is
/// of the failures that otherwise look like a peer gone quiet. A link that
/// came up and fell silent every fifteen seconds was, underneath, a relay
/// saying over and over that the machine at the other end had disconnected
/// from it — and nothing above the frame reader ever heard.
/// </remarks>
public abstract record DerpNotice;

/// <summary>A peer this node sent to is not connected to this relay.</summary>
/// <param name="Peer">The peer that is gone.</param>
/// <param name="Reason">Whether it left, or was never here.</param>
public sealed record DerpPeerGone(NodePublic Peer, DerpPeerGoneReason Reason) : DerpNotice;

/// <summary>The relay's view of this connection's health.</summary>
/// <param name="Problem">
/// What is wrong — for instance another client connected with the same key —
/// or empty when an earlier problem has cleared.
/// </param>
public sealed record DerpHealth(string Problem) : DerpNotice
{
    /// <summary>Whether this clears an earlier problem rather than reporting one.</summary>
    public bool IsHealthy => Problem.Length == 0;
}

/// <summary>The relay is restarting and this connection will drop.</summary>
/// <param name="ReconnectIn">How long to wait before reconnecting.</param>
/// <param name="TryFor">How long to keep trying after that.</param>
public sealed record DerpServerRestarting(TimeSpan ReconnectIn, TimeSpan TryFor) : DerpNotice;

// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

namespace Tailcat.Derp;

/// <summary>
/// How a <see cref="DerpConnection"/> checks that its connection still carries
/// bytes, and how soon it gives up on one that does not. Not a setting: every
/// connection does this; the type exists so tests can run the same logic on a
/// shorter clock.
/// </summary>
/// <remarks>
/// <para>
/// A relay connection can die without ending. A stateful firewall that loses
/// track of a TCP flow — one confused by a receive window that closed and
/// reopened is enough — drops everything on it from then on, in both
/// directions, and neither end is told. The operating system notices only when
/// its own retransmissions run out, which on Windows is twenty seconds or more,
/// and every session carried by the relay is silent for all of it.
/// </para>
/// <para>
/// So the connection asks. After it has sent something and heard nothing back
/// for <see cref="ProbeAfterSend"/>, or after <see cref="ProbeWhenIdle"/> of
/// silence regardless, it pings the relay; if no frame at all arrives within
/// <see cref="Timeout"/> of that, the connection is abandoned and a new one is
/// made — which a firewall sees as a new flow, with nothing stale about it.
/// </para>
/// </remarks>
internal sealed record DerpLiveness
{
    /// <summary>
    /// The defaults: a stalled connection is replaced within about three and a
    /// half seconds. Tight on purpose. A false alarm costs one reconnection,
    /// a fraction of a second; a slow verdict costs every session on the relay
    /// its retransmissions, and QUIC gives up on a peer after sixteen seconds
    /// without an acknowledgement — which two cuts in a row, each noticed
    /// slowly, were measured to reach.
    /// </summary>
    public static DerpLiveness Default { get; } = new();

    /// <summary>No checking: for tests that need a connection left exactly as the relay leaves it.</summary>
    public static DerpLiveness Off { get; } = new() { Enabled = false };

    /// <summary>Whether to check at all.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// How long after sending, with nothing received, before asking. Short,
    /// because a peer mid-conversation answers well inside it.
    /// </summary>
    public TimeSpan ProbeAfterSend { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>How long a connection may be silent, with nothing sent either, before asking.</summary>
    public TimeSpan ProbeWhenIdle { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>How long to wait for any frame after a ping before abandoning the connection.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>How often the checks run.</summary>
    public TimeSpan CheckInterval { get; init; } = TimeSpan.FromMilliseconds(500);
}

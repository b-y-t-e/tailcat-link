// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

namespace Tailcat.Link.Protocol;

/// <summary>What the machine at the other end of a session can take.</summary>
/// <remarks>
/// <para>
/// Carried in the answer to a ping, which is the one exchange every version of
/// this protocol already answers and whose answer every version already
/// ignores.
/// </para>
/// <para>
/// Every exchange sent is shaped by it: <see cref="Exchanges"/> gets the
/// exchange frame, <see cref="LargeFrames"/> alone gets single frames, and a
/// machine that says neither is refused anything at all.
/// </para>
/// </remarks>
[Flags]
internal enum PeerCapabilities : byte
{
    /// <summary>A machine that has said it takes nothing this build sends.</summary>
    None = 0,

    /// <summary>
    /// Takes a request or notification frame of any length, whole — what the
    /// browser client takes in place of exchanges.
    /// </summary>
    LargeFrames = 1,

    /// <summary>
    /// Speaks <see cref="LinkFrameKind.Exchange"/>: content of any size, resumed
    /// in both directions across sessions.
    /// </summary>
    Exchanges = 2,
}

/// <summary>The one byte a ping is answered with.</summary>
internal static class PeerCapabilitiesCodec
{
    /// <summary>What this build can take, and so what it says in every ping answer.</summary>
    public const PeerCapabilities ThisBuild = PeerCapabilities.LargeFrames | PeerCapabilities.Exchanges;

    /// <summary>Writes the answer a ping gets.</summary>
    public static byte[] Encode(PeerCapabilities capabilities) => [(byte)capabilities];

    /// <summary>Reads what the other machine said, or nothing.</summary>
    /// <remarks>
    /// Bits this build does not know are dropped rather than refused: a newer
    /// machine saying it can do more is not a reason to stop talking to it,
    /// and nothing here will ask it for what those bits mean.
    /// </remarks>
    public static PeerCapabilities Decode(ReadOnlySpan<byte> answer) =>
        answer.IsEmpty ? PeerCapabilities.None : (PeerCapabilities)answer[0] & ThisBuild;
}

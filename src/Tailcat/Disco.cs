// Copyright (c) Tailscale Inc & contributors
// SPDX-License-Identifier: BSD-3-Clause

using Tailcat.Keys;

namespace Tailcat;

/// <summary>
/// The "meow" handshake tailcat runs over DERP before a client is admitted.
/// </summary>
/// <remarks>
/// Meow messages are sent as raw DERP packets (not disco-framed). They are
/// identified by a 4-byte magic prefix, followed by a 1-byte message type
/// and the message payload.
/// </remarks>
public static class Disco
{
    /// <summary>
    /// The 4-byte prefix for all meow DERP packets. It's distinct from
    /// WireGuard message types (1-4) and disco's "TS💬" magic.
    /// </summary>
    public static ReadOnlySpan<byte> MeowMagic => "meow"u8;

    /// <summary>The meow ping message type (client to server).</summary>
    public const byte MeowTypePing = 0x01;

    /// <summary>The meowed acknowledgement message type (server to client).</summary>
    public const byte MeowTypePong = 0x02;

    // The length of a well-formed meow ping: magic, type, node key, disco key.
    private const int MeowPingLen = 4 + 1 + NodePublic.RawLen + DiscoPublic.RawLen;

    /// <summary>Reports whether <paramref name="pkt"/> starts with the meow magic prefix.</summary>
    public static bool IsMeowPacket(ReadOnlySpan<byte> pkt) =>
        pkt.Length >= 4 && pkt[..4].SequenceEqual(MeowMagic);

    /// <summary>
    /// Encodes a meow ping packet containing the sender's node public key and
    /// disco public key.
    /// </summary>
    public static byte[] EncodeMeowPing(NodePublic nodeKey, DiscoPublic discoKey)
    {
        List<byte> b = new(MeowPingLen);
        b.AddRange(MeowMagic);
        b.Add(MeowTypePing);
        nodeKey.AppendTo(b);
        discoKey.AppendTo(b);
        return [.. b];
    }

    /// <summary>Encodes a meowed (acknowledgment) packet.</summary>
    public static byte[] EncodeMeowed()
    {
        List<byte> b = new(4 + 1);
        b.AddRange(MeowMagic);
        b.Add(MeowTypePong);
        return [.. b];
    }

    /// <summary>
    /// Parses a meow ping packet, returning the sender's node public key and
    /// disco public key. The packet must have already been verified with
    /// <see cref="IsMeowPacket"/>.
    /// </summary>
    /// <returns>
    /// False if the packet is too short, isn't a ping, or carries a zero disco
    /// key.
    /// </returns>
    public static bool TryParseMeowPing(
        ReadOnlySpan<byte> pkt,
        out NodePublic nodeKey,
        out DiscoPublic discoKey)
    {
        (nodeKey, discoKey) = (default, default);
        if (pkt.Length < MeowPingLen)
        {
            return false;
        }
        if (pkt[4] != MeowTypePing)
        {
            return false;
        }
        DiscoPublic disco = DiscoPublic.FromRaw32(pkt.Slice(5 + NodePublic.RawLen, DiscoPublic.RawLen));

        // A meow arrives on an unauthenticated DERP packet, and a zero disco
        // key reaches endpoint advertisement, where deriving the shared secret
        // fails. Reject it here rather than there.
        if (disco.IsZero)
        {
            return false;
        }
        nodeKey = NodePublic.FromRaw32(pkt.Slice(5, NodePublic.RawLen));
        discoKey = disco;
        return true;
    }

    /// <summary>Reports whether <paramref name="pkt"/> is a meowed (acknowledgment) packet.</summary>
    public static bool IsMeowedPacket(ReadOnlySpan<byte> pkt) =>
        pkt.Length >= 5 && pkt[..4].SequenceEqual(MeowMagic) && pkt[4] == MeowTypePong;
}

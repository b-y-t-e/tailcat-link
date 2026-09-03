// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Security.Cryptography;
using Sodium;
using Tailcat.Keys;

namespace Tailcat.Net;

/// <summary>The kind of a message exchanged between two tailcat peers.</summary>
public enum PeerMessageType : byte
{
    /// <summary>Opens a session: carries our TLS fingerprint and endpoint candidates.</summary>
    Hello = 0x01,

    /// <summary>Answers a <see cref="Hello"/> with the same information.</summary>
    HelloAck = 0x02,

    /// <summary>Probes whether a path works, and how fast.</summary>
    Ping = 0x03,

    /// <summary>Answers a <see cref="Ping"/>, echoing its ID.</summary>
    Pong = 0x04,

    /// <summary>Carries an opaque datagram (a QUIC packet) for the session.</summary>
    Data = 0x05,

    /// <summary>
    /// Announces a new set of endpoint candidates, after the sender network
    /// changed underneath it.
    /// </summary>
    EndpointUpdate = 0x06,

    /// <summary>
    /// One encrypted record of a <c>relay1</c> session.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="Data"/> on purpose: that one means "an opaque
    /// datagram that is already encrypted", which the link forwards into the
    /// UDP bridge for QUIC to read. These carry their own encryption and
    /// belong to a session that has no bridge.
    /// </remarks>
    Relay1Record = 0x07,
}

/// <summary>
/// Frames the messages two tailcat peers exchange, whether over a DERP relay
/// or over a direct UDP path.
/// </summary>
/// <remarks>
/// <para>
/// Control messages (everything but <see cref="PeerMessageType.Data"/>) are
/// sealed in a NaCl box between the two nodes' keys. That is what makes the
/// relay untrusted: it routes by public key, but it holds neither private
/// key, so it can neither read a control message nor forge one. Without
/// this, a relay could hand us an attacker's TLS fingerprint or point our
/// path switch at an address of its choosing.
/// </para>
/// <para>
/// <see cref="PeerMessageType.Data"/> is sent in the clear because it carries
/// QUIC packets, which already carry their own TLS 1.3 encryption and
/// authentication end to end.
/// </para>
/// </remarks>
public static class PeerMessage
{
    /// <summary>The 4-byte prefix on every message, distinguishing it from other traffic.</summary>
    public static ReadOnlySpan<byte> Magic => "TCN1"u8;

    /// <summary>The length of the fixed header: magic plus the type byte.</summary>
    public const int HeaderLen = 4 + 1;

    /// <summary>The length of a NaCl box nonce.</summary>
    public const int NonceLen = 24;

    /// <summary>The length of the authentication tag a NaCl box adds.</summary>
    public const int MacLen = 16;

    /// <summary>
    /// How many bytes a sealed message adds to its payload: the header, the
    /// nonce, and the box's authentication tag. Used to size a padded probe
    /// to an exact number of bytes on the wire.
    /// </summary>
    public const int SealOverhead = HeaderLen + NonceLen + MacLen;

    /// <summary>Reports whether <paramref name="packet"/> carries our framing.</summary>
    public static bool IsPeerMessage(ReadOnlySpan<byte> packet) =>
        packet.Length >= HeaderLen && packet[..4].SequenceEqual(Magic);

    /// <summary>Returns the type of a message, which must be one of ours.</summary>
    public static PeerMessageType TypeOf(ReadOnlySpan<byte> packet) => (PeerMessageType)packet[4];

    /// <summary>Wraps an opaque datagram, such as a QUIC packet, for sending.</summary>
    public static byte[] EncodeData(ReadOnlySpan<byte> datagram)
    {
        byte[] msg = new byte[HeaderLen + datagram.Length];
        Magic.CopyTo(msg);
        msg[4] = (byte)PeerMessageType.Data;
        datagram.CopyTo(msg.AsSpan(HeaderLen));
        return msg;
    }

    /// <summary>Returns the datagram carried by a <see cref="PeerMessageType.Data"/> message.</summary>
    public static ReadOnlyMemory<byte> DecodeData(ReadOnlyMemory<byte> packet) => packet[HeaderLen..];

    /// <summary>
    /// Seals a control message so that only the holder of
    /// <paramref name="peer"/>'s private key can read it, and only the holder
    /// of ours could have written it.
    /// </summary>
    public static byte[] Seal(PeerMessageType type, ReadOnlySpan<byte> payload, NodePrivate self, NodePublic peer)
    {
        byte[] nonce = PublicKeyBox.GenerateNonce();
        byte[] box = PublicKeyBox.Create(payload.ToArray(), nonce, self.Raw32(), peer.Raw32());

        byte[] msg = new byte[HeaderLen + NonceLen + box.Length];
        Magic.CopyTo(msg);
        msg[4] = (byte)type;
        nonce.CopyTo(msg, HeaderLen);
        box.CopyTo(msg, HeaderLen + NonceLen);
        return msg;
    }

    /// <summary>
    /// Opens a control message sealed by <paramref name="peer"/>. It fails if
    /// the message was forged, tampered with, or sent by anyone else.
    /// </summary>
    public static bool TryOpen(
        ReadOnlySpan<byte> packet,
        NodePrivate self,
        NodePublic peer,
        out PeerMessageType type,
        [NotNullWhen(true)] out byte[]? payload)
    {
        (type, payload) = (default, null);
        if (!IsPeerMessage(packet) || packet.Length < HeaderLen + NonceLen)
        {
            return false;
        }
        type = TypeOf(packet);
        if (type == PeerMessageType.Data)
        {
            return false;
        }

        byte[] nonce = packet.Slice(HeaderLen, NonceLen).ToArray();
        byte[] box = packet[(HeaderLen + NonceLen)..].ToArray();
        try
        {
            payload = PublicKeyBox.Open(box, nonce, self.Raw32(), peer.Raw32());
            return true;
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            // A forged or corrupted message; indistinguishable, and both are
            // simply dropped.
            return false;
        }
    }
}

/// <summary>
/// How the two nodes carry a session's streams once they have found each
/// other.
/// </summary>
/// <remarks>
/// <para>
/// Which one is used is the dialling node's to ask for and the answering
/// node's to agree to, inside the sealed hello — so a relay cannot talk
/// either end down to something weaker, and a node that speaks only one of
/// them is refused rather than left waiting.
/// </para>
/// <para>
/// Values other than those named here belong to transports this build does
/// not have. They are decoded rather than rejected, because the peer asking
/// for one deserves an answer saying so.
/// </para>
/// </remarks>
public enum PeerTransport : byte
{
    /// <summary>
    /// QUIC over whichever path the link prefers, authenticated by the
    /// certificate fingerprint in the hello. What every node that can open a
    /// UDP socket uses.
    /// </summary>
    Quic = 0,

    /// <summary>
    /// Streams carried by the relay itself, encrypted end to end and never
    /// leaving it. What a node uses when it cannot have QUIC — a browser,
    /// which has no UDP socket, or Windows 10, which has no QUIC at all.
    /// Slower and more fragile than QUIC, so it is never the first choice.
    /// </summary>
    Relay1 = 1,
}

/// <summary>
/// What a peer tells the other side when opening a session: how it wants the
/// session carried, how to authenticate it, and where it might be reachable.
/// </summary>
/// <param name="SessionId">Distinguishes one session from a later one between the same nodes.</param>
/// <param name="CertificateFingerprint">
/// The SHA-256 hash of the peer's TLS certificate. Learning it over a sealed
/// box is what lets QUIC pin the certificate instead of trusting a CA.
/// </param>
/// <param name="Endpoints">
/// Addresses the peer might be reachable at directly: its local addresses and
/// whatever a STUN server said its public one is.
/// </param>
/// <param name="HomeRegionId">
/// The relay region the sender listens in. Without it the answer would go to
/// whichever region the answerer happens to be in, which is only the right
/// one when both nodes are near each other.
/// </param>
/// <param name="Ephemeral">
/// A one-session X25519 public key, present when the sender offers
/// <see cref="PeerTransport.Relay1"/>. It rides inside the sealed hello, so
/// the peer's static key vouches for it, and the traffic keys come from the
/// two ephemeral halves rather than the static ones — which is what stops a
/// static key stolen tomorrow opening a session recorded today.
/// </param>
/// <param name="Transports">
/// In a hello, every transport the dialling node can speak, best first; in
/// the answer, the single one the other node has chosen. Sending the whole
/// set rather than one wish is what lets a pair settle on the best they
/// share in one round trip, instead of the dialler guessing and retrying.
/// It is last on the wire, and an empty list — which is what a node built
/// before there was anything to negotiate sends — reads as
/// <see cref="PeerTransport.Quic"/>, the only transport such a node has.
/// </param>
public sealed record PeerHello(
    ulong SessionId,
    byte[] CertificateFingerprint,
    IReadOnlyList<IPEndPoint> Endpoints,
    int HomeRegionId = 0,
    IReadOnlyList<PeerTransport>? Transports = null,
    byte[]? Ephemeral = null)
{
    /// <summary>What an empty list on the wire means.</summary>
    public static readonly IReadOnlyList<PeerTransport> DefaultTransports = [PeerTransport.Quic];

    /// <summary>The length of a certificate fingerprint (SHA-256).</summary>
    public const int FingerprintLen = 32;

    /// <summary>The length of the ephemeral key a relay1 hello carries.</summary>
    public const int EphemeralLen = 32;

    // A peer offering more than this is not negotiating, it is asking us to
    // read a list for its own sake.
    private const int MaxTransports = 16;

    /// <summary>
    /// Every transport the sender offers, best first, never empty.
    /// </summary>
    public IReadOnlyList<PeerTransport> Transports { get; init; } =
        Transports is { Count: > 0 } offered ? offered : DefaultTransports;

    // The most endpoints we will encode or accept. A peer listing hundreds of
    // candidates would just be asking us to probe on its behalf.
    private const int MaxEndpoints = 32;

    /// <summary>Encodes the hello as the payload of a sealed control message.</summary>
    public byte[] Encode()
    {
        if (CertificateFingerprint.Length != FingerprintLen)
        {
            throw new InvalidOperationException($"fingerprint must be {FingerprintLen} bytes");
        }

        List<byte> buf = [];
        Span<byte> u64 = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(u64, SessionId);
        buf.AddRange(u64);
        buf.AddRange(CertificateFingerprint);

        Span<byte> region = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(region, HomeRegionId);
        buf.AddRange(region);

        int count = Math.Min(Endpoints.Count, MaxEndpoints);
        buf.Add((byte)count);
        Span<byte> port = stackalloc byte[2];
        for (int i = 0; i < count; i++)
        {
            IPEndPoint ep = Endpoints[i];
            byte[] addr = ep.Address.GetAddressBytes();
            buf.Add((byte)addr.Length);
            buf.AddRange(addr);
            BinaryPrimitives.WriteUInt16BigEndian(port, (ushort)ep.Port);
            buf.AddRange(port);
        }

        int transports = Math.Min(Transports.Count, MaxTransports);
        buf.Add((byte)transports);
        for (int i = 0; i < transports; i++)
        {
            buf.Add((byte)Transports[i]);
        }

        // Last, and only when it means something: a hello that offers no
        // relay1 has no session keys to agree on.
        if (Ephemeral is { Length: EphemeralLen })
        {
            buf.AddRange(Ephemeral);
        }
        return [.. buf];
    }

    /// <summary>Decodes a hello from the payload of a sealed control message.</summary>
    public static bool TryDecode(ReadOnlySpan<byte> payload, [NotNullWhen(true)] out PeerHello? hello)
    {
        hello = null;
        if (payload.Length < 8 + FingerprintLen + 4 + 1)
        {
            return false;
        }
        ulong sessionId = BinaryPrimitives.ReadUInt64BigEndian(payload);
        byte[] fingerprint = payload.Slice(8, FingerprintLen).ToArray();
        int homeRegionId = BinaryPrimitives.ReadInt32BigEndian(payload[(8 + FingerprintLen)..]);

        ReadOnlySpan<byte> rest = payload[(8 + FingerprintLen + 4)..];
        int count = rest[0];
        if (count > MaxEndpoints)
        {
            return false;
        }
        rest = rest[1..];

        List<IPEndPoint> endpoints = new(count);
        for (int i = 0; i < count; i++)
        {
            if (rest.Length < 1)
            {
                return false;
            }
            int addrLen = rest[0];
            if (addrLen is not (4 or 16) || rest.Length < 1 + addrLen + 2)
            {
                return false;
            }
            IPAddress address = new(rest.Slice(1, addrLen));
            ushort port = BinaryPrimitives.ReadUInt16BigEndian(rest[(1 + addrLen)..]);
            endpoints.Add(new IPEndPoint(address, port));
            rest = rest[(1 + addrLen + 2)..];
        }

        // Absent from a peer that predates the negotiation, and the default is
        // what such a peer means.
        List<PeerTransport> transports = [];
        if (rest.Length >= 1)
        {
            int offered = rest[0];
            if (offered > MaxTransports || rest.Length < 1 + offered)
            {
                return false;
            }
            for (int i = 0; i < offered; i++)
            {
                transports.Add((PeerTransport)rest[1 + i]);
            }
        }

        byte[]? ephemeral = null;
        int consumed = transports.Count == 0 ? 0 : 1 + transports.Count;
        if (rest.Length >= consumed + EphemeralLen)
        {
            ephemeral = rest.Slice(consumed, EphemeralLen).ToArray();
        }

        hello = new PeerHello(sessionId, fingerprint, endpoints, homeRegionId, transports, ephemeral);
        return true;
    }
}

/// <summary>A path probe, or its answer.</summary>
/// <param name="Id">Identifies the probe so an answer can be matched to it.</param>
/// <param name="SessionId">The session the probe belongs to.</param>
public readonly record struct PeerPing(ulong Id, ulong SessionId)
{
    /// <summary>The encoded length of a ping.</summary>
    public const int EncodedLen = 16;

    /// <summary>Encodes the ping as the payload of a sealed control message.</summary>
    public byte[] Encode()
    {
        byte[] buf = new byte[EncodedLen];
        BinaryPrimitives.WriteUInt64BigEndian(buf, Id);
        BinaryPrimitives.WriteUInt64BigEndian(buf.AsSpan(8), SessionId);
        return buf;
    }

    /// <summary>Decodes a ping from the payload of a sealed control message.</summary>
    public static bool TryDecode(ReadOnlySpan<byte> payload, out PeerPing ping)
    {
        if (payload.Length < EncodedLen)
        {
            ping = default;
            return false;
        }
        ping = new PeerPing(
            BinaryPrimitives.ReadUInt64BigEndian(payload),
            BinaryPrimitives.ReadUInt64BigEndian(payload[8..]));
        return true;
    }
}

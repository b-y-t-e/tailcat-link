// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Buffers.Binary;
using System.Security.Cryptography;
using Org.BouncyCastle.Crypto.Agreement;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;
using Tailcat.Keys;

namespace Tailcat.Net.Relay1;

/// <summary>
/// The ephemeral half of a <c>relay1</c> handshake: a key that exists for one
/// session and is thrown away with it.
/// </summary>
/// <remarks>
/// It travels inside the sealed hello, so the peer's static key is what
/// vouches for it. Deriving the session keys from the two ephemeral halves
/// rather than from the static ones is what stops a static key stolen
/// tomorrow from opening a session recorded today.
/// </remarks>
internal sealed class Relay1Ephemeral
{
    /// <summary>The length of an X25519 public key.</summary>
    public const int KeyLen = 32;

    private readonly X25519PrivateKeyParameters _private;

    public Relay1Ephemeral()
    {
        _private = new X25519PrivateKeyParameters(new SecureRandom());
        PublicKey = _private.GeneratePublicKey().GetEncoded();
    }

    /// <summary>The half that goes on the wire.</summary>
    public byte[] PublicKey { get; }

    /// <summary>
    /// Completes the exchange, producing one key per direction.
    /// </summary>
    /// <param name="peerPublic">The peer's ephemeral public key.</param>
    /// <param name="sessionId">Binds the keys to this session and no other.</param>
    /// <param name="dialer">The static key of the end that dialled.</param>
    /// <param name="host">The static key of the end that was dialled.</param>
    /// <exception cref="TailcatException">If the peer's key is not a usable X25519 key.</exception>
    public Relay1Keys Derive(ReadOnlySpan<byte> peerPublic, ulong sessionId, NodePublic dialer, NodePublic host)
    {
        if (peerPublic.Length != KeyLen)
        {
            throw new TailcatException($"a relay1 hello must carry a {KeyLen}-byte key, this one has {peerPublic.Length}");
        }

        byte[] shared = new byte[KeyLen];
        try
        {
            X25519Agreement agreement = new();
            agreement.Init(_private);
            agreement.CalculateAgreement(new X25519PublicKeyParameters(peerPublic.ToArray()), shared, 0);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            throw new TailcatException("the peer's relay1 key is not a usable X25519 key");
        }

        try
        {
            return Schedule(shared, sessionId, dialer, host);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(shared);
        }
    }

    /// <summary>
    /// The schedule alone: a shared secret in, one traffic key per direction out.
    /// </summary>
    /// <remarks>
    /// Separate from the exchange above because the two answer to different
    /// things — the exchange to X25519, this to the salt and the labels the
    /// other implementation must use as well. It is also the only way to hold
    /// both implementations to those from a fixed secret, which no test can do
    /// through an ephemeral key it did not choose.
    /// </remarks>
    public static Relay1Keys Schedule(ReadOnlySpan<byte> shared, ulong sessionId, NodePublic dialer, NodePublic host)
    {
        // The salt names who is talking to whom and which session, so the same
        // pair of ephemeral keys could not produce the same traffic keys twice.
        byte[] salt = new byte[8 + (NodePublic.RawLen * 2)];
        BinaryPrimitives.WriteUInt64BigEndian(salt, sessionId);
        dialer.Raw32().CopyTo(salt.AsSpan(8));
        host.Raw32().CopyTo(salt.AsSpan(8 + NodePublic.RawLen));

        byte[] prk = new byte[SHA256.HashSizeInBytes];
        HKDF.Extract(HashAlgorithmName.SHA256, shared, salt, prk);

        byte[] dialerToHost = HKDF.Expand(HashAlgorithmName.SHA256, prk, Relay1Record.KeyLen, "tailcat relay1 v1 d2h"u8.ToArray());
        byte[] hostToDialer = HKDF.Expand(HashAlgorithmName.SHA256, prk, Relay1Record.KeyLen, "tailcat relay1 v1 h2d"u8.ToArray());
        CryptographicOperations.ZeroMemory(prk);

        return new Relay1Keys(dialerToHost, hostToDialer);
    }
}

/// <summary>One traffic key per direction, so the two ends can never collide on a nonce.</summary>
internal sealed record Relay1Keys(byte[] DialerToHost, byte[] HostToDialer);

/// <summary>
/// One encrypted record: a counter in the clear and everything else sealed.
/// </summary>
/// <remarks>
/// <para>
/// Records ride in their own peer message type rather than in
/// <see cref="PeerMessageType.Data"/>, which means "an opaque datagram that
/// is already encrypted" and is forwarded into the UDP bridge.
/// </para>
/// <para>
/// The counter must arrive strictly in sequence. A gap means the relay
/// dropped a record — DERP drops for a receiver that has fallen behind — and
/// there is no retransmission here to recover the stream it belonged to, so
/// the session ends rather than carrying on with a hole in it.
/// </para>
/// </remarks>
internal static class Relay1Record
{
    /// <summary>The length of an AES-256 key.</summary>
    public const int KeyLen = 32;

    /// <summary>The length of the GCM authentication tag.</summary>
    public const int TagLen = 16;

    /// <summary>Where the counter sits, after the peer-message header.</summary>
    public const int CounterOffset = PeerMessage.HeaderLen;

    /// <summary>The length of the counter.</summary>
    public const int CounterLen = 8;

    /// <summary>Everything a record adds to its plaintext.</summary>
    public const int Overhead = CounterOffset + CounterLen + TagLen;

    /// <summary>
    /// The most plaintext one record may carry.
    /// </summary>
    /// <remarks>
    /// Sized against 32 KiB, not DERP's 64 KiB packet limit: a relay reached
    /// over a WebSocket — which is the only way a browser can reach one —
    /// closes a client that sends a message larger than 32768 bytes, with
    /// "read limited at 32769 bytes". The limit is the same on both
    /// transports because either end of a session may be a browser, and half
    /// a record's worth of throughput is not worth a second size to reason
    /// about. What is left over covers the DERP frame header, the destination
    /// key, this record's own header and counter, and the GCM tag.
    /// </remarks>
    public const int MaxPlaintext = 32256;

    /// <summary>Seals <paramref name="plaintext"/> as record <paramref name="counter"/>.</summary>
    public static byte[] Seal(ReadOnlySpan<byte> plaintext, byte[] key, ulong counter)
    {
        byte[] record = new byte[Overhead + plaintext.Length];
        PeerMessage.Magic.CopyTo(record);
        record[4] = (byte)PeerMessageType.Relay1Record;
        BinaryPrimitives.WriteUInt64BigEndian(record.AsSpan(CounterOffset), counter);

        using AesGcm aes = new(key, TagLen);
        Span<byte> body = record.AsSpan(CounterOffset + CounterLen);
        aes.Encrypt(NonceFor(counter), plaintext, body[..plaintext.Length], body[plaintext.Length..]);
        return record;
    }

    /// <summary>Opens a record, returning false if it is not ours or does not authenticate.</summary>
    public static bool TryOpen(ReadOnlySpan<byte> record, byte[] key, out ulong counter, out byte[] plaintext)
    {
        counter = 0;
        plaintext = [];
        if (record.Length < Overhead)
        {
            return false;
        }

        counter = BinaryPrimitives.ReadUInt64BigEndian(record[CounterOffset..]);
        ReadOnlySpan<byte> body = record[(CounterOffset + CounterLen)..];
        byte[] opened = new byte[body.Length - TagLen];
        try
        {
            using AesGcm aes = new(key, TagLen);
            aes.Decrypt(NonceFor(counter), body[..opened.Length], body[opened.Length..], opened);
        }
        catch (CryptographicException)
        {
            return false;
        }

        plaintext = opened;
        return true;
    }

    // Four zero bytes and the counter. The counter never repeats under one
    // key, which is the whole requirement GCM places on a nonce.
    private static byte[] NonceFor(ulong counter)
    {
        byte[] nonce = new byte[12];
        BinaryPrimitives.WriteUInt64BigEndian(nonce.AsSpan(4), counter);
        return nonce;
    }
}

/// <summary>What a frame inside a record is doing to its stream.</summary>
[Flags]
internal enum Relay1FrameFlags : byte
{
    /// <summary>Payload is stream data.</summary>
    None = 0,

    /// <summary>No more data on this stream from this sender.</summary>
    Fin = 1,

    /// <summary>The sender abandoned the stream; the payload says why.</summary>
    Reset = 2,

    /// <summary>The payload is a credit figure, not data.</summary>
    Window = 4,
}

/// <summary>
/// One frame per record: a stream id, what it is doing, and the rest.
/// </summary>
/// <remarks>
/// There is no length field because the record already ends where the frame
/// does. A stream is opened by the first frame naming it — ids opened by the
/// dialler are odd and by the host even, so neither end has to ask before
/// opening one and they cannot collide.
/// </remarks>
internal static class Relay1Frame
{
    /// <summary>Writes a frame into a buffer sized by <see cref="HeaderLength"/>.</summary>
    public static byte[] Encode(ulong streamId, Relay1FrameFlags flags, ReadOnlySpan<byte> payload)
    {
        int header = HeaderLength(streamId);
        byte[] frame = new byte[header + payload.Length];
        int written = WriteVarint(frame, streamId);
        frame[written] = (byte)flags;
        payload.CopyTo(frame.AsSpan(header));
        return frame;
    }

    /// <summary>How many bytes a frame for <paramref name="streamId"/> spends before its payload.</summary>
    public static int HeaderLength(ulong streamId) => VarintLength(streamId) + 1;

    /// <summary>Reads a frame, returning false if it is malformed.</summary>
    public static bool TryDecode(
        ReadOnlyMemory<byte> frame,
        out ulong streamId,
        out Relay1FrameFlags flags,
        out ReadOnlyMemory<byte> payload)
    {
        streamId = 0;
        flags = Relay1FrameFlags.None;
        payload = default;

        if (!TryReadVarint(frame.Span, out streamId, out int read) || frame.Length < read + 1)
        {
            return false;
        }
        flags = (Relay1FrameFlags)frame.Span[read];
        payload = frame[(read + 1)..];
        return true;
    }

    private static int VarintLength(ulong value)
    {
        int length = 1;
        while (value >= 0x80)
        {
            value >>= 7;
            length++;
        }
        return length;
    }

    private static int WriteVarint(Span<byte> destination, ulong value)
    {
        int i = 0;
        while (value >= 0x80)
        {
            destination[i++] = (byte)(value | 0x80);
            value >>= 7;
        }
        destination[i++] = (byte)value;
        return i;
    }

    private static bool TryReadVarint(ReadOnlySpan<byte> source, out ulong value, out int read)
    {
        value = 0;
        read = 0;
        int shift = 0;
        while (read < source.Length)
        {
            byte b = source[read++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return true;
            }
            shift += 7;
            if (shift > 63)
            {
                return false;
            }
        }
        return false;
    }
}

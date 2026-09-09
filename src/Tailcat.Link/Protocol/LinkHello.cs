// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Buffers.Binary;
using System.Text;

namespace Tailcat.Link.Protocol;

/// <summary>
/// What the machine that dialled says about itself: which invitation it
/// holds, and what to call it.
/// </summary>
/// <remarks>
/// <para>
/// Version 1 of this protocol put the pairing token on the wire on its own,
/// with no envelope around it. That shape is still read, so a client built
/// against the older library — the browser client among them — still pairs;
/// what it cannot do is name itself.
/// </para>
/// <para>
/// <see cref="DisplayName"/> is a hint and nothing more. It comes from the
/// other machine, which may say anything, and the only thing authenticated on
/// a session is the peer's key.
/// </para>
/// </remarks>
/// <param name="PairingToken">The secret out of the invitation code.</param>
/// <param name="DisplayName">What the joining machine calls itself, if anything.</param>
internal readonly record struct LinkHello(string PairingToken, string? DisplayName)
{
    /// <summary>
    /// The first byte of the versioned shape. It is outside the base64url
    /// alphabet a pairing token is written in, which is what lets a bare
    /// token be told apart from an envelope without a flag anywhere.
    /// </summary>
    private const byte Version2 = 0x02;

    /// <summary>
    /// The most a display name may be. It is shown in a list of devices, not
    /// stored as a document, and a peer must not be able to make a host write
    /// megabytes to disk by naming itself.
    /// </summary>
    public const int MaxDisplayNameBytes = 256;

    /// <summary>Writes the hello for the frame that opens a session.</summary>
    /// <exception cref="LinkException">If the display name is over its limit.</exception>
    public byte[] Encode()
    {
        byte[] token = Encoding.UTF8.GetBytes(PairingToken);
        byte[] name = DisplayName is null ? [] : Encoding.UTF8.GetBytes(DisplayName);
        if (name.Length > MaxDisplayNameBytes)
        {
            throw new LinkException(
                $"a display name may be at most {MaxDisplayNameBytes} bytes, this one is {name.Length}");
        }

        byte[] encoded = new byte[1 + 2 + token.Length + 2 + name.Length];
        encoded[0] = Version2;
        Span<byte> at = encoded.AsSpan(1);
        at = WriteBytes(at, token);
        WriteBytes(at, name);
        return encoded;
    }

    /// <summary>Reads what the other machine sent, in either shape.</summary>
    /// <exception cref="LinkException">If it is not a hello at all.</exception>
    public static LinkHello Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length == 0 || payload[0] != Version2)
        {
            // A bare pairing token: everything written before this envelope
            // existed, and everything that was not rebuilt since.
            return new LinkHello(Encoding.UTF8.GetString(payload), null);
        }

        ReadOnlySpan<byte> at = payload[1..];
        string token = Encoding.UTF8.GetString(ReadBytes(ref at));
        ReadOnlySpan<byte> name = ReadBytes(ref at);
        if (name.Length > MaxDisplayNameBytes)
        {
            throw new LinkException($"the peer named itself in {name.Length} bytes; the limit is {MaxDisplayNameBytes}");
        }
        return new LinkHello(token, name.Length == 0 ? null : Encoding.UTF8.GetString(name));
    }

    private static Span<byte> WriteBytes(Span<byte> at, ReadOnlySpan<byte> value)
    {
        BinaryPrimitives.WriteUInt16BigEndian(at, (ushort)value.Length);
        value.CopyTo(at[2..]);
        return at[(2 + value.Length)..];
    }

    private static ReadOnlySpan<byte> ReadBytes(ref ReadOnlySpan<byte> at)
    {
        if (at.Length < 2)
        {
            throw new LinkException("the peer's hello ended mid-field");
        }
        int length = BinaryPrimitives.ReadUInt16BigEndian(at);
        if (at.Length < 2 + length)
        {
            throw new LinkException("the peer's hello ended mid-field");
        }
        ReadOnlySpan<byte> value = at.Slice(2, length);
        at = at[(2 + length)..];
        return value;
    }
}

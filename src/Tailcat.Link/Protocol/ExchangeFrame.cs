// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Buffers.Binary;
using System.Text;

namespace Tailcat.Link.Protocol;

/// <summary>What an exchange asks of the machine that receives it.</summary>
[Flags]
internal enum ExchangeFlags : byte
{
    /// <summary>A notification that is acknowledged once its handler has finished.</summary>
    None = 0,

    /// <summary>A request: the handler's answer comes back as content of its own.</summary>
    Answer = 1,

    /// <summary>For the transfer handler rather than the request handler.</summary>
    Transfer = 2,

    /// <summary>
    /// The content follows the header straight away instead of waiting to be
    /// told where to start. Only on a first attempt with content small enough
    /// to fit one block, where the answer would always have been zero, and it
    /// saves the round trip that answer costs.
    /// </summary>
    Pipelined = 4,

    /// <summary>
    /// Acknowledged once the content has arrived rather than once the handler
    /// has dealt with it — a notification, whose sender is waiting for nothing
    /// the handler does.
    /// </summary>
    AckOnDelivery = 8,
}

/// <summary>What opens an exchange.</summary>
/// <param name="Flags">What is being asked for.</param>
/// <param name="Length">How long the content is, when the sender knows.</param>
/// <param name="AnswerOffset">
/// How much of the answer the sender already has, so that an answer broken by
/// a session dying carries on rather than starting again. Zero the first time.
/// </param>
/// <param name="Name">What the sender calls the content. Not a path.</param>
/// <param name="ContentType">The media type the sender declares, or empty.</param>
/// <param name="Metadata">Whatever the application attached.</param>
internal readonly record struct ExchangeHeader(
    ExchangeFlags Flags,
    long? Length,
    long AnswerOffset,
    string Name,
    string ContentType,
    ReadOnlyMemory<byte> Metadata);

/// <summary>What comes in front of an answer.</summary>
/// <param name="Length">How long the answer is, when the handler knew.</param>
/// <param name="ContentType">The media type the handler declared, or empty.</param>
/// <param name="Metadata">Whatever the handler attached.</param>
internal readonly record struct AnswerHeader(long? Length, string ContentType, ReadOnlyMemory<byte> Metadata);

/// <summary>
/// The two headers of an exchange. The content and the answer follow them as
/// <see cref="TransferFrame"/> blocks; <c>docs/exchanges.md</c> is the
/// specification.
/// </summary>
/// <remarks>
/// Every field is length-prefixed with 32 bits. The transfer offer this
/// replaces used 16 for its name and type and capped its metadata, and each of
/// those was a limit on what an application could say about its content rather
/// than something the protocol needed.
/// </remarks>
internal static class ExchangeFrame
{
    private const byte Version = 1;

    private const ExchangeFlags KnownFlags =
        ExchangeFlags.Answer | ExchangeFlags.Transfer | ExchangeFlags.Pipelined | ExchangeFlags.AckOnDelivery;

    /// <summary>Encodes the header that opens an exchange.</summary>
    public static byte[] EncodeHeader(ExchangeHeader header)
    {
        if (header.Length is < 0)
        {
            throw new LinkException($"content cannot be {header.Length} bytes long");
        }
        byte[] name = Encoding.UTF8.GetBytes(header.Name);
        byte[] contentType = Encoding.UTF8.GetBytes(header.ContentType);

        byte[] encoded = new byte[1 + 1 + 8 + 8 + 4 + name.Length + 4 + contentType.Length + 4 + header.Metadata.Length];
        Span<byte> at = encoded;
        at[0] = Version;
        at[1] = (byte)header.Flags;
        at = at[2..];
        // -1 rather than a flag: a length nobody could send is the clearest way
        // to say "unknown", and it keeps the header one shape.
        BinaryPrimitives.WriteInt64BigEndian(at, header.Length ?? -1);
        BinaryPrimitives.WriteInt64BigEndian(at[8..], header.AnswerOffset);
        at = at[16..];
        at = WriteField(at, name);
        at = WriteField(at, contentType);
        WriteField(at, header.Metadata.Span);
        return encoded;
    }

    /// <summary>Reads back what <see cref="EncodeHeader"/> wrote.</summary>
    /// <exception cref="LinkException">If the peer sent something this cannot be.</exception>
    public static ExchangeHeader DecodeHeader(ReadOnlySpan<byte> payload)
    {
        try
        {
            ExpectVersion(payload, "an exchange");
            ExchangeFlags flags = (ExchangeFlags)payload[1];
            if ((flags & ~KnownFlags) != 0)
            {
                // Refused rather than ignored: a flag is an instruction, and
                // carrying on without following it would do something other
                // than what the sender asked for.
                throw new LinkException($"the peer asked for exchange flags 0x{(byte)flags:X2}, which this does not know");
            }

            ReadOnlySpan<byte> at = payload[2..];
            long length = BinaryPrimitives.ReadInt64BigEndian(at);
            long answerOffset = BinaryPrimitives.ReadInt64BigEndian(at[8..]);
            if (answerOffset < 0)
            {
                throw new LinkException($"the peer says it has {answerOffset} bytes of the answer");
            }
            at = at[16..];
            at = ReadField(at, out ReadOnlySpan<byte> name);
            at = ReadField(at, out ReadOnlySpan<byte> contentType);
            ReadField(at, out ReadOnlySpan<byte> metadata);

            return new ExchangeHeader(
                flags,
                length < 0 ? null : length,
                answerOffset,
                Encoding.UTF8.GetString(name),
                Encoding.UTF8.GetString(contentType),
                metadata.ToArray());
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            throw new LinkException("the peer's exchange header stopped in the middle", ex);
        }
    }

    /// <summary>Encodes the header in front of an answer.</summary>
    public static byte[] EncodeAnswer(AnswerHeader header)
    {
        if (header.Length is < 0)
        {
            throw new LinkException($"an answer cannot be {header.Length} bytes long");
        }
        byte[] contentType = Encoding.UTF8.GetBytes(header.ContentType);
        byte[] encoded = new byte[1 + 8 + 4 + contentType.Length + 4 + header.Metadata.Length];
        Span<byte> at = encoded;
        at[0] = Version;
        BinaryPrimitives.WriteInt64BigEndian(at[1..], header.Length ?? -1);
        at = at[9..];
        at = WriteField(at, contentType);
        WriteField(at, header.Metadata.Span);
        return encoded;
    }

    /// <summary>Reads back what <see cref="EncodeAnswer"/> wrote.</summary>
    /// <exception cref="LinkException">If the peer sent something this cannot be.</exception>
    public static AnswerHeader DecodeAnswer(ReadOnlySpan<byte> payload)
    {
        try
        {
            ExpectVersion(payload, "an answer");
            long length = BinaryPrimitives.ReadInt64BigEndian(payload[1..]);
            ReadOnlySpan<byte> at = payload[9..];
            at = ReadField(at, out ReadOnlySpan<byte> contentType);
            ReadField(at, out ReadOnlySpan<byte> metadata);
            return new AnswerHeader(length < 0 ? null : length, Encoding.UTF8.GetString(contentType), metadata.ToArray());
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            throw new LinkException("the peer's answer header stopped in the middle", ex);
        }
    }

    private static void ExpectVersion(ReadOnlySpan<byte> payload, string what)
    {
        if (payload[0] != Version)
        {
            throw new LinkException($"the peer sent {what} in version {payload[0]}, which this does not speak");
        }
    }

    private static Span<byte> WriteField(Span<byte> destination, ReadOnlySpan<byte> value)
    {
        BinaryPrimitives.WriteInt32BigEndian(destination, value.Length);
        value.CopyTo(destination[4..]);
        return destination[(4 + value.Length)..];
    }

    private static ReadOnlySpan<byte> ReadField(ReadOnlySpan<byte> source, out ReadOnlySpan<byte> value)
    {
        int length = BinaryPrimitives.ReadInt32BigEndian(source);
        if (length < 0 || length > source.Length - 4)
        {
            throw new LinkException($"the peer announced a {(uint)length}-byte field with {source.Length - 4} left");
        }
        value = source.Slice(4, length);
        return source[(4 + length)..];
    }
}

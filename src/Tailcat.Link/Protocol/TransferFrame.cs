// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Buffers.Binary;
using System.Text;

namespace Tailcat.Link.Protocol;

/// <summary>
/// The wire format of a transfer: an offer, an offset to start from, and then
/// the content as a run of length-prefixed blocks.
/// </summary>
/// <remarks>
/// <para>
/// A transfer does not fit in a <see cref="LinkFrame"/> and is not meant to:
/// a frame is a message both machines hold in memory at once, and the point
/// of a transfer is that neither does. So only the offer travels as a frame —
/// the one that opens the stream, which is why the serving loop recognises a
/// transfer with the same dispatch it uses for a request — and the content
/// follows it on the same stream as blocks.
/// </para>
/// <para>
/// The blocks are what make a transfer resumable and observable. Each one is
/// a point at which the receiver knows exactly how much it has, so a session
/// that dies mid-transfer leaves an offset rather than a ruin; each one is
/// also a sign of life, which is how a transfer that takes an hour is told
/// apart from a peer that stopped. A zero-length block ends the content, so a
/// stream that simply stops is a truncation and not an end.
/// </para>
/// </remarks>
internal static class TransferFrame
{
    /// <summary>How much content one block carries.</summary>
    /// <remarks>
    /// A quarter of a megabyte, which is <c>Relay1Stream</c>'s whole initial
    /// window: larger blocks would only wait for a window update in the
    /// middle of themselves, and smaller ones would spend more of the relay's
    /// records on headers.
    /// </remarks>
    public const int BlockBytes = 256 * 1024;

    /// <summary>The length prefix in front of every block.</summary>
    public const int BlockHeaderLength = 4;

    /// <summary>The most application metadata an offer may carry.</summary>
    /// <remarks>
    /// It travels in the frame that opens the transfer, so it is bounded by
    /// what is reasonable to hold twice over, not by what a frame could take.
    /// </remarks>
    public const int MaxMetadataBytes = 64 * 1024;

    /// <summary>The version byte every offer starts with.</summary>
    private const byte Version = 1;

    /// <summary>The most an offer's name or content type may be.</summary>
    private const int MaxTextBytes = 1024;

    /// <summary>Encodes an offer for the frame that opens a transfer.</summary>
    /// <exception cref="LinkException">If a field is over its limit.</exception>
    public static byte[] EncodeOffer(TransferOffer offer)
    {
        byte[] name = Encoding.UTF8.GetBytes(offer.Name);
        byte[] contentType = Encoding.UTF8.GetBytes(offer.ContentType);
        Check(name.Length, MaxTextBytes, "name");
        Check(contentType.Length, MaxTextBytes, "content type");
        Check(offer.Metadata.Length, MaxMetadataBytes, "metadata");
        if (offer.Length is < 0)
        {
            throw new LinkException($"a transfer cannot be {offer.Length} bytes long");
        }

        byte[] encoded = new byte[1 + 8 + 2 + name.Length + 2 + contentType.Length + 4 + offer.Metadata.Length];
        Span<byte> at = encoded;
        at[0] = Version;
        at = at[1..];
        // -1 rather than a flag byte: a length nobody could send is the
        // clearest way to say "unknown", and it keeps the header one shape.
        BinaryPrimitives.WriteInt64BigEndian(at, offer.Length ?? -1);
        at = at[8..];
        at = WriteBytes(at, name);
        at = WriteBytes(at, contentType);
        BinaryPrimitives.WriteInt32BigEndian(at, offer.Metadata.Length);
        offer.Metadata.Span.CopyTo(at[4..]);
        return encoded;
    }

    /// <summary>Reads back what <see cref="EncodeOffer"/> wrote.</summary>
    /// <exception cref="LinkException">If the peer sent something this cannot be.</exception>
    public static TransferOffer DecodeOffer(ReadOnlySpan<byte> payload)
    {
        try
        {
            if (payload[0] != Version)
            {
                throw new LinkException(
                    $"the peer offered a transfer in version {payload[0]}, which this does not speak");
            }
            long length = BinaryPrimitives.ReadInt64BigEndian(payload[1..]);
            ReadOnlySpan<byte> at = payload[9..];
            at = ReadBytes(at, MaxTextBytes, out ReadOnlySpan<byte> name);
            at = ReadBytes(at, MaxTextBytes, out ReadOnlySpan<byte> contentType);

            int metadataLength = BinaryPrimitives.ReadInt32BigEndian(at);
            if (metadataLength < 0 || metadataLength > MaxMetadataBytes || metadataLength > at.Length - 4)
            {
                throw new LinkException($"the peer announced {metadataLength} bytes of transfer metadata");
            }
            return new TransferOffer
            {
                Name = Encoding.UTF8.GetString(name),
                ContentType = Encoding.UTF8.GetString(contentType),
                Length = length < 0 ? null : length,
                Metadata = at.Slice(4, metadataLength).ToArray(),
            };
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            throw new LinkException("the peer's transfer offer stopped in the middle", ex);
        }
    }

    /// <summary>Encodes the offset the receiver wants the content to start at.</summary>
    public static byte[] EncodeOffset(long offset)
    {
        byte[] encoded = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(encoded, offset);
        return encoded;
    }

    /// <summary>Reads back an offset, refusing one that cannot be honoured.</summary>
    /// <exception cref="LinkException">If the peer asked to start somewhere impossible.</exception>
    public static long DecodeOffset(ReadOnlySpan<byte> payload, long? length)
    {
        if (payload.Length < 8)
        {
            throw new LinkException("the peer took the transfer without saying where to start");
        }
        long offset = BinaryPrimitives.ReadInt64BigEndian(payload);
        if (offset < 0 || offset > (length ?? long.MaxValue))
        {
            throw new LinkException($"the peer asked the transfer to start at byte {offset}");
        }
        return offset;
    }

    /// <summary>Writes the length in front of a block.</summary>
    public static void WriteBlockHeader(Span<byte> destination, int length) =>
        BinaryPrimitives.WriteInt32BigEndian(destination, length);

    /// <summary>
    /// Reads the length of the next block, or zero at the end of the content.
    /// </summary>
    /// <exception cref="EndOfStreamException">If the peer stopped mid-transfer.</exception>
    /// <exception cref="LinkException">If the peer announced an impossible block.</exception>
    public static async Task<int> ReadBlockHeaderAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] header = new byte[BlockHeaderLength];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);

        int length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length < 0 || length > BlockBytes)
        {
            throw new LinkException($"the peer announced a {length}-byte block; the limit is {BlockBytes}");
        }
        return length;
    }

    private static void Check(int length, int limit, string what)
    {
        if (length > limit)
        {
            throw new LinkException($"a transfer's {what} may be at most {limit} bytes, this one is {length}");
        }
    }

    private static Span<byte> WriteBytes(Span<byte> destination, ReadOnlySpan<byte> value)
    {
        BinaryPrimitives.WriteUInt16BigEndian(destination, (ushort)value.Length);
        value.CopyTo(destination[2..]);
        return destination[(2 + value.Length)..];
    }

    private static ReadOnlySpan<byte> ReadBytes(ReadOnlySpan<byte> source, int limit, out ReadOnlySpan<byte> value)
    {
        int length = BinaryPrimitives.ReadUInt16BigEndian(source);
        if (length > limit || length > source.Length - 2)
        {
            throw new LinkException($"the peer announced a {length}-byte field in a transfer offer");
        }
        value = source.Slice(2, length);
        return source[(2 + length)..];
    }
}

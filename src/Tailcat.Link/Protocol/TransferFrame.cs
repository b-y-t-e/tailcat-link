// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Buffers.Binary;

namespace Tailcat.Link.Protocol;

/// <summary>
/// How an exchange's content moves: an offset to start from, and then the
/// content as a run of length-prefixed blocks.
/// </summary>
/// <remarks>
/// <para>
/// Content does not travel in a <see cref="LinkFrame"/>: a frame is held in
/// memory whole, and the point of content is that neither machine holds it.
/// So only the header travels as a frame, and the content follows it on the
/// same stream as blocks.
/// </para>
/// <para>
/// The blocks are what make an exchange resumable and observable. Each one is
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

    /// <summary>
    /// Writes the content from wherever <paramref name="transfer"/> now is,
    /// and the block that ends it.
    /// </summary>
    /// <remarks>
    /// Nothing is held in memory: one block at a time is read from the content
    /// and written to the stream, and the transport's own flow control is what
    /// keeps a fast disk from running ahead of a slow relay.
    /// </remarks>
    public static async Task WriteContentAsync(
        Stream stream,
        OutboundTransfer transfer,
        IdleTimeout? idle,
        CancellationToken cancellationToken)
    {
        // Header and block in one buffer, so each block is one write: on the
        // relayed transport a separate four-byte write would be a whole
        // record of its own.
        byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(BlockHeaderLength + BlockBytes);
        try
        {
            while (true)
            {
                int read = await transfer
                    .ReadAsync(buffer.AsMemory(BlockHeaderLength, BlockBytes), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                WriteBlockHeader(buffer, read);
                await stream
                    .WriteAsync(buffer.AsMemory(0, BlockHeaderLength + read), cancellationToken)
                    .ConfigureAwait(false);
                idle?.Restart();
                transfer.Advance(read);
            }

            // The end marker goes out even when the content stopped short of
            // the length that was announced. Retrying that would read the same
            // short content again; the receiver is the end that can tell the
            // difference between a truncated content and a finished one, and
            // it refuses it as an answer, which stops the sender for good.
            WriteBlockHeader(buffer, 0);
            await stream
                .WriteAsync(buffer.AsMemory(0, BlockHeaderLength), cancellationToken)
                .ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Reads blocks and throws them away, up to and including the end.</summary>
    /// <remarks>
    /// For content that arrived ahead of a refusal. Left unread, it would sit
    /// in the stream with the sender still pushing, and the refusal behind it
    /// would reach nobody.
    /// </remarks>
    public static async Task DrainAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(BlockBytes);
        try
        {
            int length;
            while ((length = await ReadBlockHeaderAsync(stream, cancellationToken).ConfigureAwait(false)) != 0)
            {
                await stream.ReadExactlyAsync(buffer.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}

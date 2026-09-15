// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Buffers.Binary;

namespace Tailcat.Link.Protocol;

/// <summary>What a frame from the peer is asking for.</summary>
internal enum LinkFrameKind : byte
{
    /// <summary>A message that expects exactly one answer.</summary>
    Request = 1,

    /// <summary>A message that expects no answer.</summary>
    Notify = 2,

    /// <summary>A liveness check, answered by the library rather than the application.</summary>
    Ping = 3,

    /// <summary>
    /// The first frame of every session: the invitation the dialling machine
    /// holds, which the machine that was dialled either accepts or refuses.
    /// </summary>
    Hello = 4,

    /// <summary>
    /// The frame that opens a channel, naming which of the peer's channel
    /// handlers it is for. The frames follow it on the same stream.
    /// </summary>
    /// <seealso cref="ChannelFrame"/>
    Channel = 6,

    /// <summary>
    /// Content of any size, going either way and resuming across sessions: a
    /// request with its answer, a notification, or a transfer. Only sent to a
    /// machine that said <see cref="PeerCapabilities.Exchanges"/>.
    /// </summary>
    /// <seealso cref="ExchangeFrame"/>
    Exchange = 7,
}

/// <summary>How a request turned out.</summary>
internal enum LinkFrameStatus : byte
{
    /// <summary>The payload is the answer.</summary>
    Ok = 0,

    /// <summary>The payload is a human-readable reason the request failed.</summary>
    Failed = 1,
}

/// <summary>What one request turned into, ready to be written back.</summary>
internal readonly record struct LinkAnswer(LinkFrameStatus Status, ReadOnlyMemory<byte> Payload);

/// <summary>
/// One length-prefixed message: a tag byte, the exchange it belongs to, a
/// 32-bit big-endian length, and that many bytes.
/// </summary>
/// <remarks>
/// <para>
/// QUIC gives ordered, reliable bytes on a stream, not messages, so the
/// length prefix is what turns them back into one. Each exchange gets its own
/// stream, so nothing here demultiplexes concurrent requests: QUIC already
/// does that.
/// </para>
/// <para>
/// The exchange id is not for routing, then, but for identity across
/// sessions. A request that is retried after the session died carries the id
/// of the original, which is how the receiver recognises it as the same
/// request rather than a second one.
/// </para>
/// <para>
/// There is no cap on the length. There used to be one, sixteen megabytes,
/// and it was a limit on what an application could send rather than a defence:
/// what it defended against — a peer announcing gigabytes and this side
/// allocating them on its word — is answered by allocating as bytes arrive
/// instead of as they are announced. The one frame that is still bounded is
/// the hello a host reads before it knows who is calling; see
/// <see cref="LinkProtocol.HelloFrameBytes"/>.
/// </para>
/// </remarks>
internal static class LinkFrame
{
    /// <summary>Tag, exchange id, length.</summary>
    public const int HeaderLength = 1 + 16 + 4;

    private const int ExchangeOffset = 1;
    private const int LengthOffset = ExchangeOffset + 16;

    /// <summary>
    /// How much is handed to the stream at a time.
    /// </summary>
    /// <remarks>
    /// Not a buffer size — the payload is already in memory — but how often
    /// the transfer can say that it is still moving. One write of sixteen
    /// megabytes would look identical to a peer that has gone silent.
    /// </remarks>
    private const int ProgressChunkBytes = 64 * 1024;

    /// <summary>
    /// What a read starts with before it has seen any of the payload.
    /// </summary>
    /// <remarks>
    /// The buffer then doubles as bytes arrive, up to the announced length, so
    /// memory follows what the peer has actually sent. A length is free to
    /// announce and bytes are not.
    /// </remarks>
    private const int FirstReadBytes = 64 * 1024;

    /// <summary>Writes one frame.</summary>
    /// <remarks>
    /// <c>idle</c> is told about every chunk that moves, so that a slow
    /// transfer is not mistaken for a dead one; it is null where the caller
    /// imposes no limit.
    /// </remarks>
    public static async Task WriteAsync(
        Stream stream,
        byte tag,
        Guid exchange,
        ReadOnlyMemory<byte> payload,
        IdleTimeout? idle,
        CancellationToken cancellationToken)
    {
        byte[] header = new byte[HeaderLength];
        header[0] = tag;
        // Big-endian so the bytes on the wire read as the printed form of the
        // id, whatever the endianness of either machine.
        exchange.TryWriteBytes(header.AsSpan(ExchangeOffset), bigEndian: true, out _);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(LengthOffset), payload.Length);

        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        for (int sent = 0; sent < payload.Length; sent += ProgressChunkBytes)
        {
            int size = Math.Min(ProgressChunkBytes, payload.Length - sent);
            await stream.WriteAsync(payload.Slice(sent, size), cancellationToken).ConfigureAwait(false);
            idle?.Restart();
        }
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads one frame, of whatever length the peer sends.</summary>
    /// <remarks>
    /// <c>idle</c> is as for <see cref="WriteAsync"/>: told about every chunk
    /// that arrives.
    /// </remarks>
    /// <exception cref="EndOfStreamException">If the peer stopped mid-frame.</exception>
    /// <exception cref="LinkException">If the peer announced a length no array can hold.</exception>
    public static Task<(byte Tag, Guid Exchange, byte[] Payload)> ReadAsync(
        Stream stream,
        IdleTimeout? idle,
        CancellationToken cancellationToken) =>
        ReadCoreAsync(stream, limit: null, idle, cancellationToken);

    /// <summary>
    /// Reads one frame from a machine that has not yet been shown to be a
    /// paired one, refusing anything longer than <paramref name="limit"/>.
    /// </summary>
    /// <exception cref="EndOfStreamException">If the peer stopped mid-frame.</exception>
    /// <exception cref="LinkException">If the peer announced more than <paramref name="limit"/>.</exception>
    public static Task<(byte Tag, Guid Exchange, byte[] Payload)> ReadAsync(
        Stream stream,
        int limit,
        IdleTimeout? idle,
        CancellationToken cancellationToken) =>
        ReadCoreAsync(stream, limit, idle, cancellationToken);

    private static async Task<(byte Tag, Guid Exchange, byte[] Payload)> ReadCoreAsync(
        Stream stream,
        int? limit,
        IdleTimeout? idle,
        CancellationToken cancellationToken)
    {
        byte[] header = new byte[HeaderLength];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        idle?.Restart();

        // Signed on purpose: the wire carries an unsigned length, and one past
        // two gigabytes reads negative here, which is also exactly where one
        // .NET array stops being able to hold it.
        int length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(LengthOffset));
        if (length < 0)
        {
            throw new LinkException(
                $"the peer announced a {(uint)length}-byte message, which no single array can hold");
        }
        if (limit is int most && length > most)
        {
            throw new LinkException($"the peer announced a {length}-byte message here; at most {most} is read");
        }

        byte[] payload = await ReadPayloadAsync(stream, length, idle, cancellationToken).ConfigureAwait(false);
        return (header[0], new Guid(header.AsSpan(ExchangeOffset, 16), bigEndian: true), payload);
    }

    /// <summary>
    /// Reads <paramref name="length"/> bytes, growing the buffer as they arrive
    /// rather than allocating what was announced.
    /// </summary>
    /// <exception cref="EndOfStreamException">If the peer stopped before all of them.</exception>
    public static async Task<byte[]> ReadPayloadAsync(
        Stream stream,
        int length,
        IdleTimeout? idle,
        CancellationToken cancellationToken)
    {
        byte[] payload = new byte[Math.Min(length, FirstReadBytes)];
        for (int read = 0; read < length;)
        {
            if (read == payload.Length)
            {
                Array.Resize(ref payload, (int)Math.Min(length, (long)payload.Length * 2));
            }
            int arrived = await stream.ReadAsync(payload.AsMemory(read), cancellationToken).ConfigureAwait(false);
            if (arrived == 0)
            {
                throw new EndOfStreamException(
                    $"the peer stopped after {read} of {length} bytes");
            }
            read += arrived;
            idle?.Restart();
        }
        return payload;
    }
}

// Copyright (c) Tailscale Inc & contributors
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
/// </remarks>
internal static class LinkFrame
{
    /// <summary>The largest payload either side will send or accept.</summary>
    /// <remarks>
    /// A cap is not a preference but a defence: without it, a peer claiming a
    /// two-gigabyte frame makes this side allocate it.
    /// </remarks>
    public const int MaxPayloadBytes = 16 * 1024 * 1024;

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

    /// <summary>Checks a payload against the cap before anything is attempted with it.</summary>
    /// <remarks>
    /// Separate from <see cref="WriteAsync"/> so a sender can find out that a
    /// message is too large without a session: this failure is the caller's,
    /// not the link's, and retrying it on a fresh session would fail exactly
    /// the same way.
    /// </remarks>
    /// <exception cref="LinkException">If the payload is over <see cref="MaxPayloadBytes"/>.</exception>
    public static void EnsureSendable(ReadOnlyMemory<byte> payload)
    {
        if (payload.Length > MaxPayloadBytes)
        {
            throw new LinkException(
                $"a message may be at most {MaxPayloadBytes} bytes, this one is {payload.Length}");
        }
    }

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
        EnsureSendable(payload);

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

    /// <summary>Reads one frame.</summary>
    /// <remarks>
    /// <c>idle</c> is as for <see cref="WriteAsync"/>: told about every chunk
    /// that arrives.
    /// </remarks>
    /// <exception cref="EndOfStreamException">If the peer stopped mid-frame.</exception>
    /// <exception cref="LinkException">If the peer announced an impossible length.</exception>
    public static async Task<(byte Tag, Guid Exchange, byte[] Payload)> ReadAsync(
        Stream stream,
        IdleTimeout? idle,
        CancellationToken cancellationToken)
    {
        byte[] header = new byte[HeaderLength];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        idle?.Restart();

        int length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(LengthOffset));
        if (length < 0 || length > MaxPayloadBytes)
        {
            throw new LinkException($"the peer announced a {length}-byte message; the limit is {MaxPayloadBytes}");
        }

        byte[] payload = new byte[length];
        for (int read = 0; read < length;)
        {
            int arrived = await stream.ReadAsync(payload.AsMemory(read), cancellationToken).ConfigureAwait(false);
            if (arrived == 0)
            {
                throw new EndOfStreamException(
                    $"the peer stopped after {read} of {length} bytes");
            }
            read += arrived;
            idle?.Restart();
        }
        return (header[0], new Guid(header.AsSpan(ExchangeOffset, 16), bigEndian: true), payload);
    }
}

// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Buffers;
using System.Buffers.Binary;

namespace Tailcat.Derp;

/// <summary>One DERP frame: its type and its payload bytes.</summary>
/// <param name="Type">The frame type.</param>
/// <param name="Payload">The frame's payload, without the 5-byte header.</param>
public readonly record struct DerpFrame(DerpFrameType Type, ReadOnlyMemory<byte> Payload);

/// <summary>
/// Reads and writes DERP frames over a byte stream: a one-byte frame type, a
/// big-endian uint32 length, then that many payload bytes.
/// </summary>
/// <remarks>
/// The instance is not safe for concurrent readers, nor for concurrent
/// writers; one reader and one writer may run at the same time, which is how
/// a DERP connection is normally used.
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Naming", "CA1711:Identifiers should not have incorrect suffix",
    Justification = "It is a stream of DERP frames, which is what the name says; it wraps a Stream rather than being one.")]
public sealed class DerpFrameStream(Stream stream) : IAsyncDisposable
{
    // The largest frame we will read. Frames are packets plus small headers;
    // anything beyond this is a broken or hostile peer.
    private const uint MaxFrameLen = (DerpProtocol.MaxPacketSize + 1024);

    private readonly Stream _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    // Never disposed. Disposing a SemaphoreSlim does not wake its waiters, so
    // a writer queued behind a write stuck on a dead connection waited for
    // good once the stream was closed, and the holder's Release threw as well.
    // It has no wait handle, so there is nothing to free.
    private readonly SemaphoreSlim _writeMu = new(1, 1);

    // Cancelled on dispose, which is what wakes the writers queued on _writeMu.
    private readonly CancellationTokenSource _closed = new();
    private volatile bool _disposed;

    /// <summary>The underlying stream, exposed for connection setup.</summary>
    public Stream Stream => _stream;

    /// <summary>
    /// Reads the next frame. The returned payload is freshly allocated and
    /// owned by the caller.
    /// </summary>
    /// <exception cref="DerpProtocolException">If the frame is malformed or too large.</exception>
    /// <exception cref="EndOfStreamException">If the connection ends mid-frame.</exception>
    public async Task<DerpFrame> ReadFrameAsync(CancellationToken cancellationToken = default)
    {
        byte[] header = new byte[DerpProtocol.FrameHeaderLen];
        await _stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);

        DerpFrameType type = (DerpFrameType)header[0];
        uint len = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(1));
        if (len > MaxFrameLen)
        {
            throw new DerpProtocolException($"frame of type 0x{(byte)type:X2} has length {len}, over the {MaxFrameLen} limit");
        }

        byte[] payload = new byte[len];
        if (len != 0)
        {
            await _stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        }
        return new DerpFrame(type, payload);
    }

    /// <summary>Writes one frame and flushes it.</summary>
    /// <remarks>
    /// A frame that only partly reached the wire leaves the connection
    /// unreadable for good: the peer takes the next frame's header for the
    /// missing payload and is five bytes out of step from then on, with every
    /// packet after it silently misrouted. So the header and the payload go
    /// out as one write, and a write that fails or is cancelled anyway closes
    /// the stream rather than letting the next frame follow a torn one.
    /// </remarks>
    public async Task WriteFrameAsync(
        DerpFrameType type,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        if (payload.Length > MaxFrameLen)
        {
            throw new ArgumentException($"frame payload of {payload.Length} bytes exceeds the {MaxFrameLen} limit", nameof(payload));
        }

        await AcquireWriteAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Pooled: every packet a relay carries passes through here, and a
            // 64 KiB payload would otherwise be a heap allocation and a copy
            // per packet.
            int length = DerpProtocol.FrameHeaderLen + payload.Length;
            byte[] frame = ArrayPool<byte>.Shared.Rent(length);
            try
            {
                frame[0] = (byte)type;
                BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(1), (uint)payload.Length);
                payload.Span.CopyTo(frame.AsSpan(DerpProtocol.FrameHeaderLen));

                try
                {
                    await _stream.WriteAsync(frame.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
                    await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // Closing is what tells the reader on this side too: a caller
                    // that swallows the send failure would otherwise go on using a
                    // connection that can no longer carry anything, where a closed
                    // one is reconnected. Disposing twice is harmless, so the
                    // ordinary DisposeAsync still runs its course afterwards.
                    await _stream.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(frame);
            }
        }
        finally
        {
            _writeMu.Release();
        }
    }

    // Waits for the write lock, or fails as a closed stream does once the
    // stream is disposed: ObjectDisposedException is what callers already treat
    // as a connection that is gone, where a cancellation would be one nobody
    // asked for.
    private async Task AcquireWriteAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (cancellationToken.CanBeCanceled)
            {
                using CancellationTokenSource either = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closed.Token);
                await _writeMu.WaitAsync(either.Token).ConfigureAwait(false);
            }
            else
            {
                await _writeMu.WaitAsync(_closed.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ObjectDisposedException(nameof(DerpFrameStream));
        }

        if (_disposed)
        {
            _writeMu.Release();
            throw new ObjectDisposedException(nameof(DerpFrameStream));
        }
    }

    /// <summary>Disposes the underlying stream.</summary>
    /// <remarks>
    /// A write in progress fails as the stream closes under it, and writers
    /// waiting their turn fail with <see cref="ObjectDisposedException"/>.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        // Not disposed afterwards: a writer that read _disposed just before it
        // was set may still ask for the token, and must find it cancelled
        // rather than gone. With no timer and no linked parent it holds nothing.
        await _closed.CancelAsync().ConfigureAwait(false);
        await _stream.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>Thrown when a DERP peer sends something the protocol doesn't allow.</summary>
public class DerpProtocolException : Exception
{
    /// <summary>Creates an exception with no message.</summary>
    public DerpProtocolException()
    {
    }

    /// <summary>Creates an exception with the given message.</summary>
    public DerpProtocolException(string message) : base(message)
    {
    }

    /// <summary>Creates an exception wrapping an underlying cause.</summary>
    public DerpProtocolException(string message, Exception? innerException) : base(message, innerException)
    {
    }
}

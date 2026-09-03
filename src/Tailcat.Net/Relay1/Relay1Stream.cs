// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Threading.Channels;

namespace Tailcat.Net.Relay1;

/// <summary>
/// One stream of a <c>relay1</c> session, which is what QUIC used to provide.
/// </summary>
/// <remarks>
/// <para>
/// Reading and writing are independent: the peer's FIN ends what can be read
/// without stopping what can be written, exactly as a QUIC bidirectional
/// stream behaves, because the layer above opens one stream per request and
/// closes each direction as it finishes with it.
/// </para>
/// <para>
/// Writing is bounded by credit the receiver grants. Without it a sender
/// would push whole multi-megabyte messages at a relay that drops what it
/// cannot deliver, and a dropped record ends the whole session rather than
/// one stream.
/// </para>
/// </remarks>
internal sealed class Relay1Stream : Stream
{
    /// <summary>How much a receiver lets the peer send before it has read any of it.</summary>
    public const int InitialWindow = 256 * 1024;

    // Granting credit back in small pieces would spend a record on each; this
    // waits until enough has been consumed to be worth announcing.
    private const int WindowUpdateThreshold = InitialWindow / 4;

    private readonly Relay1Connection _connection;
    private readonly Channel<ReadOnlyMemory<byte>> _inbound =
        Channel.CreateUnbounded<ReadOnlyMemory<byte>>(new UnboundedChannelOptions { SingleReader = true });

    private readonly Lock _mu = new();
    private ReadOnlyMemory<byte> _leftover;
    private int _consumedSinceUpdate;
    private long _credit = InitialWindow;
    private TaskCompletionSource? _creditWaiter;
    private string? _reset;
    private bool _finSent;
    private bool _disposed;

    internal Relay1Stream(Relay1Connection connection, ulong id)
    {
        _connection = connection;
        Id = id;
    }

    /// <summary>This stream's id on the wire.</summary>
    public ulong Id { get; }

    /// <inheritdoc/>
    public override bool CanRead => true;

    /// <inheritdoc/>
    public override bool CanSeek => false;

    /// <inheritdoc/>
    public override bool CanWrite => !_finSent;

    /// <inheritdoc/>
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc/>
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <inheritdoc/>
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty)
        {
            return 0;
        }

        int taken;
        while (true)
        {
            lock (_mu)
            {
                if (_reset is not null && _leftover.IsEmpty && !_inbound.Reader.TryPeek(out _))
                {
                    throw new IOException($"the peer reset the stream: {_reset}");
                }
                if (!_leftover.IsEmpty)
                {
                    taken = Math.Min(buffer.Length, _leftover.Length);
                    _leftover[..taken].CopyTo(buffer);
                    _leftover = _leftover[taken..];
                    _consumedSinceUpdate += taken;
                    break;
                }
            }

            try
            {
                _leftover = await _inbound.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                return 0; // The peer said FIN and everything it sent is read.
            }
        }

        await GrantCreditIfDueAsync(cancellationToken).ConfigureAwait(false);
        return taken;
    }

    /// <inheritdoc/>
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int max = Relay1Record.MaxPlaintext - Relay1Frame.HeaderLength(Id);
        while (!buffer.IsEmpty)
        {
            int chunk = Math.Min(buffer.Length, max);
            chunk = (int)Math.Min(chunk, await ReserveCreditAsync(chunk, cancellationToken).ConfigureAwait(false));
            await _connection
                .SendFrameAsync(Id, Relay1FrameFlags.None, buffer[..chunk], cancellationToken)
                .ConfigureAwait(false);
            buffer = buffer[chunk..];
        }
    }

    /// <inheritdoc/>
    public override void Flush()
    {
    }

    /// <inheritdoc/>
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count) =>
        WriteAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        // The peer is waiting to read; without a FIN it waits for good.
        if (!_finSent)
        {
            _finSent = true;
            try
            {
                await _connection
                    .SendFrameAsync(Id, Relay1FrameFlags.Fin, ReadOnlyMemory<byte>.Empty, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or TailcatException)
            {
                // The session is already gone, which ends the stream anyway.
            }
        }

        _connection.Forget(Id);
        _inbound.Writer.TryComplete();
        await base.DisposeAsync().ConfigureAwait(false);
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        base.Dispose(disposing);
    }

    // ---- driven by the connection's receive path ------------------------

    internal void OnData(ReadOnlyMemory<byte> payload)
    {
        if (!payload.IsEmpty)
        {
            _inbound.Writer.TryWrite(payload);
        }
    }

    internal void OnFin() => _inbound.Writer.TryComplete();

    internal void OnReset(string reason)
    {
        lock (_mu)
        {
            _reset = reason;
        }
        _inbound.Writer.TryComplete();
    }

    internal void OnWindow(long credit)
    {
        TaskCompletionSource? waiting;
        lock (_mu)
        {
            _credit += credit;
            waiting = _creditWaiter;
            _creditWaiter = null;
        }
        waiting?.TrySetResult();
    }

    internal void OnSessionClosed()
    {
        lock (_mu)
        {
            _reset ??= "the session ended";
        }
        _inbound.Writer.TryComplete();
        OnWindow(0);
    }

    // ---- flow control ---------------------------------------------------

    private async Task<long> ReserveCreditAsync(int wanted, CancellationToken cancellationToken)
    {
        while (true)
        {
            Task waiting;
            lock (_mu)
            {
                if (_reset is not null)
                {
                    throw new IOException($"the stream is not writable: {_reset}");
                }
                if (_credit > 0)
                {
                    long taken = Math.Min(wanted, _credit);
                    _credit -= taken;
                    return taken;
                }
                _creditWaiter ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                waiting = _creditWaiter.Task;
            }
            await waiting.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task GrantCreditIfDueAsync(CancellationToken cancellationToken)
    {
        int grant;
        lock (_mu)
        {
            if (_consumedSinceUpdate < WindowUpdateThreshold)
            {
                return;
            }
            grant = _consumedSinceUpdate;
            _consumedSinceUpdate = 0;
        }

        byte[] payload = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(payload, (uint)grant);
        try
        {
            await _connection
                .SendFrameAsync(Id, Relay1FrameFlags.Window, payload, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or TailcatException)
        {
            // The session ended; there is nobody left to grant credit to.
        }
    }
}

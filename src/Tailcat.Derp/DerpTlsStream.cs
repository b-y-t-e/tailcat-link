// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Threading.Channels;
using Org.BouncyCastle.Tls;

namespace Tailcat.Derp;

/// <summary>
/// A TLS stream over BouncyCastle that allows a reader and a writer to work at
/// the same time.
/// </summary>
/// <remarks>
/// <para>
/// BouncyCastle's stream-mode TLS holds its lock for the whole of a blocking
/// read, so a thread parked waiting for data blocks every send behind it. A
/// DERP connection does exactly that — a receive loop parked on the socket
/// while other work sends — and the result is a deadlock that lasts until
/// something times out.
/// </para>
/// <para>
/// So this uses BouncyCastle's non-blocking mode instead: the protocol object
/// only transforms buffers, always under a short-lived lock and never while
/// waiting on the network. Socket I/O happens outside that lock. Outbound
/// records are queued and written by a single pump, which keeps them in the
/// order TLS produced them — TLS records must not be reordered.
/// </para>
/// </remarks>
internal sealed class DerpTlsStream : Stream
{
    private readonly Stream _transport;
    private readonly TlsClientProtocol _protocol;
    private readonly Lock _tlsMu = new();
    // Bounded so a peer that stops reading cannot grow this without limit.
    // Waiting rather than dropping: TLS records must not be skipped, or the
    // stream is unreadable from that point on.
    private readonly Channel<byte[]> _outbound =
        Channel.CreateBounded<byte[]>(new BoundedChannelOptions(1024)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

    private readonly CancellationTokenSource _cts = new();
    private Task? _outboundPump;
    private byte[] _pending = [];
    private int _pendingOffset;
    private bool _disposed;

    private DerpTlsStream(Stream transport, TlsClientProtocol protocol)
    {
        _transport = transport;
        _protocol = protocol;
    }

    /// <summary>
    /// Runs the TLS handshake with <paramref name="client"/> and returns the
    /// established stream.
    /// </summary>
    public static async Task<DerpTlsStream> HandshakeAsync(
        Stream transport,
        TlsClient client,
        CancellationToken cancellationToken)
    {
        TlsClientProtocol protocol = new(); // non-blocking mode
        DerpTlsStream stream = new(transport, protocol);

        protocol.Connect(client);
        byte[] buffer = new byte[16 * 1024];
        while (protocol.IsHandshaking)
        {
            await stream.FlushProtocolOutputAsync(cancellationToken).ConfigureAwait(false);
            if (!protocol.IsHandshaking)
            {
                break;
            }

            int n = await transport.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                throw new DerpProtocolException("the connection closed during the TLS handshake");
            }
            lock (stream._tlsMu)
            {
                protocol.OfferInput(buffer, 0, n);
            }
        }
        await stream.FlushProtocolOutputAsync(cancellationToken).ConfigureAwait(false);

        stream._outboundPump = Task.Run(() => stream.PumpOutboundAsync(stream._cts.Token), CancellationToken.None);
        return stream;
    }

    /// <inheritdoc/>
    public override bool CanRead => true;

    /// <inheritdoc/>
    public override bool CanSeek => false;

    /// <inheritdoc/>
    public override bool CanWrite => true;

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
        while (true)
        {
            // Anything already decrypted goes out first.
            int available = _pending.Length - _pendingOffset;
            if (available > 0)
            {
                int take = Math.Min(available, buffer.Length);
                _pending.AsMemory(_pendingOffset, take).CopyTo(buffer);
                _pendingOffset += take;
                return take;
            }

            byte[] chunk = new byte[16 * 1024];
            int n = await _transport.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                return 0;
            }

            byte[]? decrypted;
            byte[]? queued;
            lock (_tlsMu)
            {
                _protocol.OfferInput(chunk, 0, n);
                int ready = _protocol.GetAvailableInputBytes();
                decrypted = ready > 0 ? new byte[ready] : null;
                if (decrypted is not null)
                {
                    _protocol.ReadInput(decrypted, 0, ready);
                }
                queued = TakeProtocolOutputLocked();
            }

            // Reading can produce output of its own (a key update, an alert).
            if (queued is not null)
            {
                await _outbound.Writer.WriteAsync(queued, cancellationToken).ConfigureAwait(false);
            }
            if (decrypted is not null)
            {
                (_pending, _pendingOffset) = (decrypted, 0);
            }
        }
    }

    /// <summary>
    /// Encrypts <paramref name="buffer"/> and queues it for sending.
    /// </summary>
    /// <remarks>
    /// Returning does not mean the bytes reached the peer: the pump writes them
    /// afterwards, and a connection that dies at that moment surfaces on the
    /// reading side, not here. That suits a relay, whose delivery is best
    /// effort anyway, but callers must not read success into it.
    /// </remarks>
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        byte[]? queued = null;
        lock (_tlsMu)
        {
            byte[] data = buffer.ToArray();
            _protocol.WriteApplicationData(data, 0, data.Length);
            queued = TakeProtocolOutputLocked();
        }
        if (queued is not null)
        {
            await _outbound.Writer.WriteAsync(queued, cancellationToken).ConfigureAwait(false);
        }
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
    public override void Flush()
    {
    }

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void SetLength(long value) => throw new NotSupportedException();

    // TakeProtocolOutputLocked returns whatever TLS produced, for the caller to
    // queue. The caller must hold the lock, so records leave in the order they
    // were generated; TLS records must never be reordered.
    private byte[]? TakeProtocolOutputLocked()
    {
        int available = _protocol.GetAvailableOutputBytes();
        if (available <= 0)
        {
            return null;
        }
        byte[] output = new byte[available];
        _protocol.ReadOutput(output, 0, available);
        return output;
    }

    // FlushProtocolOutputAsync writes pending TLS output directly, for use
    // during the handshake before the pump is running.
    private async Task FlushProtocolOutputAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            byte[]? output = null;
            lock (_tlsMu)
            {
                int available = _protocol.GetAvailableOutputBytes();
                if (available > 0)
                {
                    output = new byte[available];
                    _protocol.ReadOutput(output, 0, available);
                }
            }
            if (output is null)
            {
                return;
            }
            await _transport.WriteAsync(output, cancellationToken).ConfigureAwait(false);
            await _transport.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task PumpOutboundAsync(CancellationToken ct)
    {
        try
        {
            await foreach (byte[] output in _outbound.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                await _transport.WriteAsync(output, ct).ConfigureAwait(false);
                await _transport.FlushAsync(ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // The connection is gone; readers will see it too.
        }
    }

    /// <inheritdoc/>
    public override async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        await _cts.CancelAsync().ConfigureAwait(false);
        _outbound.Writer.TryComplete();
        if (_outboundPump is not null)
        {
            try
            {
                await _outboundPump.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
        await _transport.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();

        // Safe to reach the base now: the guard above makes the Dispose(bool)
        // it triggers a no-op.
        await base.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _cts.Cancel();
            _outbound.Writer.TryComplete();
            _transport.Dispose();
            _cts.Dispose();
        }
        base.Dispose(disposing);
    }
}

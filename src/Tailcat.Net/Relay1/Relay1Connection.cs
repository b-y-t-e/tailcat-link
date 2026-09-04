// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Threading.Channels;
using Tailcat.Keys;

namespace Tailcat.Net.Relay1;

/// <summary>
/// A session carried entirely by the relay, for ends that cannot have QUIC.
/// </summary>
/// <remarks>
/// <para>
/// Everything QUIC was providing is here in its smallest honest form:
/// encryption from the ephemeral exchange in the hello, ordering from the
/// relay's own connection, framing and multiplexing from
/// <see cref="Relay1Frame"/>. What is not here is retransmission — a record
/// the relay drops ends the session, and <c>DurableLink</c> re-establishes it
/// and re-sends the request without re-running it.
/// </para>
/// <para>
/// It is slower than QUIC and it never leaves the relay, which is why a pair
/// that can do QUIC always does. This is what the other machines get instead
/// of nothing.
/// </para>
/// </remarks>
internal sealed class Relay1Connection : ITailcatConnection
{
    private readonly byte[] _sendKey;
    private readonly byte[] _receiveKey;
    private readonly Func<ReadOnlyMemory<byte>, CancellationToken, Task> _send;
    private readonly Func<Relay1Connection, ValueTask>? _onClosed;
    private readonly ConcurrentDictionary<ulong, Relay1Stream> _streams = new();
    private readonly Channel<Relay1Stream> _accepted =
        Channel.CreateUnbounded<Relay1Stream>(new UnboundedChannelOptions { SingleReader = true });

    // Peer streams this end has closed, by id. A frame for one of these must
    // not reopen it: DisposeAsync runs on whatever task finished with the
    // stream, before the peer's own FIN for it — still in flight — arrives.
    private readonly HashSet<ulong> _retiredPeerStreams = new();

    // The first peer-parity stream id that has not been retired; the run
    // below it collapses into this, and the set only holds what came out of
    // turn.
    private ulong _retiredPeerWatermark;

    // Streams are forgotten from whatever task closes them; records arrive on
    // the receive loop. The retirement state is touched from both.
    private readonly Lock _retiredMu = new();

    // One record at a time: the counter has to increase in the order the
    // records reach the relay, or the far end sees a gap and gives up.
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private ulong _sendCounter;
    private ulong _expectedCounter;

    private ulong _nextStreamId;
    private bool _disposed;

    internal Relay1Connection(
        NodePublic peer,
        Relay1Keys keys,
        bool isDialer,
        Func<ReadOnlyMemory<byte>, CancellationToken, Task> send,
        Func<Relay1Connection, ValueTask>? onClosed = null)
    {
        Peer = peer;
        _send = send;
        _onClosed = onClosed;
        (_sendKey, _receiveKey) = isDialer
            ? (keys.DialerToHost, keys.HostToDialer)
            : (keys.HostToDialer, keys.DialerToHost);

        // Odd from the dialler, even from the host, so neither end has to ask
        // before opening one and the two can never pick the same id.
        _nextStreamId = isDialer ? 1UL : 2UL;
        _retiredPeerWatermark = isDialer ? 2UL : 1UL;
    }

    /// <inheritdoc/>
    public NodePublic Peer { get; }

    /// <inheritdoc/>
    /// <remarks>
    /// Always the relay, and it never changes: that is what this transport
    /// is. The MTU reported is how much plaintext one record carries.
    /// </remarks>
    public PeerPath CurrentPath { get; } =
        new(PeerPathKind.Relay, null, null, default, Relay1Record.MaxPlaintext);

    /// <inheritdoc/>
    public IReadOnlyList<PeerPath> Paths => [CurrentPath];

    /// <inheritdoc/>
    /// <remarks>Never raised; there is nowhere for this session to move to.</remarks>
    public event Action<PeerPath>? PathChanged
    {
        add { }
        remove { }
    }

    /// <inheritdoc/>
    public Task<Stream> OpenStreamAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ulong id = Interlocked.Add(ref _nextStreamId, 2) - 2;
        Relay1Stream stream = new(this, id);
        _streams[id] = stream;
        return Task.FromResult<Stream>(stream);
    }

    /// <inheritdoc/>
    public async Task<Stream> AcceptStreamAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return await _accepted.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Seals one frame and hands it to the relay.</summary>
    internal async Task SendFrameAsync(
        ulong streamId,
        Relay1FrameFlags flags,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        byte[] frame = Relay1Frame.Encode(streamId, flags, payload.Span);

        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_sendCounter == ulong.MaxValue)
            {
                throw new TailcatException("this relay1 session has sent as many records as its keys allow");
            }
            byte[] record = Relay1Record.Seal(frame, _sendKey, _sendCounter++);
            await _send(record, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>
    /// Takes one record off the relay.
    /// </summary>
    /// <returns>
    /// False when the session cannot continue — a record that does not open,
    /// or one that arrives out of order — in which case the caller closes it.
    /// </returns>
    internal bool HandleRecord(ReadOnlySpan<byte> record)
    {
        if (_disposed)
        {
            return false;
        }
        if (!Relay1Record.TryOpen(record, _receiveKey, out ulong counter, out byte[] plaintext))
        {
            return false;
        }

        // Strictly in sequence. A gap is a record the relay dropped, and
        // nothing here can recover the stream it belonged to; carrying on
        // would hand the layer above a hole in the middle of a message.
        if (counter != _expectedCounter)
        {
            return false;
        }
        _expectedCounter++;

        if (!Relay1Frame.TryDecode(plaintext, out ulong streamId, out Relay1FrameFlags flags, out ReadOnlyMemory<byte> payload))
        {
            return false;
        }

        Relay1Stream? stream = StreamFor(streamId);
        if (stream is null)
        {
            // A frame for a stream that has been disposed, or retired. Late
            // window updates and FINs are normal; nothing to do with them.
            return true;
        }

        if (flags.HasFlag(Relay1FrameFlags.Window))
        {
            if (payload.Length >= 4)
            {
                stream.OnWindow(BinaryPrimitives.ReadUInt32BigEndian(payload.Span));
            }
            return true;
        }
        if (flags.HasFlag(Relay1FrameFlags.Reset))
        {
            stream.OnReset(System.Text.Encoding.UTF8.GetString(payload.Span));
            return true;
        }

        stream.OnData(payload);
        if (flags.HasFlag(Relay1FrameFlags.Fin))
        {
            stream.OnFin();
        }
        return true;
    }

    internal void Forget(ulong streamId)
    {
        _streams.TryRemove(streamId, out _);
        if (IsPeerStreamId(streamId))
        {
            RetirePeerStream(streamId);
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        _accepted.Writer.TryComplete();
        foreach (Relay1Stream stream in _streams.Values)
        {
            stream.OnSessionClosed();
        }
        _streams.Clear();
        _sendLock.Dispose();

        if (_onClosed is not null)
        {
            await _onClosed(this).ConfigureAwait(false);
        }
    }

    // Parity alone says who opened a stream: odd from the dialler, even from
    // the host.
    private bool IsPeerStreamId(ulong streamId) => (streamId % 2 == 1) != (_nextStreamId % 2 == 1);

    // A stream this end has finished with must not come back when the peer's
    // own FIN arrives afterwards, which it does on every request the peer
    // makes. Only the ids actually retired can say that: a higher id may well
    // arrive first, because the peer numbers a stream before taking the lock
    // that serialises its sending, so two of its concurrent requests can
    // reach the relay in the other order. Treating the highest id seen as a
    // watermark dropped the lower one's frames in silence.
    private void RetirePeerStream(ulong streamId)
    {
        lock (_retiredMu)
        {
            _retiredPeerStreams.Add(streamId);
            // The peer allocates its ids in order, so the run starting at the
            // oldest collapses into the watermark and the set only holds what
            // came out of turn.
            while (_retiredPeerStreams.Remove(_retiredPeerWatermark))
            {
                _retiredPeerWatermark += 2;
            }
        }
    }

    private bool IsRetiredPeerStream(ulong streamId)
    {
        lock (_retiredMu)
        {
            return streamId < _retiredPeerWatermark || _retiredPeerStreams.Contains(streamId);
        }
    }

    // A frame naming an id this end did not open, and has not seen before, is
    // the peer opening a stream: that is the only announcement there is.
    private Relay1Stream? StreamFor(ulong streamId)
    {
        if (_streams.TryGetValue(streamId, out Relay1Stream? known))
        {
            return known;
        }

        if (!IsPeerStreamId(streamId) || streamId == 0)
        {
            return null;
        }

        // An id this end has already retired names a stream that has been
        // closed and forgotten; reviving it would hand the layer above a
        // stream with nothing in it. See RetirePeerStream.
        if (IsRetiredPeerStream(streamId))
        {
            return null;
        }

        Relay1Stream opened = new(this, streamId);
        if (!_streams.TryAdd(streamId, opened))
        {
            return _streams[streamId];
        }
        if (!_accepted.Writer.TryWrite(opened))
        {
            _streams.TryRemove(streamId, out _);
            return null;
        }
        return opened;
    }
}

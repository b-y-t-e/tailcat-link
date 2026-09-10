// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Buffers;
using System.Buffers.Binary;
using System.Net.Quic;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using Tailcat.Link.Diagnostics;

namespace Tailcat.Link.Protocol;

/// <summary>Serves one channel the peer has opened, on the stream it opened it with.</summary>
/// <param name="stream">The stream the frames arrive on.</param>
/// <param name="cancellationToken">Cancelled when the session ends.</param>
internal delegate Task LinkChannelServe(Stream stream, CancellationToken cancellationToken);

/// <summary>
/// The wire format of a channel: a name in the frame that opens it, an answer
/// saying whether anybody is listening, and then frames as length-prefixed
/// blocks on the same stream.
/// </summary>
/// <remarks>
/// One stream per channel is what makes it ordered without anything here
/// having to sequence it — the transport already does — and what makes it end
/// with its session, which is the contract a channel offers instead of a
/// transfer's durability.
/// </remarks>
internal static class ChannelFrame
{
    /// <summary>The most one frame may carry.</summary>
    /// <remarks>
    /// A quarter of a megabyte, the same as a transfer block: large enough
    /// that no realtime frame comes near it, small enough that a peer cannot
    /// make this side allocate on its say-so.
    /// </remarks>
    public const int MaxFrameBytes = 256 * 1024;

    /// <summary>The length prefix in front of every frame.</summary>
    public const int HeaderLength = 4;

    /// <summary>The most a channel name may be, in UTF-8 bytes.</summary>
    public const int MaxNameBytes = 256;

    /// <summary>Reads the name out of the frame that opens a channel.</summary>
    /// <exception cref="LinkException">If it is not a name.</exception>
    public static string DecodeName(ReadOnlySpan<byte> payload)
    {
        if (payload.Length is 0 or > MaxNameBytes)
        {
            throw new LinkException($"a channel name must be 1 to {MaxNameBytes} bytes, this one is {payload.Length}");
        }
        return Encoding.UTF8.GetString(payload);
    }

    /// <summary>Writes the name for the frame that opens a channel.</summary>
    /// <exception cref="ArgumentException">If the name is empty or too long.</exception>
    public static byte[] EncodeName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        byte[] encoded = Encoding.UTF8.GetBytes(name);
        if (encoded.Length > MaxNameBytes)
        {
            throw new ArgumentException(
                $"a channel name may be at most {MaxNameBytes} bytes, this one is {encoded.Length}", nameof(name));
        }
        return encoded;
    }

    /// <summary>Writes one frame, or the marker that ends the channel.</summary>
    /// <remarks>
    /// The prefix and the body go out as one write. Written separately, a send
    /// cancelled between the two would leave a length prefix on a stream every
    /// later frame of this channel shares, and the peer would read the next
    /// frame as this one's body — misframed and silent from there on. A
    /// request cannot suffer that, because each one has a stream to itself.
    /// </remarks>
    public static async Task WriteAsync(Stream stream, ReadOnlyMemory<byte> frame, CancellationToken ct)
    {
        int length = HeaderLength + frame.Length;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            BinaryPrimitives.WriteInt32BigEndian(buffer, frame.Length);
            frame.Span.CopyTo(buffer.AsSpan(HeaderLength));
            await stream.WriteAsync(buffer.AsMemory(0, length), ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Reads one frame, or null once the other end has closed the channel.</summary>
    /// <exception cref="LinkException">If the peer announced an impossible length.</exception>
    public static async Task<byte[]?> ReadAsync(Stream stream, CancellationToken ct)
    {
        byte[] header = new byte[HeaderLength];
        await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length < 0 || length > MaxFrameBytes)
        {
            throw new LinkException($"the peer announced a {length}-byte channel frame; the limit is {MaxFrameBytes}");
        }
        if (length == 0)
        {
            return null;
        }

        byte[] frame = new byte[length];
        await stream.ReadExactlyAsync(frame, ct).ConfigureAwait(false);
        return frame;
    }
}

/// <summary>What both ends of a channel have in common: a name, a peer, and one ending.</summary>
internal abstract class LinkChannelBase(string name, ILinkPeer peer, LinkLog log) : ILinkChannel
{
    private readonly Lock _mu = new();
    private ChannelClosedEventArgs? _closed;

    /// <inheritdoc/>
    public string Name => name;

    /// <inheritdoc/>
    public ILinkPeer Peer => peer;

    /// <inheritdoc/>
    public bool IsOpen
    {
        get
        {
            lock (_mu)
            {
                return _closed is null;
            }
        }
    }

    /// <inheritdoc/>
    public event EventHandler<ChannelClosedEventArgs>? Closed;

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await CloseAsync(ChannelCloseReason.LocalClosed, "this end closed the channel").ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Ends the channel once, for whichever reason arrived first, and tells
    /// the application which it was.
    /// </summary>
    protected async Task CloseAsync(ChannelCloseReason reason, string detail)
    {
        ChannelClosedEventArgs ending = new(reason, detail);
        lock (_mu)
        {
            if (_closed is not null)
            {
                return;
            }
            _closed = ending;
        }

        await ReleaseAsync(reason).ConfigureAwait(false);

        // The application's handler is isolated, as every other event of the
        // link's is: closing is on the failure path of SendAsync and on
        // DisposeAsync, and a handler that throws there would replace the
        // reason the caller is being told about with one of its own.
        try
        {
            Closed?.Invoke(this, ending);
        }
        catch (Exception ex)
        {
            log.Warn($"the Closed handler of the \"{name}\" channel threw: {ex.Message}");
        }
    }

    /// <summary>Lets go of the stream, once, however the channel ended.</summary>
    protected abstract ValueTask ReleaseAsync(ChannelCloseReason reason);

    /// <summary>
    /// Ends the channel because the session carrying it has ended.
    /// </summary>
    /// <remarks>
    /// Called by the session, which holds every channel opened on it: a
    /// channel that learnt of a dead session only from its next send would
    /// leave an application that closes its pipeline on
    /// <see cref="ILinkChannel.Closed"/> waiting for an event that never came.
    /// </remarks>
    internal Task EndWithSessionAsync(string detail) => CloseAsync(ChannelCloseReason.SessionEnded, detail);

    /// <summary>
    /// Whether the failure means the channel is over rather than that the
    /// caller changed its mind.
    /// </summary>
    /// <remarks>
    /// The session's own token is what a handler is given, so a session that
    /// died and a caller that cancelled arrive as the same
    /// <see cref="OperationCanceledException"/> on the same token. The session
    /// is asked first: if it is gone, the channel is over whoever else was
    /// waiting on it.
    /// </remarks>
    protected static bool EndsTheChannel(Exception ex, CancellationToken sessionAlive, CancellationToken caller) =>
        SessionFailure.EndsTheSession(ex)
        && (sessionAlive.IsCancellationRequested || !caller.IsCancellationRequested);
}

/// <summary>The end that opened the channel and sends on it.</summary>
/// <remarks>
/// Sends are serialised, because the order frames arrive in is the only thing
/// a channel promises and two writers on one stream would interleave.
/// </remarks>
internal sealed class OutgoingChannel(
    string name,
    ILinkPeer peer,
    Stream stream,
    LinkLog log,
    CancellationToken sessionAlive)
    : LinkChannelBase(name, peer, log), ILinkChannelWriter
{
    private readonly SemaphoreSlim _sending = new(1, 1);

    /// <inheritdoc/>
    public async Task SendAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken = default)
    {
        if (frame.Length is 0 or > ChannelFrame.MaxFrameBytes)
        {
            throw new LinkException(
                $"a channel frame must be 1 to {ChannelFrame.MaxFrameBytes} bytes, this one is {frame.Length}");
        }
        if (!IsOpen)
        {
            throw new LinkClosedException($"the \"{Name}\" channel has ended");
        }

        using CancellationTokenSource alive =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, sessionAlive);
        try
        {
            // Waiting for the turn is inside the try because a session that
            // died while a frame was queued fails here rather than on the
            // write, and it is the same ending either way.
            await _sending.WaitAsync(alive.Token).ConfigureAwait(false);
            try
            {
                // Asked again with the turn in hand: the check above happened
                // before the wait, and a close that ran during it has already
                // written the goodbye marker and disposed the stream. Writing
                // now would be a frame after the channel's own ending.
                if (!IsOpen)
                {
                    throw new LinkClosedException($"the \"{Name}\" channel has ended");
                }

                // The session's token, not the caller's: cancelling the write
                // itself would leave part of a frame on a stream every later
                // frame of this channel shares, and the peer would read a
                // length prefix out of the middle of the data — on relay1 the
                // more likely of the two, since a write there is split into
                // 32256-byte records. So the caller's cancellation is honoured
                // up to the moment honouring it would misframe the channel,
                // and a session that dies still ends the write.
                await ChannelFrame.WriteAsync(stream, frame, sessionAlive).ConfigureAwait(false);
            }
            finally
            {
                // Before the channel is closed rather than after: closing lets
                // go of the semaphore this is releasing.
                _sending.Release();
            }
        }
        catch (Exception ex) when (EndsTheChannel(ex, sessionAlive, cancellationToken))
        {
            await CloseAsync(ChannelCloseReason.SessionEnded, ex.Message).ConfigureAwait(false);
            throw new LinkClosedException($"the \"{Name}\" channel ended with its session", ex);
        }
    }

    /// <inheritdoc/>
    protected override async ValueTask ReleaseAsync(ChannelCloseReason reason)
    {
        // Behind the same gate the frames go through: a marker written beside
        // a frame still on the wire would interleave with it, and the peer
        // would read the two as one corrupt length prefix. Whoever holds the
        // turn is already past the point where it can be told to stop, so
        // waiting for it is waiting for one frame.
        await _sending.WaitAsync().ConfigureAwait(false);
        try
        {
            if (reason == ChannelCloseReason.LocalClosed)
            {
                // The zero-length marker is what tells the other end that the
                // frames stopped on purpose rather than with the session. It is
                // best-effort: a session that has already died cannot carry it,
                // and the peer learns the same thing from the stream ending.
                try
                {
                    await ChannelFrame.WriteAsync(stream, ReadOnlyMemory<byte>.Empty, CancellationToken.None)
                        .ConfigureAwait(false);
                }
#pragma warning disable CA1031 // Nothing can be done about a goodbye that did not arrive, and the caller is disposing.
                catch (Exception)
#pragma warning restore CA1031
                {
                }
            }

            await stream.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            // Released and never disposed. A sender that parked on the turn
            // while this was closing has to be let through to find the channel
            // closed and be told so, and SemaphoreSlim.Dispose does not fail
            // its waiters — it abandons them, so that sender would sit inside
            // SendAsync until the session died hours later instead of getting
            // the LinkClosedException the contract promises. Nothing is leaked
            // by not disposing: no wait handle is ever asked for.
            _sending.Release();
        }
    }
}

/// <summary>The end the peer opened a channel into.</summary>
internal sealed class IncomingChannel(
    string name,
    ILinkPeer peer,
    Stream stream,
    LinkLog log,
    CancellationToken sessionAlive)
    : LinkChannelBase(name, peer, log), ILinkChannelReader
{
    /// <inheritdoc/>
    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource alive =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, sessionAlive);
        while (true)
        {
            byte[]? frame;
            try
            {
                frame = await ChannelFrame.ReadAsync(stream, alive.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (EndsTheChannel(ex, sessionAlive, cancellationToken))
            {
                await CloseAsync(ChannelCloseReason.SessionEnded, ex.Message).ConfigureAwait(false);
                yield break;
            }

            if (frame is null)
            {
                await CloseAsync(ChannelCloseReason.PeerClosed, "the peer closed the channel").ConfigureAwait(false);
                yield break;
            }
            yield return frame;
        }
    }

    /// <summary>
    /// Ends the channel because the handler that was serving it returned.
    /// </summary>
    /// <remarks>
    /// A handler that leaves <see cref="ReadAllAsync"/> early — one frame and
    /// a break — closes nothing itself, and the channel it was given would
    /// stay open for good with its <see cref="ILinkChannel.Closed"/> never
    /// raised. The serving loop calls this instead, so "the channel ends when
    /// the handler returns" holds however the handler chose to stop.
    /// </remarks>
    internal Task EndAsync(string detail) => CloseAsync(ChannelCloseReason.LocalClosed, detail);

    /// <inheritdoc/>
    protected override ValueTask ReleaseAsync(ChannelCloseReason reason) =>
        // The stream belongs to the serving loop, which closes it once the
        // handler has returned; closing it here would cut the handler off
        // from frames it is still reading.
        ValueTask.CompletedTask;
}

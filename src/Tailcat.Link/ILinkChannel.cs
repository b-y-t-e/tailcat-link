// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

namespace Tailcat.Link;

/// <summary>Why a channel stopped carrying frames.</summary>
public enum ChannelCloseReason
{
    /// <summary>This end closed it.</summary>
    LocalClosed,

    /// <summary>The other end closed it.</summary>
    PeerClosed,

    /// <summary>The session carrying it died. A channel is not resumed.</summary>
    SessionEnded,
}

/// <summary>What a channel is, whichever way it runs.</summary>
/// <remarks>
/// <para>
/// A channel is the third shape this library carries, between a message and a
/// file. <see cref="ILink.RequestAsync"/> is a message: one frame, a round
/// trip, an entry in the ledger. <see cref="ILink.SendAsync"/> is a file:
/// seekable, resumed across sessions, paced by the receiver. A channel is
/// neither — it is the stream of the moment, for audio, telemetry or input
/// events.
/// </para>
/// <para>
/// The contract, stated outright because it is the whole point of it being a
/// third thing: <b>ordered within the channel, and not durable</b>. Frames
/// arrive in the order they were sent, and a channel ends with the session
/// that carries it rather than being resumed on the next one. That is correct
/// rather than a limitation — a frame of audio from ten seconds ago is worth
/// nothing, and resuming one would be worse than dropping it.
/// </para>
/// </remarks>
public interface ILinkChannel : IAsyncDisposable
{
    /// <summary>What the two ends agreed to call this channel.</summary>
    string Name { get; }

    /// <summary>The machine at the other end.</summary>
    ILinkPeer Peer { get; }

    /// <summary>Whether frames can still move.</summary>
    bool IsOpen { get; }

    /// <summary>
    /// Raised once, with why the channel stopped.
    /// </summary>
    /// <remarks>
    /// The reason is what lets an application tell "the peer closed it" from
    /// "the session died", which are the same silence and want different
    /// answers.
    /// </remarks>
    event EventHandler<ChannelClosedEventArgs>? Closed;
}

/// <summary>The sending end of a channel, as <see cref="ILinkPeer.OpenChannelAsync"/> returns it.</summary>
public interface ILinkChannelWriter : ILinkChannel
{
    /// <summary>
    /// Sends one frame, which arrives whole and in order or not at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It returns once the frame has been handed to the transport, which
    /// paces it: a sender faster than the network is held here rather than
    /// filling memory.
    /// </para>
    /// <para>
    /// Cancelling gives up the place in the queue; once the frame has begun
    /// going out it is written to the end regardless. Stopping halfway would
    /// leave part of a frame on the stream the whole channel shares, and every
    /// frame after it would be read at the wrong offset.
    /// </para>
    /// </remarks>
    /// <exception cref="LinkClosedException">If the channel has ended.</exception>
    Task SendAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken = default);
}

/// <summary>The receiving end of a channel, as a <see cref="LinkChannelHandler"/> is given it.</summary>
public interface ILinkChannelReader : ILinkChannel
{
    /// <summary>
    /// Yields frames in the order they were sent, until the channel ends.
    /// </summary>
    /// <remarks>
    /// Reading is what paces the sender, so a handler that falls behind slows
    /// the other machine down rather than losing frames. Each frame is its
    /// own memory and outlives the iteration that produced it.
    /// </remarks>
    IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAllAsync(CancellationToken cancellationToken = default);
}

/// <summary>Why the channel stopped.</summary>
/// <param name="reason">The reason, for code to branch on.</param>
/// <param name="detail">The same thing in words.</param>
public sealed class ChannelClosedEventArgs(ChannelCloseReason reason, string detail) : EventArgs
{
    /// <summary>The reason, for code to branch on.</summary>
    public ChannelCloseReason Reason { get; } = reason;

    /// <summary>The same thing in words.</summary>
    public string Detail { get; } = detail;
}

/// <summary>Takes a channel the peer has opened.</summary>
/// <remarks>
/// The channel ends when the handler returns, so a handler that means to keep
/// receiving must stay inside <see cref="ILinkChannelReader.ReadAllAsync"/>.
/// </remarks>
/// <param name="peer">Which machine opened it.</param>
/// <param name="channel">The frames it is sending.</param>
/// <param name="cancellationToken">Cancelled when the link is closing.</param>
public delegate Task LinkChannelHandler(
    ILinkPeer peer,
    ILinkChannelReader channel,
    CancellationToken cancellationToken);

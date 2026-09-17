// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

namespace Tailcat.Link;

/// <summary>
/// An ordinary two-way <see cref="Stream"/> to a peer: bytes in order both
/// ways, each direction ended on its own, gone with the session that carries it.
/// </summary>
/// <remarks>
/// <para>
/// The fourth shape a link carries, for what already speaks in streams — a TCP
/// connection to tunnel, a protocol with its own framing. It is what a channel
/// pair used to be made to do, with the id matching and the abort marker every
/// application wrote for itself: <c>docs/streams.md</c> is the specification.
/// </para>
/// <para>
/// The contract: <b>ordered, paced by the reader, and not durable</b>, like a
/// channel. Reading returns 0 once the other end has called
/// <see cref="CompleteWritesAsync"/> or disposed its end — a clean end, and the
/// only one. Everything else ends in a <see cref="LinkStreamException"/> whose
/// <see cref="LinkStreamException.Ending"/> says what happened: the other end
/// abandoned it, it closed while this end was still writing, or the session
/// died. Those are different answers for a tunnel — a truncated download must
/// not look like a finished one — which is why a stream that simply stops is
/// never taken for an end.
/// </para>
/// <para>
/// One reader and one writer may use it at once, as with a socket. Disposing
/// it ends this end's writes cleanly and stops reading; <see cref="AbortAsync"/>
/// ends it so the other end is told it did not finish.
/// </para>
/// </remarks>
public abstract class LinkStream : Stream
{
    /// <summary>Only the library makes these; the constructor is not for subclasses elsewhere.</summary>
    private protected LinkStream()
    {
    }

    /// <summary>What the two ends agreed to call this stream.</summary>
    public abstract string Name { get; }

    /// <summary>The machine at the other end.</summary>
    public abstract ILinkPeer Peer { get; }

    /// <summary>
    /// Says there is nothing more to write, while this end can still read: the
    /// half-close of a socket. The other end reads to its end and then gets 0.
    /// </summary>
    /// <remarks>Calling it again does nothing. Writing afterwards is an <see cref="InvalidOperationException"/>.</remarks>
    /// <exception cref="LinkStreamException">If the stream has already ended some other way.</exception>
    public abstract Task CompleteWritesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Abandons the stream both ways. The other end's reads and writes fail
    /// with <see cref="LinkStreamEnding.PeerAborted"/> rather than seeing a clean end.
    /// </summary>
    /// <remarks>
    /// For what a tunnel does when its own connection was reset: a clean end
    /// there would tell the far side that everything arrived. A write in
    /// progress is stopped where it is.
    /// </remarks>
    public abstract Task AbortAsync();
}

/// <summary>How a <see cref="LinkStream"/> ended, when it did not end cleanly.</summary>
public enum LinkStreamEnding
{
    /// <summary>
    /// The other end closed the stream while this end was still writing. What
    /// it wrote before closing was all there was, and it arrived.
    /// </summary>
    PeerClosed,

    /// <summary>
    /// The other end abandoned the stream without finishing it: it aborted, its
    /// handler threw, or its bytes stopped part-way. What was read may be incomplete.
    /// </summary>
    PeerAborted,

    /// <summary>This end aborted it.</summary>
    Aborted,

    /// <summary>The session carrying it died. A stream is not resumed.</summary>
    SessionEnded,
}

/// <summary>A <see cref="LinkStream"/> that stopped without a clean end.</summary>
/// <remarks>
/// An <see cref="IOException"/>, because that is what code written against
/// <see cref="Stream"/> — <see cref="Stream.CopyToAsync(Stream)"/>, a pipe, a
/// protocol reader — already catches.
/// </remarks>
public class LinkStreamException : IOException
{
    /// <summary>Creates an exception with no message, for a session that ended.</summary>
    public LinkStreamException()
    {
    }

    /// <summary>Creates an exception with a message, for a session that ended.</summary>
    public LinkStreamException(string message)
        : base(message)
    {
    }

    /// <summary>Creates an exception with a message and an underlying cause, for a session that ended.</summary>
    public LinkStreamException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates an exception saying how the stream ended.</summary>
    public LinkStreamException(LinkStreamEnding ending, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Ending = ending;
    }

    /// <summary>How the stream ended.</summary>
    public LinkStreamEnding Ending { get; } = LinkStreamEnding.SessionEnded;
}

/// <summary>Takes a stream a peer has opened.</summary>
/// <remarks>
/// The stream ends when the handler returns — disposed, which ends this end's
/// writes cleanly — so a handler that means to keep it stays inside. A handler
/// that throws aborts it instead, and the other end is told it did not finish.
/// </remarks>
/// <param name="peer">Which machine opened it.</param>
/// <param name="stream">The stream, both ways.</param>
/// <param name="cancellationToken">Cancelled when the session carrying it ends.</param>
public delegate Task LinkStreamHandler(ILinkPeer peer, LinkStream stream, CancellationToken cancellationToken);

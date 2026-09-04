// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

namespace Tailcat.Link.Protocol;

/// <summary>
/// One transfer this machine is sending, and how far it has got.
/// </summary>
/// <remarks>
/// It belongs to the link rather than to a session for the same reason
/// <see cref="IncomingTransfer"/> does: the attempt is what a session carries,
/// and the transfer is what survives one. Everything a resumed attempt needs
/// — the id the receiver knows it by, where in the content to seek back to,
/// and when a byte last moved — is here.
/// </remarks>
internal sealed class OutboundTransfer(
    TransferOffer offer,
    Stream content,
    IProgress<TransferProgress>? progress,
    TimeProvider time)
{
    /// <summary>
    /// Where byte zero of the transfer is in the stream it was given.
    /// </summary>
    /// <remarks>
    /// Not always zero: a caller may hand over the rest of a stream it has
    /// already read part of, and a resume must seek back to where the
    /// transfer began rather than to where the stream does.
    /// </remarks>
    private readonly long _origin = content.CanSeek ? content.Position : 0;

    /// <summary>What the receiving machine knows this transfer by.</summary>
    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>What the transfer says about itself.</summary>
    public TransferOffer Offer { get; } = offer;

    /// <summary>Where the content is read from.</summary>
    private Stream Content { get; } = content;

    /// <summary>
    /// How much has been read out of the content but not yet acknowledged.
    /// </summary>
    /// <remarks>
    /// A block is read before it is written, so a session that dies between
    /// the two leaves the content a block ahead of <see cref="Sent"/>. Without
    /// this, a resume that lands exactly on <see cref="Sent"/> would carry on
    /// from where the stream stopped and deliver a file with a hole in it.
    /// </remarks>
    private int _readAhead;

    /// <summary>How much of it has reached the peer.</summary>
    public long Sent { get; private set; }

    /// <summary>When a byte last moved, for telling a slow link from a dead one.</summary>
    public long LastMoved { get; private set; } = time.GetTimestamp();

    /// <summary>How long nothing has moved.</summary>
    public TimeSpan Stalled => time.GetElapsedTime(LastMoved);

    /// <summary>Takes the next block out of the content.</summary>
    /// <returns>How many bytes were read; zero at the end of the content.</returns>
    public async ValueTask<int> ReadAsync(Memory<byte> block, CancellationToken cancellationToken)
    {
        int read = await Content.ReadAsync(block, cancellationToken).ConfigureAwait(false);
        _readAhead = read;
        return read;
    }

    /// <summary>Records a block the peer now has.</summary>
    public void Advance(int bytes)
    {
        _readAhead = 0;
        Sent += bytes;
        LastMoved = time.GetTimestamp();
        progress?.Report(new TransferProgress(Sent, Offer.Length));
    }

    /// <summary>
    /// Puts the content back to where the receiving machine says it got to.
    /// </summary>
    /// <remarks>
    /// The receiver decides, not this end: it is the one that knows which
    /// blocks made it out of a session that then died.
    /// </remarks>
    /// <exception cref="LinkException">
    /// If the content cannot be rewound. A transfer from a stream that only
    /// goes forwards — a socket, a pipe, anything being generated as it is
    /// sent — cannot survive a reconnection, and says so rather than
    /// delivering something with a hole in it.
    /// </exception>
    public void RewindTo(long offset)
    {
        if (offset == Sent && _readAhead == 0)
        {
            return;
        }
        if (!Content.CanSeek)
        {
            throw new LinkException(
                $"the transfer has to start again at byte {offset}, and its content cannot be rewound; "
                + "send from a file or an array to survive a reconnection");
        }
        Content.Position = _origin + offset;
        Sent = offset;
        _readAhead = 0;
        LastMoved = time.GetTimestamp();
    }
}

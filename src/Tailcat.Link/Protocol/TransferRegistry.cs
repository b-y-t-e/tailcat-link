// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Text;

namespace Tailcat.Link.Protocol;

/// <summary>
/// The transfers this machine is receiving, which outlive the sessions that
/// carry them.
/// </summary>
/// <remarks>
/// This is the whole of what makes a transfer survive a reconnection. A
/// session knows only how to move blocks; what has arrived, how much of it,
/// and the handler halfway through reading it all live here, on the link, so
/// a transfer that comes back on a fresh session picks up mid-file rather
/// than starting a second one.
/// </remarks>
internal sealed class TransferRegistry(
    Func<LinkTransferHandler?> handler,
    TimeSpan retention,
    TimeProvider time,
    CancellationToken linkClosed)
{
    private readonly Dictionary<Guid, IncomingTransfer> _transfers = [];
    private readonly Lock _mu = new();

    /// <summary>
    /// Takes one session's delivery of a transfer, starting it if this is the
    /// first the machine has heard of it.
    /// </summary>
    public async Task DeliverAsync(
        Guid id,
        ReadOnlyMemory<byte> offer,
        Stream stream,
        CancellationToken cancellationToken)
    {
        LinkTransferHandler? receive = handler();
        if (receive is null)
        {
            // Refused rather than left to time out, and refused as an answer
            // rather than an error, so the sender stops instead of retrying
            // into a machine that will never take it.
            await LinkFrame.WriteAsync(
                stream,
                (byte)LinkFrameStatus.Failed,
                id,
                Encoding.UTF8.GetBytes("the other machine is not receiving transfers"),
                idle: null,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        IncomingTransfer transfer;
        lock (_mu)
        {
            if (!_transfers.TryGetValue(id, out IncomingTransfer? known))
            {
                known = new IncomingTransfer(
                    id, TransferFrame.DecodeOffer(offer.Span), time, retention, () => Forget(id));
                _transfers[id] = known;
                known.Start(receive, linkClosed);
            }
            transfer = known;
        }

        await transfer.DeliverAsync(stream, id, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Ends every transfer still in flight, so a handler blocked on one is
    /// released rather than left waiting for a link that is closing.
    /// </summary>
    public void ExpireAll()
    {
        IncomingTransfer[] pending;
        lock (_mu)
        {
            pending = [.. _transfers.Values];
        }
        foreach (IncomingTransfer transfer in pending)
        {
            transfer.Expire();
        }
    }

    private void Forget(Guid id)
    {
        lock (_mu)
        {
            _transfers.Remove(id);
        }
    }
}

// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Text;

namespace Tailcat.Link.Protocol;

/// <summary>
/// The exchanges this machine is receiving, which outlive the sessions that
/// carry them.
/// </summary>
/// <remarks>
/// This is the whole of what makes an exchange survive a reconnection. A
/// session knows only how to move blocks; what has arrived, the handler
/// reading it, and the answer it gave all live here, on the link, so an
/// exchange that comes back on a fresh session picks up where it stopped
/// rather than starting a second one.
/// </remarks>
/// <param name="requests">What answers requests and notifications, read on every arrival.</param>
/// <param name="transfers">What takes transfers, read on every arrival.</param>
/// <param name="retention">How long an exchange nobody comes back for is kept.</param>
/// <param name="ackPatience">How long to wait for a sender to say it has the whole answer.</param>
/// <param name="time">The clock both are measured on.</param>
/// <param name="linkClosed">The lifetime every handler is given.</param>
internal sealed class ExchangeRegistry(
    Func<LinkContentHandler?> requests,
    Func<LinkTransferHandler?> transfers,
    TimeSpan retention,
    TimeSpan ackPatience,
    TimeProvider time,
    CancellationToken linkClosed)
{
    private readonly Dictionary<Guid, IncomingExchange> _exchanges = [];
    private readonly Lock _mu = new();

    /// <summary>Takes one session's attempt at an exchange.</summary>
    public Task DeliverAsync(Guid id, ReadOnlyMemory<byte> header, Stream stream, CancellationToken cancellationToken) =>
        DeliverAsync(id, ExchangeFrame.DecodeHeader(header.Span), stream, cancellationToken);

    /// <summary>
    /// Ends every exchange still in flight, so a handler blocked on one is
    /// released rather than left waiting for a link that is closing.
    /// </summary>
    public void ExpireAll()
    {
        IncomingExchange[] pending;
        lock (_mu)
        {
            pending = [.. _exchanges.Values];
        }
        foreach (IncomingExchange exchange in pending)
        {
            exchange.Expire();
        }
    }

    private async Task DeliverAsync(
        Guid id,
        ExchangeHeader header,
        Stream stream,
        CancellationToken cancellationToken)
    {
        bool transfer = header.Flags.HasFlag(ExchangeFlags.Transfer);
        Func<IncomingTransfer, CancellationToken, Task<LinkContent?>>? handler = HandlerFor(transfer);
        if (handler is null)
        {
            // Refused rather than left to time out, and refused as an answer
            // rather than an error, so the sender stops instead of retrying
            // into a machine that will never take it. Content already on its
            // way is read first, or the refusal would sit behind it unread.
            if (header.Flags.HasFlag(ExchangeFlags.Pipelined))
            {
                await TransferFrame.DrainAsync(stream, cancellationToken).ConfigureAwait(false);
            }
            string reason = transfer
                ? "the other machine is not receiving transfers"
                : "the other machine is not handling requests";
            await LinkFrame.WriteAsync(
                stream, (byte)LinkFrameStatus.Failed, id, Encoding.UTF8.GetBytes(reason), idle: null, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        IncomingExchange exchange;
        lock (_mu)
        {
            if (!_exchanges.TryGetValue(id, out IncomingExchange? known))
            {
                IncomingExchange? created = null;
                created = new IncomingExchange(id, header, time, retention, ackPatience, () => Forget(id, created!));
                _exchanges[id] = created;
                created.Start(handler, linkClosed);
                known = created;
            }
            exchange = known;
        }

        await exchange.DeliverAsync(stream, header, cancellationToken).ConfigureAwait(false);
    }

    private Func<IncomingTransfer, CancellationToken, Task<LinkContent?>>? HandlerFor(bool transfer)
    {
        if (transfer)
        {
            return transfers() is { } take
                ? async (content, ct) =>
                {
                    await take(content, ct).ConfigureAwait(false);
                    return null;
                }
                : null;
        }
        return requests() is { } answer
            ? async (content, ct) => await answer(content, ct).ConfigureAwait(false)
            : null;
    }

    private void Forget(Guid id, IncomingExchange exchange)
    {
        lock (_mu)
        {
            // Only that one: an id forgotten and then sent again is a new
            // exchange under the same key, and must not be dropped with it.
            if (_exchanges.TryGetValue(id, out IncomingExchange? held) && ReferenceEquals(held, exchange))
            {
                _exchanges.Remove(id);
            }
        }
    }
}

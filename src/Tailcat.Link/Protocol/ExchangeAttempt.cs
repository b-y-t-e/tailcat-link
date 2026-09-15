// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Text;

namespace Tailcat.Link.Protocol;

/// <summary>What one attempt at an exchange needs of the session carrying it.</summary>
internal interface IExchangeCarrier
{
    /// <summary>Cancelled the moment the session stops carrying anything.</summary>
    CancellationToken Alive { get; }

    /// <summary>Opens a stream whose movement counts towards the session being alive.</summary>
    Task<Stream> OpenStreamAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Ends the session for a failed attempt, unless what failed was one
    /// exchange's <paramref name="silence"/> on a session still moving bytes.
    /// </summary>
    void FailUnlessBusy(string reason, bool silence);
}

/// <summary>
/// Moves as much of an exchange, in both directions, as one session lives long
/// enough to move.
/// </summary>
/// <remarks>
/// <para>
/// One attempt, not the whole exchange. The header says how much of the answer
/// this end already has; the other machine says how much of the content it
/// already has; both carry on from there. A failure here is the caller's cue to
/// try again on the next session.
/// </para>
/// <para>
/// For a request, this returns once the whole answer is here. It hands the
/// answer to the caller as soon as its header arrives, so what is waited for
/// past that point is the caller reading it — and that is not the peer being
/// silent, so only the network reads are timed.
/// </para>
/// </remarks>
internal sealed class ExchangeAttempt
{
    private readonly IExchangeCarrier _session;
    private readonly TimeProvider _time;

    public ExchangeAttempt(IExchangeCarrier session, TimeProvider time)
    {
        _session = session;
        _time = time;
    }

    /// <exception cref="RemoteHandlerException">
    /// If the peer refused the exchange or its handler threw. Neither is worth
    /// retrying.
    /// </exception>
    /// <exception cref="LinkException">
    /// If this attempt failed. A handler that did not answer within the
    /// exchange's patience fails it without condemning the session.
    /// </exception>
    public async Task RunAsync(OutboundExchange exchange, CancellationToken cancellationToken)
    {
        using IdleTimeout idle = new(exchange.Patience, _time);
        try
        {
            // Inside the try on purpose: a session disposed by the supervisor
            // makes reading its token throw, and that must be retried on the
            // next session rather than reach the application.
            using CancellationTokenSource alive =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _session.Alive);
            using CancellationTokenSource moving =
                CancellationTokenSource.CreateLinkedTokenSource(alive.Token, idle.Token);

            Stream stream = await _session.OpenStreamAsync(moving.Token).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                ExchangeHeader header = await SendContentAsync(stream, exchange, idle, moving.Token).ConfigureAwait(false);
                await ReceiveOutcomeAsync(stream, exchange, header, idle, moving.Token, alive.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (SessionFailure.EndsTheSession(ex) && !cancellationToken.IsCancellationRequested)
        {
            string reason = idle.Expired
                ? $"the exchange moved nothing for {exchange.Patience}"
                : ex.Message;
            _session.FailUnlessBusy(reason, idle.Expired);
            throw new LinkException(reason, ex);
        }
    }

    /// <summary>
    /// Sends the header and the content from wherever the other machine says
    /// it got to — or all of it at once, when it could only have said zero.
    /// </summary>
    private static async Task<ExchangeHeader> SendContentAsync(
        Stream stream,
        OutboundExchange exchange,
        IdleTimeout idle,
        CancellationToken moving)
    {
        bool pipelined = exchange.TakeFirstAttempt();
        ExchangeHeader header = exchange.HeaderFor(pipelined);
        idle.Restart();
        await LinkFrame.WriteAsync(
            stream,
            (byte)LinkFrameKind.Exchange,
            exchange.Id,
            ExchangeFrame.EncodeHeader(header),
            idle,
            moving).ConfigureAwait(false);
        if (pipelined)
        {
            await TransferFrame.WriteContentAsync(stream, exchange.Cursor, idle, moving).ConfigureAwait(false);
        }

        (byte taken, _, byte[] where) = await LinkFrame.ReadAsync(stream, idle, moving).ConfigureAwait(false);
        ThrowIfRefused(taken, where, exchange.Flags);
        long offset = TransferFrame.DecodeOffset(where, exchange.Content.Length);
        if (!pipelined)
        {
            exchange.Cursor.RewindTo(offset);
            await TransferFrame.WriteContentAsync(stream, exchange.Cursor, idle, moving).ConfigureAwait(false);
        }
        return header;
    }

    private async Task ReceiveOutcomeAsync(
        Stream stream,
        OutboundExchange exchange,
        ExchangeHeader header,
        IdleTimeout idle,
        CancellationToken moving,
        CancellationToken alive)
    {
        if (exchange.Flags.HasFlag(ExchangeFlags.Answer))
        {
            await ReceiveAnswerAsync(stream, exchange, header.AnswerOffset, alive).ConfigureAwait(false);
            return;
        }

        // Without the idle bound unless the exchange asked to be told on
        // delivery: otherwise what is waited for is the receiving application's
        // handler finishing with the content, and a handler is allowed to take
        // as long as it takes. A peer that has gone away is the heartbeat's
        // business.
        (byte done, _, byte[] reason) = exchange.Flags.HasFlag(ExchangeFlags.AckOnDelivery)
            ? await LinkFrame.ReadAsync(stream, idle, moving).ConfigureAwait(false)
            : await LinkFrame.ReadAsync(stream, idle: null, alive).ConfigureAwait(false);
        ThrowIfRefused(done, reason, exchange.Flags);
        await AcknowledgeAsync(stream, exchange, alive).ConfigureAwait(false);
    }

    private async Task ReceiveAnswerAsync(
        Stream stream,
        OutboundExchange exchange,
        long answerOffset,
        CancellationToken alive)
    {
        // The handler is working, which is not the peer being silent, so the
        // wait is bounded without condemning a session every other exchange
        // may be sharing. Deciding that the peer is gone is the heartbeat's job.
        byte status;
        byte[] header;
        using (IdleTimeout handling = new(exchange.Patience, _time))
        using (CancellationTokenSource waiting = CancellationTokenSource.CreateLinkedTokenSource(alive, handling.Token))
        {
            try
            {
                (status, _, header) = await LinkFrame.ReadAsync(stream, idle: null, waiting.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (handling.Expired && !alive.IsCancellationRequested)
            {
                throw new LinkException($"no answer within {exchange.Patience}", ex);
            }
        }
        ThrowIfRefused(status, header, exchange.Flags);

        IncomingTransfer answer = exchange.StartAnswer(ExchangeFrame.DecodeAnswer(header));
        await answer.ReadBodyAsync(stream, answerOffset, silence: exchange.Patience, alive).ConfigureAwait(false);
        await AcknowledgeAsync(stream, exchange, alive).ConfigureAwait(false);
    }

    /// <summary>
    /// Tells the other machine this end has the outcome, so it can let the
    /// exchange go now rather than at the end of the retention window.
    /// </summary>
    /// <remarks>
    /// Nothing is lost if it never arrives, and nothing is retried for it: the
    /// outcome is already here, and a retry would reach a machine that may have
    /// forgotten the exchange and would run its handler again.
    /// </remarks>
    private static async Task AcknowledgeAsync(Stream stream, OutboundExchange exchange, CancellationToken alive)
    {
        try
        {
            await LinkFrame.WriteAsync(stream, (byte)LinkFrameStatus.Ok, exchange.Id, default, idle: null, alive)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (SessionFailure.EndsTheSession(ex))
        {
        }
    }

    private static void ThrowIfRefused(byte status, byte[] answer, ExchangeFlags flags)
    {
        if (status != (byte)LinkFrameStatus.Failed)
        {
            return;
        }
        string what = flags.HasFlag(ExchangeFlags.Transfer)
            ? "would not take the transfer"
            : flags.HasFlag(ExchangeFlags.Answer)
                ? "could not answer"
                : "would not take the notification";
        throw new RemoteHandlerException($"the other machine {what}: {Encoding.UTF8.GetString(answer)}");
    }
}

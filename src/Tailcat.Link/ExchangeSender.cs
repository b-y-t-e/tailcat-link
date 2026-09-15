// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using Tailcat.Link.Protocol;

namespace Tailcat.Link;

/// <summary>
/// Where an exchange finds the session it goes on, and learns that one ended.
/// </summary>
/// <remarks>
/// What a sender needs of a peer and nothing more, so that how sessions are
/// kept up and how exchanges ride on them change for their own reasons.
/// </remarks>
internal interface IPeerSessions
{
    /// <summary>The session that is up, as soon as there is one.</summary>
    Task<LinkSession> CurrentSessionAsync(CancellationToken cancellationToken);

    /// <summary>Completes when the session up now, or the next one, ends.</summary>
    Task CurrentSessionEnded();

    /// <summary>Cancelled once the peer is disposed.</summary>
    CancellationToken Stopping { get; }
}

/// <summary>
/// Carries exchanges to one peer across as many sessions as they take, each in
/// the shape that peer's version can take.
/// </summary>
internal sealed class ExchangeSender
{
    private readonly IPeerSessions _sessions;
    private readonly LinkOptions _options;

    public ExchangeSender(IPeerSessions sessions, LinkOptions options)
    {
        _sessions = sessions;
        _options = options;
    }

    /// <summary>
    /// Starts a request and returns its answer as soon as the answer begins,
    /// leaving the rest to arrive in the background.
    /// </summary>
    public async Task<IncomingTransfer> StartRequestAsync(
        LinkContent request,
        TimeSpan patience,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        OutboundExchange exchange = new(request, ExchangeFlags.Answer, patience, _options.TimeProvider);
        // The caller's token covers the whole exchange, the answer included,
        // and so does disposing the answer: either one stops it.
        CancellationTokenSource lifetime =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _sessions.Stopping);
        Task? running = null;
        exchange.AnswerReleased = async () =>
        {
            // An answer that has all arrived is not abandoned: what is left is
            // telling the other machine so, and cancelling that would leave
            // the whole answer held there until its retention ran out.
            if (running is { IsCompleted: false } && exchange.Answer is not { BodyEnded: true })
            {
                try
                {
                    await lifetime.CancelAsync().ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                    // It finished between the check and here.
                }
            }
            if (running is not null)
            {
                try
                {
                    await running.ConfigureAwait(false);
                }
#pragma warning disable CA1031 // Whatever ended it has already reached the answer, which is what the caller is letting go of.
                catch (Exception)
#pragma warning restore CA1031
                {
                }
            }
        };
        running = RunRequestAsync(exchange, lifetime);

        Task first = await Task.WhenAny(exchange.AnswerStarted, running).ConfigureAwait(false);
        if (first == running)
        {
            // Over before an answer began: this is what it ended with.
            await running.ConfigureAwait(false);
        }
        return await exchange.AnswerStarted.ConfigureAwait(false);
    }

    /// <summary>Sends an exchange nothing waits on an answer for.</summary>
    public async Task DeliverAsync(
        LinkContent content,
        ExchangeFlags flags,
        TimeSpan patience,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        OutboundExchange exchange = new(content, flags, patience, _options.TimeProvider);
        try
        {
            await RunExchangeAsync(exchange, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await exchange.ReleaseContentAsync().ConfigureAwait(false);
        }
    }

    private async Task RunRequestAsync(OutboundExchange exchange, CancellationTokenSource lifetime)
    {
        try
        {
            await RunExchangeAsync(exchange, lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (exchange.Answer is { } answer)
        {
            // The caller already holds the answer and is reading it; this is
            // how it hears that the rest is not coming.
            answer.Fail(ex);
        }
        finally
        {
            await exchange.ReleaseContentAsync().ConfigureAwait(false);
            lifetime.Dispose();
        }
    }

    /// <summary>
    /// Carries one exchange across as many sessions as it takes, for as long
    /// as something keeps moving.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately without a deadline on the whole: twenty gigabytes through a
    /// relay is hours, and any total limit would be a limit on how large
    /// content this library can send. What is bounded is silence — the
    /// exchange's patience — and waiting for a session counts as silence too,
    /// so an exchange to a machine that has gone for good ends in bounded time
    /// instead of waiting for it for ever.
    /// </para>
    /// <para>
    /// Each machine is spoken to the way it said it can be.
    /// </para>
    /// </remarks>
    private async Task RunExchangeAsync(OutboundExchange exchange, CancellationToken cancellationToken)
    {
        using CancellationTokenSource running =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _sessions.Stopping);
        Exception? last = null;
        while (true)
        {
            TimeSpan left = exchange.Patience - exchange.Stalled;
            if (left <= TimeSpan.Zero)
            {
                break;
            }

            // Taken before the attempt: a session that dies during it is
            // reported by the supervisor afterwards, and this still catches it.
            Task ended = _sessions.CurrentSessionEnded();
            try
            {
                if (await ReadyWithinAsync(left, running.Token).ConfigureAwait(false) is not var (session, peer))
                {
                    break;
                }

                await SendInTheShapeTakenAsync(session, peer, exchange, running.Token).ConfigureAwait(false);
                return;
            }
            catch (RemoteHandlerException)
            {
                // The other machine has decided about this exchange. Sending it
                // again would reach the same decision.
                throw;
            }
            catch (LinkException ex) when (!running.IsCancellationRequested)
            {
                if (exchange.OnlyOnce)
                {
                    throw;
                }
                last = ex;
            }
            catch (OperationCanceledException) when (running.IsCancellationRequested)
            {
                break;
            }

            if (exchange.CannotResume)
            {
                // Nothing can be resumed from a stream that only goes forwards,
                // and the other machine will ask for an offset that is now
                // behind this one. Said plainly rather than retried into the
                // same wall until the patience runs out.
                throw new LinkException(
                    $"the content stopped after {exchange.Cursor.Sent} bytes and cannot be rewound; "
                    + "send from a file, bytes or a seekable stream to survive a reconnection",
                    last);
            }

            // Never straight round again. Usually the session is already
            // ending — a failed attempt condemns it — and waiting for that is
            // what stops the next attempt from spinning on a connection that is
            // still being torn down; the pause bounds the case where the
            // session survived whatever the attempt ran into.
            try
            {
                await ended.WaitAsync(_options.MinReconnectDelay, _options.TimeProvider, running.Token)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
            }
            catch (OperationCanceledException) when (running.IsCancellationRequested)
            {
                break;
            }
        }

        // After the two below, so that a caller who changed its mind is not
        // counted as a peer that went silent.
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_sessions.Stopping.IsCancellationRequested, _sessions);
        throw new LinkTimeoutException(
            exchange.Flags.HasFlag(ExchangeFlags.Transfer)
                ? $"the transfer moved nothing for {exchange.Patience}, after {exchange.Cursor.Sent} bytes"
                : exchange.Flags.HasFlag(ExchangeFlags.Answer)
                    ? $"no answer within {exchange.Patience}"
                    : $"the notification moved nothing for {exchange.Patience}",
            last);
    }

    /// <summary>
    /// The current session and what its other end can take, or null if they
    /// do not both arrive within <paramref name="left"/>.
    /// </summary>
    /// <remarks>
    /// Asking what a machine can take is a ping, and on a link saturated by
    /// another exchange a ping can be outpaced for as long as that exchange
    /// keeps moving. Waiting for it is silence for this exchange all the same,
    /// so it is bounded by the same patience as waiting for a session.
    /// </remarks>
    private async Task<(LinkSession Session, PeerCapabilities Peer)?> ReadyWithinAsync(
        TimeSpan left,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource patience = new(left, _options.TimeProvider);
        using CancellationTokenSource either =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, patience.Token);
        try
        {
            LinkSession session = await _sessions.CurrentSessionAsync(either.Token).ConfigureAwait(false);
            return (session, await session.CapabilitiesAsync(either.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (patience.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>One attempt, in the shape the other machine said it takes.</summary>
    /// <exception cref="RemoteHandlerException">If it takes no shape this build sends.</exception>
    private Task SendInTheShapeTakenAsync(
        LinkSession session,
        PeerCapabilities peer,
        OutboundExchange exchange,
        CancellationToken cancellationToken)
    {
        if (peer.HasFlag(PeerCapabilities.Exchanges))
        {
            return new ExchangeAttempt(session, _options.TimeProvider).RunAsync(exchange, cancellationToken);
        }
        if (peer.HasFlag(PeerCapabilities.LargeFrames))
        {
            return SendAsFramesAsync(session, exchange, cancellationToken);
        }
        throw new RemoteHandlerException("the other machine says it takes neither exchanges nor frames");
    }

    /// <summary>
    /// Sends an exchange as the single frames a machine without exchanges —
    /// the browser client — takes.
    /// </summary>
    private static async Task SendAsFramesAsync(
        LinkSession session,
        OutboundExchange exchange,
        CancellationToken cancellationToken)
    {
        if (exchange.Flags.HasFlag(ExchangeFlags.Transfer))
        {
            throw new RemoteHandlerException("the other machine does not take transfers");
        }

        // Before reading, when the length is known: a gigabyte read into memory
        // only to be refused is what this check is there to spare.
        if (exchange.Content.Length is long announced)
        {
            EnsureFitsOneFrame(announced);
        }
        ReadOnlyMemory<byte> whole = await exchange.WholeContentAsync(cancellationToken).ConfigureAwait(false);
        EnsureFitsOneFrame(whole.Length);

        if (exchange.Flags.HasFlag(ExchangeFlags.Answer))
        {
            byte[] answer = await session.RequestAsync(exchange.Id, whole, cancellationToken).ConfigureAwait(false);
            exchange.TakeWholeAnswer(answer);
            return;
        }

        // That machine cannot recognise a notification it has already had,
        // so it gets one attempt.
        exchange.OnlyOnce = true;
        await session.NotifyAsync(whole, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Refuses a message a machine without exchanges cannot take in one frame.
    /// </summary>
    /// <remarks>
    /// Such a machine takes a message as one array, so past what an array holds
    /// it is refused — said here rather than left to overflow on the way into
    /// memory.
    /// </remarks>
    private static void EnsureFitsOneFrame(long length)
    {
        if (length > Array.MaxLength)
        {
            throw new RemoteHandlerException(
                $"the other machine takes at most {Array.MaxLength} bytes in one message, "
                + $"and this one is {length}; send less");
        }
    }
}

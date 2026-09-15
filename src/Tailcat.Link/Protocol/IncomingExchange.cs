// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Text;

namespace Tailcat.Link.Protocol;

/// <summary>What a handler made of an exchange: a refusal, or an answer.</summary>
/// <param name="Refusal">Why the handler failed, or null if it did not.</param>
/// <param name="Answer">What it answered with, when anything is waiting for an answer.</param>
internal readonly record struct ExchangeOutcome(string? Refusal, LinkContent? Answer);

/// <summary>
/// One exchange this machine is receiving, which outlives the sessions that
/// carry it.
/// </summary>
/// <remarks>
/// <para>
/// Everything that has to survive a session dying lives here, on the link: the
/// content received so far, the handler already reading it, and the answer it
/// gave. A session only moves blocks. So an exchange that comes back on a fresh
/// session carries on mid-content into the same handler, and a sender whose
/// session died after the handler finished is answered rather than having the
/// handler run a second time.
/// </para>
/// <para>
/// It is let go of when the sender says it has the whole answer, or when nobody
/// has come back for it within the retention window.
/// </para>
/// </remarks>
internal sealed class IncomingExchange
{
    private readonly Guid _id;
    private readonly ExchangeFlags _flags;
    private readonly TimeSpan _retention;
    private readonly TimeSpan _ackPatience;
    private readonly TimeProvider _time;
    private readonly Action _forget;
    private readonly ITimer _expiry;
    private readonly Lock _mu = new();

    private Task<ExchangeOutcome>? _handler;
    private CancellationTokenSource? _delivering;
    private Task _letGo = Task.CompletedTask;
    private OutboundTransfer? _answer;
    private Stream? _answerStream;
    private bool _answerOwned;
    private AnswerHeader _answerHeader;
    private bool _expired;
    private int _finished;

    /// <param name="id">What the sender knows the exchange by.</param>
    /// <param name="header">The header of the first attempt to arrive.</param>
    /// <param name="time">The clock the windows below are measured on.</param>
    /// <param name="retention">How long to wait for a sender to come back.</param>
    /// <param name="ackPatience">
    /// How long to wait for the sender to say it has the whole answer. Not
    /// hearing it only means the answer is kept for the retention window
    /// instead of being let go of at once.
    /// </param>
    /// <param name="forget">Takes the exchange out of whatever is holding it.</param>
    public IncomingExchange(
        Guid id,
        ExchangeHeader header,
        TimeProvider time,
        TimeSpan retention,
        TimeSpan ackPatience,
        Action forget)
    {
        _id = id;
        _flags = header.Flags;
        _retention = retention;
        _ackPatience = ackPatience;
        _time = time;
        _forget = forget;
        Request = new IncomingTransfer(id, header.Name, header.ContentType, header.Length, header.Metadata, time);
        _expiry = time.CreateTimer(_ => Expire(), null, retention, Timeout.InfiniteTimeSpan);
    }

    /// <summary>The content, as the handler reads it.</summary>
    public IncomingTransfer Request { get; }

    /// <summary>Starts the handler, once per exchange.</summary>
    /// <param name="handler">What the application does with the content.</param>
    /// <param name="linkClosed">
    /// The handler's lifetime, which is the link's rather than the session's:
    /// a session dropping mid-content is what resuming is for, and cancelling
    /// the handler for it would throw away everything read so far.
    /// </param>
    public void Start(Func<IncomingTransfer, CancellationToken, Task<LinkContent?>> handler, CancellationToken linkClosed)
    {
        lock (_mu)
        {
            _handler ??= Task.Run(() => RunAsync(handler, linkClosed), CancellationToken.None);
        }
    }

    /// <summary>
    /// Takes one session's attempt at the exchange: the content from wherever
    /// the last attempt got to, and the answer from wherever the sender says it
    /// got to.
    /// </summary>
    /// <param name="stream">The attempt's stream.</param>
    /// <param name="header">What the attempt opened with.</param>
    /// <param name="cancellationToken">Gives up on this attempt.</param>
    public async Task DeliverAsync(
        Stream stream,
        ExchangeHeader header,
        CancellationToken cancellationToken)
    {
        (CancellationTokenSource mine, TaskCompletionSource letGo) = await ClaimAsync(cancellationToken).ConfigureAwait(false);
        bool answered = false;
        try
        {
            using CancellationTokenSource attempt =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, mine.Token);
            answered = await DeliverClaimedAsync(stream, header, attempt.Token).ConfigureAwait(false);
        }
        finally
        {
            Release(mine, letGo, answered);
        }
    }

    /// <summary>
    /// Forgets the exchange: one nobody came back for, one whose answer has
    /// been taken, or one on a link that is closing.
    /// </summary>
    public void Expire()
    {
        CancellationTokenSource? active;
        lock (_mu)
        {
            if (_expired)
            {
                return;
            }
            _expired = true;
            active = _delivering;
        }
        _forget();
        _expiry.Dispose();

        if (active is null)
        {
            Finish();
            return;
        }

        // Something is still delivering. It lets go when cancelled, and it is
        // the one to finish, because it owns the writing until then.
        try
        {
            active.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // It let go between the lock above and here, saw the exchange
            // expired, and has finished already.
        }
    }

    private async Task<bool> DeliverClaimedAsync(
        Stream stream,
        ExchangeHeader header,
        CancellationToken token)
    {
        bool pipelined = header.Flags.HasFlag(ExchangeFlags.Pipelined);
        if (pipelined)
        {
            // Sent before this end was asked where to start, which only ever
            // happens from the very beginning; what overlaps is dropped.
            await Request.ReadBodyAsync(stream, startsAt: 0, silence: null, token).ConfigureAwait(false);
        }

        long offset = Request.BytesReceived;
        await WriteOkAsync(stream, TransferFrame.EncodeOffset(offset), token).ConfigureAwait(false);
        if (!pipelined)
        {
            await Request.ReadBodyAsync(stream, offset, silence: null, token).ConfigureAwait(false);
        }

        if (header.Flags.HasFlag(ExchangeFlags.AckOnDelivery))
        {
            // A notification: the sender is waiting for nothing the handler
            // does, so it is told the content arrived and goes on its way.
            await WriteOkAsync(stream, TransferFrame.EncodeOffset(Request.BytesReceived), token).ConfigureAwait(false);
            return await AcknowledgedAsync(stream, token).ConfigureAwait(false);
        }

        ExchangeOutcome outcome = await Outcome().WaitAsync(token).ConfigureAwait(false);
        if (outcome.Refusal is { } reason)
        {
            await WriteFailedAsync(stream, reason, token).ConfigureAwait(false);
            return false;
        }
        if (!header.Flags.HasFlag(ExchangeFlags.Answer))
        {
            await WriteOkAsync(stream, TransferFrame.EncodeOffset(Request.BytesReceived), token).ConfigureAwait(false);
            return await AcknowledgedAsync(stream, token).ConfigureAwait(false);
        }

        OutboundTransfer answer;
        try
        {
            answer = OpenAnswer(outcome.Answer ?? LinkContent.Empty);
            answer.RewindTo(header.AnswerOffset);
        }
        catch (LinkException ex)
        {
            // An answer that cannot be read again from where the sender got to
            // is over for good, and said so rather than sent with a hole in it.
            await WriteFailedAsync(stream, ex.Message, token).ConfigureAwait(false);
            return false;
        }

        await WriteOkAsync(stream, ExchangeFrame.EncodeAnswer(_answerHeader), token).ConfigureAwait(false);
        await TransferFrame.WriteContentAsync(stream, answer, idle: null, token).ConfigureAwait(false);
        return await AcknowledgedAsync(stream, token).ConfigureAwait(false);
    }

    /// <summary>Whether the sender says it has the outcome.</summary>
    /// <remarks>
    /// Only a sender that says so lets the exchange go at once. One that never
    /// does — gone, or a session that died on the last write — leaves it for
    /// the retention window, in case it comes back. Without this every
    /// notification would be held for ten minutes after it was delivered, and
    /// an application sending a few a second would hold thousands.
    /// </remarks>
    private async Task<bool> AcknowledgedAsync(Stream stream, CancellationToken token)
    {
        using CancellationTokenSource patience = new(_ackPatience, _time);
        using CancellationTokenSource waiting =
            CancellationTokenSource.CreateLinkedTokenSource(token, patience.Token);
        try
        {
            (byte status, _, _) = await LinkFrame
                .ReadAsync(stream, limit: 0, idle: null, waiting.Token).ConfigureAwait(false);
            return status == (byte)LinkFrameStatus.Ok;
        }
        catch (Exception ex) when (
            (SessionFailure.EndsTheSession(ex) || ex is LinkException) && !token.IsCancellationRequested)
        {
            return false;
        }
    }

    private Task<ExchangeOutcome> Outcome()
    {
        lock (_mu)
        {
            return _handler ?? throw new LinkException("the exchange was never started");
        }
    }

    private async Task<ExchangeOutcome> RunAsync(
        Func<IncomingTransfer, CancellationToken, Task<LinkContent?>> handler,
        CancellationToken linkClosed)
    {
        try
        {
            LinkContent? answer = await handler(Request, linkClosed).ConfigureAwait(false);
            await Request.CompleteReaderAsync(error: null).ConfigureAwait(false);
            if (!_flags.HasFlag(ExchangeFlags.Answer) && answer is not null)
            {
                // Nobody is waiting for it, so nobody else will close it.
                await answer.ReleaseAsync().ConfigureAwait(false);
                answer = null;
            }
            return new ExchangeOutcome(Refusal: null, answer);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The application refused it. That is an answer, and the sender
            // hears it rather than retrying something that would fail again.
            await Request.CompleteReaderAsync(ex).ConfigureAwait(false);
            return new ExchangeOutcome(ex.Message, Answer: null);
        }
    }

    /// <summary>
    /// Makes this attempt the one delivering the exchange, taking over from any
    /// attempt that is still unwinding.
    /// </summary>
    /// <remarks>
    /// The newest attempt wins, because the sender runs one attempt at a time:
    /// a new one means the old one is abandoned, usually on a session that has
    /// died and not yet noticed. Refusing the newcomer, as transfers used to,
    /// failed the fresh session too and sent both ends round again.
    /// </remarks>
    private async Task<(CancellationTokenSource Mine, TaskCompletionSource LetGo)> ClaimAsync(CancellationToken cancellationToken)
    {
        CancellationTokenSource mine = new();
        TaskCompletionSource letGo = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenSource? previous;
        Task previousLetGo;
        lock (_mu)
        {
            if (_expired)
            {
                mine.Dispose();
                throw new LinkException($"the exchange was not resumed within {_retention}");
            }
            previous = _delivering;
            previousLetGo = _letGo;
            _delivering = mine;
            _letGo = letGo.Task;
            _expiry.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        if (previous is not null)
        {
            // Superseded, so nothing else will dispose it: its own release saw
            // that it no longer held the exchange.
            await previous.CancelAsync().ConfigureAwait(false);
        }
        try
        {
            await previousLetGo.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Release(mine, letGo, answered: false);
            throw;
        }
        finally
        {
            previous?.Dispose();
        }
        return (mine, letGo);
    }

    private void Release(CancellationTokenSource mine, TaskCompletionSource letGo, bool answered)
    {
        bool held;
        bool expired;
        lock (_mu)
        {
            held = ReferenceEquals(_delivering, mine);
            if (held)
            {
                _delivering = null;
            }
            expired = _expired;
            if (held && !expired && !answered)
            {
                _expiry.Change(_retention, Timeout.InfiniteTimeSpan);
            }
        }
        letGo.TrySetResult();
        if (held)
        {
            mine.Dispose();
        }

        if (answered)
        {
            Expire();
        }
        else if (held && expired)
        {
            Finish();
        }
    }

    private OutboundTransfer OpenAnswer(LinkContent content)
    {
        if (_answer is null)
        {
            (Stream stream, bool owned) = content.Open();
            _answerStream = stream;
            _answerOwned = owned;
            _answerHeader = new AnswerHeader(content.Length, content.ContentType, content.Metadata);
            _answer = new OutboundTransfer(new TransferOffer { Length = content.Length }, stream, progress: null, _time);
        }
        return _answer;
    }

    /// <summary>Lets go of everything, once, with nothing delivering.</summary>
    private void Finish()
    {
        if (Interlocked.Exchange(ref _finished, 1) != 0)
        {
            return;
        }
        Request.Fail(new LinkException($"the content stopped and was not resumed within {_retention}"));
        _ = ReleaseAnswerAsync();
    }

    private async Task ReleaseAnswerAsync()
    {
        try
        {
            if (_answerStream is not null)
            {
                if (_answerOwned)
                {
                    await _answerStream.DisposeAsync().ConfigureAwait(false);
                }
                return;
            }

            // Never sent — the sender never came back for it — so it was never
            // opened either, and whatever it holds is released unread.
            Task<ExchangeOutcome>? handler;
            lock (_mu)
            {
                handler = _handler;
            }
            if (handler is not null && (await handler.ConfigureAwait(false)).Answer is { } unsent)
            {
                await unsent.ReleaseAsync().ConfigureAwait(false);
            }
        }
#pragma warning disable CA1031 // Letting go happens on no caller's behalf; there is nobody to report a failure to.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    private Task WriteOkAsync(Stream stream, ReadOnlyMemory<byte> payload, CancellationToken token) =>
        LinkFrame.WriteAsync(stream, (byte)LinkFrameStatus.Ok, _id, payload, idle: null, token);

    private Task WriteFailedAsync(Stream stream, string reason, CancellationToken token) =>
        LinkFrame.WriteAsync(
            stream, (byte)LinkFrameStatus.Failed, _id, Encoding.UTF8.GetBytes(reason), idle: null, token);
}

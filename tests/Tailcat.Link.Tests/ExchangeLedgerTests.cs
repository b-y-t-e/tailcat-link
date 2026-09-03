// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Text;
using Tailcat.Link.Protocol;

namespace Tailcat.Link.Tests;

/// <summary>
/// Covers what keeps a retried request from becoming a second request: the
/// receiver remembering what it already answered.
/// </summary>
public class ExchangeLedgerTests
{
    private static readonly TimeSpan Retention = TimeSpan.FromSeconds(30);

    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UnixEpoch);

    private ExchangeLedger NewLedger() => new(Retention, _clock);

    private static LinkAnswer Ok(string answer) =>
        new(LinkFrameStatus.Ok, Encoding.UTF8.GetBytes(answer));

    /// <summary>
    /// The point of the whole class: the same request arriving twice runs the
    /// handler once, so a command the sender retried is not carried out twice.
    /// </summary>
    [Fact]
    public async Task ARepeatedRequestIsAnsweredWithoutRunningAgain()
    {
        ExchangeLedger ledger = NewLedger();
        Guid exchange = Guid.NewGuid();
        int runs = 0;
        Task<LinkAnswer> Answer()
        {
            runs++;
            return Task.FromResult(Ok("done"));
        }

        LinkAnswer first = await ledger.AnswerAsync(exchange, Answer);
        LinkAnswer again = await ledger.AnswerAsync(exchange, Answer);

        Assert.Equal(1, runs);
        Assert.Equal("done"u8.ToArray(), again.Payload.ToArray());
        Assert.Equal(first.Payload.ToArray(), again.Payload.ToArray());
    }

    /// <summary>A different request is a different request, id and all.</summary>
    [Fact]
    public async Task ADistinctRequestIsRunOnItsOwn()
    {
        ExchangeLedger ledger = NewLedger();
        int runs = 0;
        Task<LinkAnswer> Answer()
        {
            runs++;
            return Task.FromResult(Ok("done"));
        }

        await ledger.AnswerAsync(Guid.NewGuid(), Answer);
        await ledger.AnswerAsync(Guid.NewGuid(), Answer);

        Assert.Equal(2, runs);
    }

    /// <summary>
    /// A retry can arrive while the first attempt is still running — the
    /// session died, but the handler did not. It joins that one rather than
    /// starting a second.
    /// </summary>
    [Fact]
    public async Task ARetryOfARequestStillRunningJoinsIt()
    {
        ExchangeLedger ledger = NewLedger();
        Guid exchange = Guid.NewGuid();
        int runs = 0;
        TaskCompletionSource<LinkAnswer> handler = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<LinkAnswer> Answer()
        {
            runs++;
            return handler.Task;
        }

        Task<LinkAnswer> first = ledger.AnswerAsync(exchange, Answer);
        Task<LinkAnswer> retry = ledger.AnswerAsync(exchange, Answer);
        handler.SetResult(Ok("slow"));

        Assert.Equal("slow"u8.ToArray(), (await first).Payload.ToArray());
        Assert.Equal("slow"u8.ToArray(), (await retry).Payload.ToArray());
        Assert.Equal(1, runs);
    }

    /// <summary>
    /// A handler that reported a failure gave a real answer, and the retry is
    /// owed that same answer rather than another attempt at the work.
    /// </summary>
    [Fact]
    public async Task AReportedFailureIsRememberedLikeAnyOtherAnswer()
    {
        ExchangeLedger ledger = NewLedger();
        Guid exchange = Guid.NewGuid();
        int runs = 0;
        Task<LinkAnswer> Answer()
        {
            runs++;
            return Task.FromResult(new LinkAnswer(LinkFrameStatus.Failed, "the disk is on fire"u8.ToArray()));
        }

        await ledger.AnswerAsync(exchange, Answer);
        LinkAnswer again = await ledger.AnswerAsync(exchange, Answer);

        Assert.Equal(1, runs);
        Assert.Equal(LinkFrameStatus.Failed, again.Status);
    }

    /// <summary>
    /// Failing to produce an answer at all is not an answer, so there is
    /// nothing to recall and a retry is free to try again.
    /// </summary>
    [Fact]
    public async Task ARequestThatProducedNoAnswerIsNotRemembered()
    {
        ExchangeLedger ledger = NewLedger();
        Guid exchange = Guid.NewGuid();
        int runs = 0;
        Task<LinkAnswer> Answer()
        {
            runs++;
            return Task.FromResult(Ok("done"));
        }

        await Assert.ThrowsAsync<LinkException>(() => ledger.AnswerAsync(
            exchange, () => Task.FromException<LinkAnswer>(new LinkException("no handler"))));
        await ledger.AnswerAsync(exchange, Answer);

        Assert.Equal(1, runs);
    }

    /// <summary>
    /// What is remembered is not remembered forever: once the sender's own
    /// deadline has passed, no retry of that request can still arrive.
    /// </summary>
    [Fact]
    public async Task AnAnswerIsForgottenOnceNobodyCouldStillBeRetrying()
    {
        ExchangeLedger ledger = NewLedger();
        Guid exchange = Guid.NewGuid();
        int runs = 0;
        Task<LinkAnswer> Answer()
        {
            runs++;
            return Task.FromResult(Ok("done"));
        }

        await ledger.AnswerAsync(exchange, Answer);
        _clock.Advance(Retention + TimeSpan.FromSeconds(1));
        await ledger.AnswerAsync(exchange, Answer);

        Assert.Equal(2, runs);
    }

    /// <summary>
    /// The ceiling on what is remembered must not be paid for by an exchange
    /// that is still running: forgetting one of those would let its retry run
    /// the handler a second time, which is the one thing this class promises
    /// will not happen.
    /// </summary>
    [Fact]
    public async Task AFloodOfRequestsIsRefusedRatherThanForgettingOneStillRunning()
    {
        const int MoreThanIsRemembered = 5000;
        ExchangeLedger ledger = NewLedger();
        Guid firstExchange = Guid.NewGuid();
        int runs = 0;
        TaskCompletionSource<LinkAnswer> handlers = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<LinkAnswer> Answer()
        {
            runs++;
            return handlers.Task;
        }

        Task<LinkAnswer> first = ledger.AnswerAsync(firstExchange, Answer);
        int accepted = 1;
        for (int i = 1; i < MoreThanIsRemembered; i++)
        {
            try
            {
                _ = ledger.AnswerAsync(Guid.NewGuid(), Answer);
                accepted++;
            }
            catch (LinkException)
            {
                break;
            }
        }

        Task<LinkAnswer> retry = ledger.AnswerAsync(firstExchange, Answer);
        handlers.SetResult(Ok("done"));

        Assert.True(accepted < MoreThanIsRemembered, "the flood should have been refused at some point");
        Assert.Equal(accepted, runs);
        Assert.Equal("done"u8.ToArray(), (await first).Payload.ToArray());
        Assert.Equal("done"u8.ToArray(), (await retry).Payload.ToArray());
    }
}

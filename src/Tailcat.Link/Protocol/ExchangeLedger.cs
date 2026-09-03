// Copyright (c) Tailscale Inc & contributors
// SPDX-License-Identifier: BSD-3-Clause

namespace Tailcat.Link.Protocol;

/// <summary>
/// Remembers, for as long as the sender may still be retrying, what each
/// request was answered with — so a request that arrives twice is answered
/// twice but only ever <em>run</em> once.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="DurableLink"/> retries a request across sessions, and a session
/// can die between the handler finishing and its answer reaching the sender.
/// Without this the retry would run the handler a second time, which for the
/// commands this library is meant to carry — restart a service, write a file,
/// take a payment — is not an acceptable way to lose an answer.
/// </para>
/// <para>
/// It belongs to the link rather than the session, because the retry that has
/// to be recognised arrives on the <em>next</em> session by definition.
/// </para>
/// </remarks>
internal sealed class ExchangeLedger(TimeSpan retention, TimeProvider time)
{
    /// <summary>
    /// A ceiling on memory for a peer that floods ids. The oldest answered
    /// exchanges go first, which are also the ones least likely to still be
    /// retried; an exchange still running is never one of them.
    /// </summary>
    private const int MaxRemembered = 4096;

    private readonly Dictionary<Guid, Remembered> _answers = [];
    private readonly Lock _mu = new();

    private sealed record Remembered(Task<LinkAnswer> Answer, long At);

    /// <summary>
    /// Returns the answer to <paramref name="exchange"/>, producing it with
    /// <paramref name="answer"/> the first time and recalling it after that.
    /// A request still in flight is joined rather than started again.
    /// </summary>
    public Task<LinkAnswer> AnswerAsync(Guid exchange, Func<Task<LinkAnswer>> answer)
    {
        TaskCompletionSource<LinkAnswer> mine = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_mu)
        {
            ForgetStale();
            if (_answers.TryGetValue(exchange, out Remembered? seen))
            {
                return seen.Answer;
            }
            if (!MakeRoom())
            {
                // Everything remembered is still running, so nothing can be
                // dropped without risking a handler running twice — which is
                // the one thing this ledger exists to prevent. Refusing is
                // recoverable: the exchange dies without an answer, and the
                // sender's retry finds room once one of them finishes.
                throw new LinkException(
                    $"the other machine has {MaxRemembered} requests still running");
            }
            _answers[exchange] = new Remembered(mine.Task, time.GetTimestamp());
        }
        return ProduceAsync(exchange, answer, mine);
    }

    private async Task<LinkAnswer> ProduceAsync(
        Guid exchange,
        Func<Task<LinkAnswer>> answer,
        TaskCompletionSource<LinkAnswer> mine)
    {
        try
        {
            LinkAnswer produced = await answer().ConfigureAwait(false);
            mine.SetResult(produced);
            return produced;
        }
        catch (Exception ex)
        {
            // No answer was produced, so there is nothing to recall: a retry
            // must be allowed to try again rather than inherit this failure.
            Forget(exchange);
            mine.SetException(ex);
            _ = mine.Task.Exception;
            throw;
        }
    }

    private void Forget(Guid exchange)
    {
        lock (_mu)
        {
            _answers.Remove(exchange);
        }
    }

    /// <summary>Drops what the sender can no longer be retrying.</summary>
    private void ForgetStale()
    {
        long now = time.GetTimestamp();
        List<Guid> stale = [.. _answers
            .Where(entry => time.GetElapsedTime(entry.Value.At, now) > retention)
            .Select(entry => entry.Key)];
        foreach (Guid exchange in stale)
        {
            _answers.Remove(exchange);
        }
    }

    /// <summary>
    /// Frees a slot for one more exchange by forgetting the oldest ones that
    /// have already been answered, and reports whether it managed to.
    /// </summary>
    private bool MakeRoom()
    {
        if (_answers.Count < MaxRemembered)
        {
            return true;
        }

        List<Guid> answered = [.. _answers
            .Where(entry => entry.Value.Answer.IsCompleted)
            .OrderBy(entry => entry.Value.At)
            .Select(entry => entry.Key)
            .Take(_answers.Count - MaxRemembered + 1)];
        foreach (Guid exchange in answered)
        {
            _answers.Remove(exchange);
        }
        return _answers.Count < MaxRemembered;
    }
}

// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Security.Cryptography;

namespace Tailcat.Derp;

/// <summary>
/// Notices a relay connection that stopped carrying bytes without ending, and
/// has it abandoned. See <see cref="DerpLiveness"/> for why and when.
/// </summary>
/// <param name="liveness">The timings.</param>
/// <param name="time">The clock every timing is measured on.</param>
/// <param name="currentClient">The connection in use now; it changes on every reconnection.</param>
/// <param name="abandonAsync">
/// Closes the client if it is still current and the condition, checked with
/// the client held steady, still holds.
/// </param>
internal sealed class DerpLivenessWatch(
    DerpLiveness liveness,
    TimeProvider time,
    Func<DerpClient> currentClient,
    Func<DerpClient, Func<bool>, CancellationToken, Task> abandonAsync)
{
    private readonly byte[] _pingPayload = new byte[8];

    // Written on the receiving task and by senders, read by the check: longs
    // through Interlocked, the flag as an int.
    private long _lastFrameAt = time.GetTimestamp();
    private int _sentSinceFrame;

    // The outstanding ping and the connection it went out on, together: a ping
    // sent into a connection that has since been replaced says nothing about
    // its replacement, and must not condemn it.
    private (DerpClient Client, long SentAt)? _probe;
    private readonly Lock _probeMu = new();

    // The connection given up on, until it is replaced. It is not asked again
    // meanwhile, and its end is not held against the relay.
    private DerpClient? _abandoned;
    private int _stalledCount;

    /// <summary>When the last frame of any kind arrived.</summary>
    public long LastFrameAt => Interlocked.Read(ref _lastFrameAt);

    /// <summary>How many connections were abandoned for going silent.</summary>
    public int StalledCount => _stalledCount;

    /// <summary>Whether <paramref name="client"/> ended because this watch gave up on it.</summary>
    public bool WasAbandoned(DerpClient client) => ReferenceEquals(Volatile.Read(ref _abandoned), client);

    /// <summary>Something went out, so an answer is now expected soon.</summary>
    public void Sent() => Volatile.Write(ref _sentSinceFrame, 1);

    /// <summary>A frame arrived: the connection carries bytes this way.</summary>
    public void FrameReceived()
    {
        Interlocked.Exchange(ref _lastFrameAt, time.GetTimestamp());
        Volatile.Write(ref _sentSinceFrame, 0);
        lock (_probeMu)
        {
            _probe = null;
        }
    }

    /// <summary>Checks every <see cref="DerpLiveness.CheckInterval"/> until cancelled.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(liveness.CheckInterval, time, ct).ConfigureAwait(false);
                await CheckAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or DerpProtocolException)
            {
                // The connection went while it was being checked; the receive
                // loop is already replacing it.
            }
        }
    }

    /// <summary>One check: asks a connection that has been silent too long, abandons one that did not answer.</summary>
    public async Task CheckAsync(CancellationToken ct)
    {
        DerpClient client = currentClient();
        if (WasAbandoned(client))
        {
            return; // already given up on; the receive loop is replacing it
        }

        (DerpClient Client, long SentAt)? probe;
        lock (_probeMu)
        {
            probe = _probe;
        }

        if (probe is { } outstanding && ReferenceEquals(outstanding.Client, client))
        {
            await AbandonIfUnansweredAsync(client, outstanding, ct).ConfigureAwait(false);
            return;
        }

        if (IsSilentLongEnoughToAsk())
        {
            await PingAsync(client, ct).ConfigureAwait(false);
        }
    }

    private async Task AbandonIfUnansweredAsync(DerpClient client, (DerpClient Client, long SentAt) outstanding, CancellationToken ct)
    {
        if (time.GetElapsedTime(outstanding.SentAt) < liveness.Timeout)
        {
            return;
        }

        // Nothing at all came back — not the pong, not a packet, not a
        // keepalive.
        await AbandonAsync(client, () => _probe == outstanding, ct).ConfigureAwait(false);
    }

    private bool IsSilentLongEnoughToAsk()
    {
        TimeSpan silent = time.GetElapsedTime(LastFrameAt);
        bool waitingForAnswer = Volatile.Read(ref _sentSinceFrame) == 1;
        return silent >= (waitingForAnswer ? liveness.ProbeAfterSend : liveness.ProbeWhenIdle);
    }

    private async Task PingAsync(DerpClient client, CancellationToken ct)
    {
        RandomNumberGenerator.Fill(_pingPayload);
        long attemptAt = time.GetTimestamp();

        // The ping queues behind every write already waiting, and on a dead
        // flow the one in front can be stuck on a full send buffer for good, so
        // the write gets a deadline of its own. Unbounded, this await never
        // returned, and the check stopped at exactly the connection it exists
        // for.
        using (CancellationTokenSource deadline = new(liveness.Timeout, time))
        using (CancellationTokenSource either = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token))
        {
            try
            {
                await client.SendPingAsync(_pingPayload, either.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // A write that cannot go out is damning only if nothing came in
                // either: a busy link still delivers its peers' answers, and
                // the next check asks again.
                await AbandonAsync(client, () => LastFrameAt < attemptAt, ct).ConfigureAwait(false);
                return;
            }
        }

        // The timeout runs from when the ping left, not from when it began
        // queueing: a ping that waited behind a large send has not gone
        // unanswered while it waited. And only if nothing has arrived since
        // the attempt began: an answer quicker than this line has already come
        // and cleared nothing, and waiting for a second one would condemn a
        // healthy connection. FrameReceived stamps _lastFrameAt before it
        // takes _probeMu, so reading it under the lock cannot miss one.
        lock (_probeMu)
        {
            if (LastFrameAt < attemptAt)
            {
                _probe = (client, time.GetTimestamp());
            }
        }
    }

    private Task AbandonAsync(DerpClient client, Func<bool> stillSilent, CancellationToken ct) =>
        abandonAsync(client, () => Condemn(client, stillSilent), ct);

    // Called by the owner with the client held steady, just before it closes
    // it; marks it abandoned first, so the receive loop that sees it end knows.
    private bool Condemn(DerpClient client, Func<bool> stillSilent)
    {
        lock (_probeMu)
        {
            if (!stillSilent())
            {
                return false; // answered meanwhile
            }
            _probe = null;
        }
        Interlocked.Increment(ref _stalledCount);
        Volatile.Write(ref _abandoned, client);
        return true;
    }
}

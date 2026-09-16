// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using Tailcat.Keys;

namespace Tailcat.Derp;

/// <summary>
/// Sends again, on the connection that replaces a dead one, what the dead one
/// may have swallowed — ahead of anything new. See <see cref="RecentlySent"/>.
/// </summary>
internal sealed class LostPacketResender(TimeProvider time)
{
    // How far before a dead connection's last sign of life its sends are
    // resent from: a frame received says the connection worked when it left
    // the relay, not that everything sent up to then arrived.
    private static readonly TimeSpan ResendMargin = TimeSpan.FromSeconds(1);

    // How long a send waits for a replacement to be dialled, so it follows the
    // resent packets instead of overtaking them. Short, and paid once per
    // outage at most: a caller such as PeerLink's probe loop awaits its relay
    // send before probing direct paths, and a relay that cannot be reached
    // must not starve the path that still works.
    private static readonly TimeSpan SendWaitsForReplacement = TimeSpan.FromSeconds(2);

    // How long a send waits for a resend in progress before it is let go; its
    // packet is recorded all the same and goes out behind the backlog. Waiting
    // at all paces a fast sender, so the backlog empties: at most one packet
    // per wait. Waiting only this long is for PeerLink, which awaits its relay
    // probe before its direct-path keepalives, sent every 2 s, on a path that
    // counts as dead after 6 s without an answer. For that path to survive one
    // lost keepalive, 2 × (2 s + this) plus the round trip must stay under 6 s.
    private static readonly TimeSpan SendWaitsForResend = TimeSpan.FromMilliseconds(500);

    private readonly RecentlySent _recentlySent = new();
    private readonly Lock _mu = new();

    // Closed while the first attempt at a replacement is dialled; sends wait on it.
    private TaskCompletionSource? _gate;

    // Closed while a replacement that is up takes the backlog; sends wait on
    // it for SendWaitsForResend. It used to be no limit at all, so a long
    // backlog held back every sender, the direct path's keepalives included.
    private TaskCompletionSource? _resending;

    // Where the resend starts, from the first failure until a replacement is
    // in place. It outlives failed attempts: what needs resending dates from
    // the connection that died, not from the retry. While it is set, sends are
    // left to the resend rather than written into the dead connection.
    private long? _resendFrom;

    private long MarginTicks => (long)(ResendMargin.TotalSeconds * time.TimestampFrequency);

    /// <summary>
    /// Remembers a packet about to be sent, once a replacement in progress has
    /// had its chance to resend first.
    /// </summary>
    /// <returns>
    /// Whether to write it into the current connection. Not while a replacement
    /// is pending: the current connection is the dead one, and a packet written
    /// there would be lost after the resend had already gone past it.
    /// </returns>
    public async Task<bool> RecordAsync(NodePublic destination, ReadOnlyMemory<byte> packet, CancellationToken ct)
    {
        await WaitForReplacementAsync(ct).ConfigureAwait(false);
        lock (_mu)
        {
            _recentlySent.Add(time.GetTimestamp(), destination, packet);
            return _resendFrom is null;
        }
    }

    /// <summary>The connection answered: what was sent well before now arrived.</summary>
    public void Answered() => _recentlySent.ForgetBefore(time.GetTimestamp() - MarginTicks);

    /// <summary>
    /// A connection died whose last frame arrived at <paramref name="lastFrameAt"/>.
    /// The first call holds sends back; calls for a replacement's failed retry change nothing.
    /// </summary>
    public void BeginReplacement(long lastFrameAt)
    {
        lock (_mu)
        {
            if (_resendFrom is not null)
            {
                return;
            }
            _resendFrom = lastFrameAt - MarginTicks;
            Volatile.Write(ref _gate, NewGate());
        }
    }

    /// <summary>
    /// Resends into <paramref name="replacement"/>, in order, each packet with
    /// <paramref name="perPacketTimeout"/> to go out, until nothing is left —
    /// packets recorded meanwhile included — and then calls
    /// <paramref name="adopt"/> to put the replacement in place.
    /// </summary>
    /// <remarks>
    /// The last look for a backlog and <paramref name="adopt"/> happen under the
    /// lock <see cref="RecordAsync"/> takes, so no packet is left to a
    /// connection that is no longer the one written into.
    /// </remarks>
    /// <exception cref="IOException">If the replacement stops taking them.</exception>
    public async Task ResendAsync(DerpClient replacement, TimeSpan perPacketTimeout, Action adopt, CancellationToken ct)
    {
        TaskCompletionSource resending = NewGate();
        Volatile.Write(ref _resending, resending);
        try
        {
            long resendFrom;
            lock (_mu)
            {
                resendFrom = _resendFrom ?? long.MaxValue;
            }

            long resentUpTo = 0;
            while (TakeBacklogOrAdopt(resendFrom, resentUpTo, adopt) is { } backlog)
            {
                foreach (RecentlySent.SentPacket entry in backlog)
                {
                    _recentlySent.MarkResent(entry, time.GetTimestamp());
                    await ResendOneAsync(replacement, entry.Destination, entry.Packet, perPacketTimeout, ct).ConfigureAwait(false);
                    resentUpTo = entry.Sequence;
                }
            }
        }
        finally
        {
            Interlocked.CompareExchange(ref _resending, null, resending);
            resending.TrySetResult();
        }
    }

    /// <summary>An attempt failed: sends stop waiting, and the resend still starts where it did.</summary>
    public void ReplacementFailed()
    {
        if (Volatile.Read(ref _gate) is { } gate)
        {
            OpenGate(gate);
        }
    }

    /// <summary>A replacement is in place, or none will be: nothing is pending any more.</summary>
    public void ReplacementEnded()
    {
        lock (_mu)
        {
            _resendFrom = null;
        }
        ReplacementFailed();
    }

    private static TaskCompletionSource NewGate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    // In order, because relay1 closes on a gap: what is still to go, or null
    // once nothing is and the replacement has been adopted.
    private List<RecentlySent.SentPacket>? TakeBacklogOrAdopt(long resendFrom, long resentUpTo, Action adopt)
    {
        lock (_mu)
        {
            List<RecentlySent.SentPacket> backlog = _recentlySent.Since(resendFrom, resentUpTo);
            if (backlog.Count > 0)
            {
                return backlog;
            }
            adopt();
            _resendFrom = null;
            return null;
        }
    }

    private async Task WaitForReplacementAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref _resending) is { } resending)
        {
            try
            {
                await resending.Task.WaitAsync(SendWaitsForResend, time, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Not opened, unlike the dial gate: the next send is paced too.
                // Nothing is lost by stopping: RecordAsync still records the
                // packet while a replacement is pending, and the resend takes
                // up everything recorded before it adopts the replacement.
            }
            return;
        }
        if (Volatile.Read(ref _gate) is not { } gate)
        {
            return;
        }
        try
        {
            await gate.Task.WaitAsync(SendWaitsForReplacement, time, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // The replacement is slow; every send after this one would pay the
            // same wait for nothing.
            OpenGate(gate);
        }
    }

    private void OpenGate(TaskCompletionSource gate)
    {
        Interlocked.CompareExchange(ref _gate, null, gate);
        gate.TrySetResult();
    }

    // A deadline of its own, as the liveness ping has: a replacement cut
    // straight after its handshake takes writes into a full send buffer that
    // never drains, and the caller holds the connection lock, so an unbounded
    // write stalled the whole node until it was disposed. Missing the deadline
    // fails the attempt like a dropped connection, and the receive loop tries
    // again.
    private async Task ResendOneAsync(
        DerpClient replacement, NodePublic destination, byte[] packet, TimeSpan timeout, CancellationToken ct)
    {
        using CancellationTokenSource deadline = new(timeout, time);
        using CancellationTokenSource either = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        try
        {
            await replacement.SendAsync(destination, packet, either.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new IOException("the replacement relay connection stopped taking the resent packets", ex);
        }
    }
}

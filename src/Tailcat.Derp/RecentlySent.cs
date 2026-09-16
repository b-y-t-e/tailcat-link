// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using Tailcat.Keys;

namespace Tailcat.Derp;

/// <summary>
/// The packets a <see cref="DerpConnection"/> sent lately, so the ones a
/// connection took with it when it died can be sent again on the next.
/// </summary>
/// <remarks>
/// A relay acknowledges nothing, so which packets a dead connection delivered
/// cannot be known; what can be known is when it last showed signs of life.
/// Everything sent from shortly before then is resent. The peer may get some
/// twice, which both transports above tolerate — QUIC by packet number,
/// relay1 by record counter — while getting none of them would leave QUIC
/// waiting out retransmission timers that double with every loss, and end a
/// relay1 session outright.
/// </remarks>
internal sealed class RecentlySent
{
    // A bound on memory, not on what is worth resending. relay1 survives a cut
    // only if every record from the resend point on goes out again — one
    // missing is a gap that ends the session — and a relay1 transfer fills
    // 64 KiB with two records. What a connection answering frames holds is
    // about a second of sends anyway; this is reached only by a flow that
    // writes megabytes into a dead connection before it is noticed, or by an
    // outage long enough that everything sent meanwhile is waiting here.
    private const long MaxBytes = 16 * 1024 * 1024;

    private readonly Lock _mu = new();
    private readonly Queue<SentPacket> _entries = new();
    private long _bytes;
    private long _lastSequence;

    /// <summary>One packet, numbered in the order it was handed over.</summary>
    internal sealed class SentPacket(long sequence, long sentAt, NodePublic destination, byte[] packet)
    {
        public long Sequence { get; } = sequence;

        public long SentAt { get; set; } = sentAt;

        public NodePublic Destination { get; } = destination;

        public byte[] Packet { get; } = packet;
    }

    public void Add(long sentAt, NodePublic destination, ReadOnlyMemory<byte> packet)
    {
        lock (_mu)
        {
            _entries.Enqueue(new SentPacket(++_lastSequence, sentAt, destination, packet.ToArray()));
            _bytes += packet.Length;
            while (_bytes > MaxBytes)
            {
                _bytes -= _entries.Dequeue().Packet.Length;
            }
        }
    }

    /// <summary>
    /// Drops what was sent before <paramref name="timestamp"/>: the connection has answered since.
    /// </summary>
    /// <remarks>
    /// Stops at the first packet sent later, so one resent — and so sent again
    /// later than what followed it — keeps those behind it a little longer,
    /// which costs a copy at most.
    /// </remarks>
    public void ForgetBefore(long timestamp)
    {
        lock (_mu)
        {
            while (_entries.TryPeek(out SentPacket? oldest) && oldest.SentAt < timestamp)
            {
                _bytes -= _entries.Dequeue().Packet.Length;
            }
        }
    }

    /// <summary>
    /// Everything sent at or after <paramref name="timestamp"/> and numbered
    /// after <paramref name="afterSequence"/>, oldest first.
    /// </summary>
    public List<SentPacket> Since(long timestamp, long afterSequence)
    {
        lock (_mu)
        {
            return _entries
                .Where(entry => entry.Sequence > afterSequence && entry.SentAt >= timestamp)
                .ToList();
        }
    }

    /// <summary>
    /// Counts <paramref name="entry"/> as sent at <paramref name="timestamp"/>,
    /// so a replacement that dies at once hands it on again.
    /// </summary>
    public void MarkResent(SentPacket entry, long timestamp)
    {
        lock (_mu)
        {
            entry.SentAt = timestamp;
        }
    }
}

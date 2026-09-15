// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

namespace Tailcat.Link.Protocol;

/// <summary>When bytes last moved on any stream of one session.</summary>
/// <remarks>
/// What stands between one silent exchange and the whole session being
/// condemned for it. A ping outpaced by a large upload, or one exchange held
/// up by the application reading it slowly, says nothing about the peer while
/// other bytes are still arriving from it and leaving for it.
/// </remarks>
internal sealed class SessionMovement
{
    private readonly TimeProvider _time;
    private long _lastMoved;

    public SessionMovement(TimeProvider time)
    {
        _time = time;
        _lastMoved = time.GetTimestamp();
    }

    /// <summary>Whether bytes have moved within <paramref name="window"/>.</summary>
    public bool MovedWithin(TimeSpan window) =>
        _time.GetElapsedTime(Interlocked.Read(ref _lastMoved)) < window;

    /// <summary>Records that bytes moved.</summary>
    public void NoteMoved() => Interlocked.Exchange(ref _lastMoved, _time.GetTimestamp());

    /// <summary>The same stream, reporting its movement here.</summary>
    /// <param name="stream">The stream underneath.</param>
    /// <param name="countsStreamStartingWith">As for <see cref="MovingStream"/>.</param>
    public Stream Watch(Stream stream, Func<byte, bool>? countsStreamStartingWith = null) =>
        new MovingStream(stream, NoteMoved, countsStreamStartingWith);
}

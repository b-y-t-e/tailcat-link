// Copyright (c) Tailscale Inc & contributors
// SPDX-License-Identifier: BSD-3-Clause

namespace Tailcat.TestSupport;

/// <summary>
/// A manually advanced clock, so tests about elapsed time need not sleep.
/// </summary>
/// <remarks>
/// <para>
/// Timestamps come from the same reading as the wall clock. Overriding only
/// <see cref="GetUtcNow"/> would leave <see cref="GetTimestamp"/> on the real
/// Stopwatch, and both the session handshake timeout and the relay's
/// stability window are measured that way — a test that advanced such a clock
/// would move neither, and would pass while testing nothing.
/// </para>
/// <para>
/// Timers are deliberately left on the real clock. The code under test drives
/// background loops with <c>Task.Delay(..., TimeProvider)</c>, and a delay
/// that never fired would hang them instead of advancing the test; where a
/// test measures a backoff, that real duration is the thing being measured.
/// </para>
/// </remarks>
public sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
{
    private readonly Lock _mu = new();
    private DateTimeOffset _now = start;

    /// <inheritdoc/>
    public override DateTimeOffset GetUtcNow()
    {
        lock (_mu)
        {
            return _now;
        }
    }

    /// <inheritdoc/>
    public override long GetTimestamp()
    {
        lock (_mu)
        {
            return _now.UtcTicks;
        }
    }

    /// <inheritdoc/>
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    /// <summary>Moves the clock forward.</summary>
    public void Advance(TimeSpan delta)
    {
        lock (_mu)
        {
            _now += delta;
        }
    }
}

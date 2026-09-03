// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

namespace Tailcat.Link.Protocol;

/// <summary>
/// A deadline that measures silence rather than duration: it fires only when
/// nothing has moved for its whole limit.
/// </summary>
/// <remarks>
/// An exchange may legitimately take far longer than one request window — a
/// sixteen-megabyte payload through a shared relay is minutes, not seconds —
/// while a peer that has gone away is recognised by nothing arriving at all.
/// Putting a total deadline on the exchange would confuse the two and make
/// large messages impossible; every retry would resend from the start and run
/// out of time in exactly the same place.
/// </remarks>
internal sealed class IdleTimeout : IDisposable
{
    private readonly TimeSpan _limit;
    private readonly CancellationTokenSource _cts;

    /// <param name="limit">How long nothing may happen before the exchange is given up on.</param>
    /// <param name="time">
    /// The clock it is measured on, so a test with a hand-wound
    /// <see cref="TimeProvider"/> reaches the limit without waiting.
    /// </param>
    public IdleTimeout(TimeSpan limit, TimeProvider time)
    {
        _limit = limit;
        _cts = new CancellationTokenSource(limit, time);
    }

    /// <summary>Cancelled once the limit passes with nothing having moved.</summary>
    public CancellationToken Token => _cts.Token;

    /// <summary>Whether the silence, rather than anything else, ended the exchange.</summary>
    public bool Expired => _cts.IsCancellationRequested;

    /// <summary>Reports progress, which starts the limit again from now.</summary>
    public void Restart()
    {
        if (!_cts.IsCancellationRequested)
        {
            _cts.CancelAfter(_limit);
        }
    }

    public void Dispose() => _cts.Dispose();
}

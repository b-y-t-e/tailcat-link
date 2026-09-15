// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

namespace Tailcat.Link.Protocol;

/// <summary>What the machine at the other end of one session can take, asked once.</summary>
/// <remarks>
/// Asked lazily, by the first thing that needs to know, and remembered: a
/// machine does not change what it is in the middle of a session. The cost is
/// one round trip through the relay per session, and a heartbeat that gets
/// there first pays it instead.
/// </remarks>
internal sealed class PeerCapabilitiesQuery
{
    private readonly Func<CancellationToken, Task<PeerCapabilities>> _ping;
    private readonly Lock _mu = new();
    private Task<PeerCapabilities>? _capabilities;

    /// <param name="ping">Pings the other machine and returns what its answer said.</param>
    public PeerCapabilitiesQuery(Func<CancellationToken, Task<PeerCapabilities>> ping)
    {
        _ping = ping;
    }

    /// <summary>Keeps what a ping answer said, unless something was already kept.</summary>
    public void Remember(PeerCapabilities said)
    {
        lock (_mu)
        {
            _capabilities ??= Task.FromResult(said);
        }
    }

    /// <summary>What the other machine said, asking it first if nobody has.</summary>
    public Task<PeerCapabilities> GetAsync(CancellationToken cancellationToken)
    {
        Task<PeerCapabilities> asked;
        lock (_mu)
        {
            // An ask that failed is asked again rather than remembered: the
            // ping can be outpaced by a busy session without the session
            // being any the worse, and a remembered failure would fail every
            // exchange on it for the rest of its life.
            if (_capabilities is { IsFaulted: true } or { IsCanceled: true })
            {
                _capabilities = null;
            }
            asked = _capabilities ??= AskAsync();
        }
        return asked.WaitAsync(cancellationToken);
    }

    /// <summary>Pings until the answer arrives or the session ends.</summary>
    /// <remarks>
    /// A ping outpaced by the session's own bytes is asked again rather than
    /// failed: on a saturated link that is the normal case, and failing it
    /// would stop every exchange from starting until the link went quiet —
    /// which is exactly what the traffic keeping it busy prevents.
    /// </remarks>
    private async Task<PeerCapabilities> AskAsync()
    {
        while (true)
        {
            try
            {
                return await _ping(CancellationToken.None).ConfigureAwait(false);
            }
            catch (SessionBusyException)
            {
            }
        }
    }
}

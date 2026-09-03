// Copyright (c) Tailscale Inc & contributors
// SPDX-License-Identifier: BSD-3-Clause

using Tailcat.Keys;
using Tailcat.Link.Transport;
using Tailcat.Net;

namespace Tailcat.Link.Protocol;

/// <summary>
/// Where the next session comes from. The only difference between the two
/// ends of a link: one dials, the other waits.
/// </summary>
/// <remarks>
/// Splitting it out is what keeps <see cref="DurableLink"/> free of "am I the
/// host?" branches — it reconnects the same way whichever end it is on.
/// </remarks>
internal interface ISessionSource
{
    /// <summary>Produces the next session, waiting as long as it takes.</summary>
    Task<TailcatConnection> NextSessionAsync(INodeGateway gateway, CancellationToken cancellationToken);
}

/// <summary>The joining end: it knows where the host is and dials it.</summary>
/// <param name="peerAddress">The host's address, out of the invitation code.</param>
/// <param name="pairingToken">
/// The secret out of the same code. Presented on every session, because this
/// end cannot tell whether the machine it reached still remembers it — a host
/// that was reset is indistinguishable from one that never paired.
/// </param>
/// <param name="handshakeTimeout">
/// How long the host may say nothing before this end gives up on the session
/// and tries again. Without it a machine that accepts connections and answers
/// nothing would hold this link down for good.
/// </param>
/// <param name="time">The clock that timeout is measured on.</param>
internal sealed class DialingSessionSource(
    ConnBlob peerAddress,
    string pairingToken,
    TimeSpan handshakeTimeout,
    TimeProvider time) : ISessionSource
{
    public async Task<TailcatConnection> NextSessionAsync(
        INodeGateway gateway,
        CancellationToken cancellationToken)
    {
        TailcatConnection connection = await gateway.ConnectAsync(peerAddress, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            using IdleTimeout idle = new(handshakeTimeout, time);
            using CancellationTokenSource cts =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, idle.Token);
            await PairingHandshake.OfferAsync(connection, pairingToken, idle, cts.Token).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}

/// <summary>
/// The hosting end: it waits, and lets in only the machine it is paired with
/// or the one holding the invitation it is currently offering.
/// </summary>
/// <remarks>
/// Pairing is trust on first use, but not on any use: the first machine to
/// arrive with the invitation's <em>token</em> becomes the peer, and everyone
/// else — including whoever learned the host's address by watching the relay
/// it is connected to — is turned away. Once the pairing is made, or the
/// invitation's window has closed, there is nothing left for a leaked code to
/// buy.
/// </remarks>
/// <param name="policy">Decides who may stay, and writes the pairing down.</param>
/// <param name="handshakeTimeout">
/// How long a machine that has just connected may say nothing before it is
/// dropped. A stranger that connects and stalls would otherwise stop the host
/// from ever hearing from its peer again.
/// </param>
/// <param name="silenceTimeout">
/// How long this end may hear nothing at all before it gives up on the node
/// it is listening with. Waiting for a peer looks exactly like a node whose
/// relay socket died without saying so — a laptop resumed from sleep behind a
/// different NAT — and this end has nobody to restart it, so the wait is
/// bounded and the supervision loop rebuilds the node from the stored
/// identity instead. The address does not change, so a peer that was merely
/// away is unaffected.
/// </param>
/// <param name="time">The clock those timeouts are measured on.</param>
/// <param name="log">Where refusals are reported, if anywhere.</param>
internal sealed class AcceptingSessionSource(
    IPairingPolicy policy,
    TimeSpan handshakeTimeout,
    TimeSpan silenceTimeout,
    TimeProvider time,
    Action<string>? log) : ISessionSource
{
    // Candidates are heard side by side, because one at a time is a host that
    // a stranger can starve: knowing the address is enough to connect every
    // handshakeTimeout and say nothing, and the peer's connection would never
    // be looked at. The moment that matters is exactly the one this layer
    // exists for — a link that has just dropped and is repairing itself. The
    // number is a cap rather than none so that a flood costs bounded memory.
    private const int MaxCandidatesAtOnce = 8;

    public async Task<TailcatConnection> NextSessionAsync(INodeGateway gateway, CancellationToken cancellationToken)
    {
        using CancellationTokenSource abandon = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        List<Task<TailcatConnection?>> candidates = [];
        Task<TailcatConnection>? accepting = null;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (accepting is null && candidates.Count < MaxCandidatesAtOnce)
                {
                    accepting = AcceptBeforeGivingUpOnTheNodeAsync(gateway, abandon.Token);
                }

                Task finished = await FirstToFinishAsync(accepting, candidates).ConfigureAwait(false);
                if (finished == accepting)
                {
                    // Awaiting it is what turns silence into the LinkException
                    // that has the node rebuilt.
                    TailcatConnection knocked = await accepting.ConfigureAwait(false);
                    accepting = null;
                    candidates.Add(AdmitOrDropAsync(knocked, abandon.Token));
                    continue;
                }

                Task<TailcatConnection?> settled = (Task<TailcatConnection?>)finished;
                candidates.Remove(settled);
                if (await settled.ConfigureAwait(false) is { } admitted)
                {
                    return admitted;
                }
            }
        }
        finally
        {
            await abandon.CancelAsync().ConfigureAwait(false);
            await DiscardAsync(accepting, candidates).ConfigureAwait(false);
        }
    }

    private static Task<Task> FirstToFinishAsync(
        Task<TailcatConnection>? accepting,
        IEnumerable<Task<TailcatConnection?>> candidates)
    {
        List<Task> running = [.. candidates];
        if (accepting is not null)
        {
            running.Add(accepting);
        }
        return Task.WhenAny(running);
    }

    // The silence is measured per wait, so a stranger knocking, or a peer
    // that connects and is refused, counts as the node still working.
    private async Task<TailcatConnection> AcceptBeforeGivingUpOnTheNodeAsync(
        INodeGateway gateway,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource silence = new(silenceTimeout, time);
        using CancellationTokenSource cts =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, silence.Token);
        try
        {
            return await gateway.AcceptAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (silence.IsCancellationRequested)
        {
            throw new LinkException($"nothing has reached this machine in {silenceTimeout}");
        }
    }

    // A candidate owns its connection until it is admitted: whoever is turned
    // away is dropped here, because nothing else holds a reference to it.
    private async Task<TailcatConnection?> AdmitOrDropAsync(
        TailcatConnection connection,
        CancellationToken cancellationToken)
    {
        NodePublic wasPairedWith = policy.Peer;
        if (await TryAdmitAsync(connection, cancellationToken).ConfigureAwait(false))
        {
            log?.Invoke(wasPairedWith.IsZero ? $"paired with {connection.Peer}" : "peer is back");
            return connection;
        }

        log?.Invoke($"refused {connection.Peer}");
        await connection.DisposeAsync().ConfigureAwait(false);
        return null;
    }

    private async Task<bool> TryAdmitAsync(TailcatConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            using IdleTimeout idle = new(handshakeTimeout, time);
            using CancellationTokenSource cts =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, idle.Token);
            return await PairingHandshake.AcceptAsync(connection, policy, idle, cts.Token).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Nothing a stranger can do to its own handshake may reach the loop that is waiting for the peer.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            // One stranger's bad handshake is not this host's problem: it is
            // dropped and the next machine is heard, which is exactly what
            // must happen when the next one is the peer. A verdict is always
            // returned so that the connection is closed rather than left to
            // the finalizer.
            if (!cancellationToken.IsCancellationRequested)
            {
                log?.Invoke($"handshake with {connection.Peer} failed: {ex.Message}");
            }
            return false;
        }
    }

    // Whatever was still in flight when one candidate won, or when the link
    // shut down, is closed: a peer holding a session nobody reads waits for
    // an answer that is never coming, while a closed one has it reconnect.
    private static async Task DiscardAsync(
        Task<TailcatConnection>? accepting,
        IEnumerable<Task<TailcatConnection?>> candidates)
    {
        foreach (Task<TailcatConnection?> candidate in candidates)
        {
            await CloseCandidateAsync(candidate).ConfigureAwait(false);
        }
        if (accepting is not null)
        {
            await CloseAcceptedAsync(accepting).ConfigureAwait(false);
        }
    }

    private static async Task CloseCandidateAsync(Task<TailcatConnection?> candidate)
    {
        try
        {
            if (await candidate.ConfigureAwait(false) is { } spare)
            {
                await spare.DisposeAsync().ConfigureAwait(false);
            }
        }
#pragma warning disable CA1031 // This runs while unwinding; a second failure here would hide the first.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    private static async Task CloseAcceptedAsync(Task<TailcatConnection> accepting)
    {
        try
        {
            await (await accepting.ConfigureAwait(false)).DisposeAsync().ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Likewise: the accept was abandoned on purpose, so how it ended says nothing.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }
}

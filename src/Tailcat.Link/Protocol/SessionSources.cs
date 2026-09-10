// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Threading.Channels;
using Tailcat.Link.Transport;
using Tailcat.Net;

namespace Tailcat.Link.Protocol;

/// <summary>How far the attempt that has just ended got.</summary>
/// <remarks>
/// The pause between attempts is only dead time when there is a session
/// waiting to be served, and there can only be one waiting if the loop got as
/// far as asking for it. An attempt that died before that — building the node
/// failed, because the network went away — left whatever is queued untouched,
/// so shortening the pause for it would spin the loop on a connection nobody
/// is going to reach.
/// </remarks>
internal enum SessionAttempt
{
    /// <summary>The loop never asked the source for a session.</summary>
    NeverReachedTheSource,

    /// <summary>A session was taken from the source, and it is that session that ended.</summary>
    TookASession,
}

/// <summary>
/// Where a peer's next session comes from. The only difference between the
/// two ends of a link: one dials, the other is dialled.
/// </summary>
/// <remarks>
/// Splitting it out is what keeps the supervision loop free of "am I the
/// host?" branches — it reconnects the same way whichever end it is on.
/// </remarks>
internal interface ISessionSource : IAsyncDisposable
{
    /// <summary>Produces the next session, waiting as long as it takes.</summary>
    Task<ITailcatConnection> NextSessionAsync(INodeGateway gateway, CancellationToken cancellationToken);

    /// <summary>Waits out the pause the supervision loop keeps between attempts.</summary>
    /// <param name="pause">How long the loop would like to wait.</param>
    /// <param name="previous">How far the attempt that has just ended got.</param>
    /// <param name="cancellationToken">Stops waiting when the link is going down.</param>
    /// <remarks>
    /// Here, and not in the loop, because what the pause is for differs by end
    /// and nothing else about reconnecting does. It paces attempts the link
    /// makes itself; an end that makes none has nothing to pace, and waiting
    /// there is dead time in which a session is already going stale.
    /// </remarks>
    Task PauseAsync(TimeSpan pause, SessionAttempt previous, CancellationToken cancellationToken);
}

/// <summary>The joining end: it knows where the host is and dials it.</summary>
/// <param name="peerAddress">The host's address, out of the invitation code.</param>
/// <param name="hello">
/// What this end says about itself: the secret out of the same code, and
/// optionally a name to be listed under. The token is presented on every
/// session, because this end cannot tell whether the machine it reached still
/// remembers it — a host that was reset is indistinguishable from one that
/// never paired.
/// </param>
/// <param name="handshakeTimeout">
/// How long the host may say nothing before this end gives up on the session
/// and tries again. Without it a machine that accepts connections and answers
/// nothing would hold this link down for good.
/// </param>
/// <param name="time">The clock that timeout is measured on.</param>
internal sealed class DialingSessionSource(
    ConnBlob peerAddress,
    LinkHello hello,
    TimeSpan handshakeTimeout,
    TimeProvider time) : ISessionSource
{
    public async Task<ITailcatConnection> NextSessionAsync(
        INodeGateway gateway,
        CancellationToken cancellationToken)
    {
        ITailcatConnection connection = await gateway.ConnectAsync(peerAddress, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            using IdleTimeout idle = new(handshakeTimeout, time);
            using CancellationTokenSource cts =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, idle.Token);
            await PairingHandshake.OfferAsync(connection, hello, idle, cts.Token).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The whole of it. This end dials, and a link that cannot be built would
    /// otherwise retry in a tight loop against a public relay.
    /// </remarks>
    public Task PauseAsync(TimeSpan pause, SessionAttempt previous, CancellationToken cancellationToken) =>
        Task.Delay(pause, time, cancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// The hosting end's, one per peer: the host admits a machine and hands the
/// session here, rather than this waiting on the network itself.
/// </summary>
/// <remarks>
/// A host has one accept loop and many peers, so who a session belongs to is
/// decided once, where the handshake happens, instead of every peer racing to
/// claim every arrival.
/// </remarks>
internal sealed class OfferedSessionSource(TimeProvider time) : ISessionSource
{
    private readonly Channel<ITailcatConnection> _offered =
        Channel.CreateUnbounded<ITailcatConnection>(new UnboundedChannelOptions { SingleReader = true });

    /// <summary>Hands a freshly admitted session to the peer it belongs to.</summary>
    public void Offer(ITailcatConnection connection)
    {
        if (!_offered.Writer.TryWrite(connection))
        {
            // The peer is being disposed; nobody will ever read it.
            _ = connection.DisposeAsync().AsTask();
        }
    }

    public async Task<ITailcatConnection> NextSessionAsync(
        INodeGateway gateway,
        CancellationToken cancellationToken)
    {
        ITailcatConnection next = await _offered.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        // A peer that dialled twice while this end was busy is asking on its
        // newest connection; the older ones are already stale, and holding
        // them would leave the peer waiting on a session nobody reads.
        while (_offered.Reader.TryRead(out ITailcatConnection? newer))
        {
            await next.DisposeAsync().ConfigureAwait(false);
            next = newer;
        }
        return next;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// Only until the next session is offered. This end does not dial, so the
    /// pause paces nothing: the accept loop has already done the handshake and
    /// put a live connection in the queue, and nobody serves it until this
    /// loop comes round and takes it. The machine at the other end is by then
    /// connected, asking, and hearing nothing.
    /// </para>
    /// <para>
    /// Sitting the pause out is what made a flap permanent rather than a
    /// blip. It doubles to <see cref="LinkOptions.MaxReconnectDelay"/>, and
    /// once it is longer than the far end's
    /// <see cref="LinkOptions.HeartbeatInterval"/> plus
    /// <see cref="LinkOptions.RequestTimeout"/>, that end gives up and dials
    /// again inside every pause — and each new dial replaces the session
    /// whose connection is still waiting here, so what this loop finally picks
    /// up is one the node has already closed. Two machines can flap like that
    /// for as long as they are both switched on.
    /// </para>
    /// <para>
    /// The pause is still kept whole for the case it was written for: an
    /// attempt that failed before it asked for a session, because building the
    /// node itself is what failed.
    /// </para>
    /// </remarks>
    public async Task PauseAsync(TimeSpan pause, SessionAttempt previous, CancellationToken cancellationToken)
    {
        if (previous is not SessionAttempt.TookASession)
        {
            // Nothing was taken from the queue, so nothing in it is a session
            // this loop is about to serve: the attempt died before it asked,
            // which on this end means the node could not be built at all. A
            // connection left queued would otherwise end every pause at once
            // and spin the loop for as long as the network stayed away.
            await Task.Delay(pause, time, cancellationToken).ConfigureAwait(false);
            return;
        }

        using CancellationTokenSource elapsed = new(pause, time);
        using CancellationTokenSource either =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, elapsed.Token);
        try
        {
            if (await _offered.Reader.WaitToReadAsync(either.Token).ConfigureAwait(false))
            {
                return;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The pause ran out with nothing offered, which is the ordinary
            // case when the machine at the other end is simply away.
            return;
        }

        // The queue is closed: this peer is being disposed and no session will
        // ever be offered again. Its loop is about to be cancelled, and until
        // it is, returning at once would spin.
        await Task.Delay(pause, time, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _offered.Writer.TryComplete();
        while (_offered.Reader.TryRead(out ITailcatConnection? spare))
        {
            await spare.DisposeAsync().ConfigureAwait(false);
        }
    }
}

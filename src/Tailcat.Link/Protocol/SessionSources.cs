// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Threading.Channels;
using Tailcat.Link.Transport;
using Tailcat.Net;

namespace Tailcat.Link.Protocol;

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
internal sealed class OfferedSessionSource : ISessionSource
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

    public async ValueTask DisposeAsync()
    {
        _offered.Writer.TryComplete();
        while (_offered.Reader.TryRead(out ITailcatConnection? spare))
        {
            await spare.DisposeAsync().ConfigureAwait(false);
        }
    }
}

// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Buffers;
using System.Net.Quic;
using System.Net.Sockets;
using System.Text;
using Tailcat.Keys;
using Tailcat.Net;

namespace Tailcat.Link.Protocol;

/// <summary>
/// One live session with the peer: it carries requests both ways and reports,
/// exactly once, that it has ended.
/// </summary>
/// <remarks>
/// <para>
/// A session knows nothing about reconnecting. It has one job — speak the
/// frame protocol over one connection — and one signal, <see cref="Ended"/>,
/// which is what <see cref="DurableLink"/> waits on to decide it is time to
/// build another. Keeping those apart is what makes either of them
/// understandable.
/// </para>
/// <para>
/// Every path that touches the network funnels its failures into
/// <see cref="Fail"/>, so there is a single place where "this session is
/// dead" is decided, whether the news arrives through a failed request, a
/// heartbeat that went unanswered, or the serving loop falling over.
/// </para>
/// </remarks>
internal sealed class LinkSession : IExchangeCarrier, IAsyncDisposable
{
    private readonly ITailcatConnection _connection;
    private readonly Func<LinkRequestHandler?> _handler;
    private readonly Func<string, LinkChannelServe?> _channels;
    private readonly CancellationToken _handlerLifetime;
    private readonly ExchangeLedger _ledger;
    private readonly ExchangeRegistry _exchanges;
    private readonly PeerCapabilities _advertised;
    private readonly TimeSpan _requestTimeout;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource<string> _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock _channelsMu = new();
    private readonly HashSet<LinkChannelBase> _openChannels = [];
    private bool _channelsEnded;
    private string _endedReason = "the session ended";
    private readonly SessionMovement _movement;
    private readonly PeerCapabilitiesQuery _capabilities;
    private Task? _serveLoop;
    private bool _disposed;

    /// <param name="connection">The session's connection, which it takes ownership of.</param>
    /// <param name="handler">
    /// Read on every inbound request rather than captured, so an application
    /// that sets its handler after connecting still answers.
    /// </param>
    /// <param name="channels">
    /// Finds what serves a channel of a given name, or null when nothing
    /// does. Read on arrival rather than captured, for the same reason the
    /// request handler is.
    /// </param>
    /// <param name="ledger">
    /// Shared with every other session of the same link, because that is where
    /// a request retried after this session dies will arrive.
    /// </param>
    /// <param name="exchanges">
    /// Shared for the same reason, and for longer: an exchange resumed on a
    /// later session continues into the handler that is still reading it here.
    /// </param>
    /// <param name="advertised">
    /// What this machine says it can take when the peer pings it.
    /// </param>
    /// <param name="requestTimeout">
    /// How long an exchange may go without a byte moving before it is given
    /// up on. It bounds silence, not the exchange, so a payload that takes
    /// minutes to move is fine while a peer that stopped answering is not.
    /// </param>
    /// <param name="time">The clock the timeout is measured on.</param>
    /// <param name="linkClosed">
    /// What the application's handlers are given, instead of this session's
    /// own token. A handler runs on behalf of the link, not of the connection
    /// that happened to carry the request: cancelling it when the session
    /// drops would leave its side effects half-done and let the sender's retry
    /// — which the ledger can no longer answer from memory — run it a second
    /// time. So it keeps going to its answer, which the retry then collects.
    /// </param>
    public LinkSession(
        ITailcatConnection connection,
        Func<LinkRequestHandler?> handler,
        Func<string, LinkChannelServe?> channels,
        ExchangeLedger ledger,
        ExchangeRegistry exchanges,
        PeerCapabilities advertised,
        TimeSpan requestTimeout,
        TimeProvider time,
        CancellationToken linkClosed)
    {
        _connection = connection;
        _handler = handler;
        _channels = channels;
        _handlerLifetime = linkClosed;
        _ledger = ledger;
        _exchanges = exchanges;
        _advertised = advertised;
        _requestTimeout = requestTimeout;
        _time = time;
        _movement = new SessionMovement(time);
        _capabilities = new PeerCapabilitiesQuery(PingAsync);
    }

    /// <summary>The machine at the other end.</summary>
    public NodePublic Peer => _connection.Peer;

    /// <summary>Completes with the reason this session ended.</summary>
    public Task<string> Ended => _ended.Task;

    /// <summary>Cancelled the moment this session stops carrying anything.</summary>
    /// <remarks>
    /// A channel holds this rather than the link's token: it ends with the
    /// session that carries it, which is the whole of what a channel promises
    /// instead of a transfer's durability.
    /// </remarks>
    public CancellationToken Alive => _cts.Token;

    /// <summary>Starts answering the peer.</summary>
    public void Start() => _serveLoop ??= Task.Run(() => ServeLoopAsync(_cts.Token), CancellationToken.None);

    /// <summary>
    /// Opens a channel on this session and returns the stream its frames go
    /// on, once the peer has said it has a handler for the name.
    /// </summary>
    /// <exception cref="RemoteHandlerException">If the peer has no handler for that name.</exception>
    /// <exception cref="LinkException">If the session could not carry it.</exception>
    public async Task<Stream> OpenChannelAsync(string name, CancellationToken cancellationToken)
    {
        // Before the stream exists, because a name this side got wrong is the
        // caller's own mistake and must not cost a stream to find out.
        byte[] encodedName = ChannelFrame.EncodeName(name);

        using IdleTimeout idle = new(_requestTimeout, _time);
        Stream? stream = null;
        bool handedOver = false;
        try
        {
            using CancellationTokenSource cts =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token, idle.Token);
            stream = await OpenStreamAsync(cts.Token).ConfigureAwait(false);
            idle.Restart();
            await LinkFrame.WriteAsync(
                    stream, (byte)LinkFrameKind.Channel, Guid.NewGuid(), encodedName, idle, cts.Token)
                .ConfigureAwait(false);

            (byte status, _, byte[] answer) =
                await LinkFrame.ReadAsync(stream, idle, cts.Token).ConfigureAwait(false);
            if (status == (byte)LinkFrameStatus.Failed)
            {
                throw new RemoteHandlerException(
                    $"the other machine would not take the channel: {Encoding.UTF8.GetString(answer)}");
            }
            handedOver = true;
            return stream;
        }
        catch (Exception ex) when (SessionFailure.EndsTheSession(ex) && !cancellationToken.IsCancellationRequested)
        {
            string reason = idle.Expired ? $"the other machine sent nothing for {_requestTimeout}" : ex.Message;
            throw new LinkException(reason, ex);
        }
        finally
        {
            // Every way out but the one that worked: a caller that gave up,
            // a name the peer would not take, a session that died mid-open.
            // The exchange beside this says the same thing with `await
            // using`, which does not fit here because on success the stream
            // outlives the call — it is what the channel then sends on.
            if (!handedOver && stream is not null)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>Sends a request and returns the peer's answer.</summary>
    /// <param name="exchange">
    /// Identifies the request rather than this attempt at it: a retry on a
    /// later session repeats the id, and the peer answers from memory instead
    /// of running its handler again.
    /// </param>
    /// <param name="payload">What to send.</param>
    /// <param name="cancellationToken">Gives up on the request.</param>
    public async Task<byte[]> RequestAsync(
        Guid exchange,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        (byte status, byte[] answer) = await ExchangeAsync(
            LinkFrameKind.Request,
            exchange,
            payload,
            expectAnswer: true,
            silenceEndsSession: false,
            cancellationToken).ConfigureAwait(false);

        if (status == (byte)LinkFrameStatus.Failed)
        {
            throw new RemoteHandlerException(
                $"the other machine could not answer: {Encoding.UTF8.GetString(answer)}");
        }
        return answer;
    }

    /// <summary>Sends a message the peer will not answer.</summary>
    public async Task NotifyAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken) =>
        await ExchangeAsync(
                LinkFrameKind.Notify,
                Guid.NewGuid(),
                payload,
                expectAnswer: false,
                silenceEndsSession: false,
                cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// Checks that the peer is still there.
    /// </summary>
    /// <remarks>
    /// This is the only way to find out. Writing into a session whose peer has
    /// vanished succeeds — the bytes go to a relay that has nobody to give
    /// them to — so silence, not an error, is what a dead peer looks like.
    /// <para>
    /// This is also the only exchange whose silence condemns the session. It
    /// is answered by the peer's frame loop rather than by application code,
    /// so nothing legitimate can make it slow.
    /// </para>
    /// </remarks>
    public async Task<PeerCapabilities> PingAsync(CancellationToken cancellationToken)
    {
        (_, byte[] answer) = await ExchangeAsync(
                LinkFrameKind.Ping,
                Guid.NewGuid(),
                ReadOnlyMemory<byte>.Empty,
                expectAnswer: true,
                silenceEndsSession: true,
                cancellationToken)
            .ConfigureAwait(false);

        PeerCapabilities said = PeerCapabilitiesCodec.Decode(answer);
        _capabilities.Remember(said);
        return said;
    }

    /// <summary>What the machine at the other end can take, asked once per session.</summary>
    public Task<PeerCapabilities> CapabilitiesAsync(CancellationToken cancellationToken) =>
        _capabilities.GetAsync(cancellationToken);

    /// <summary>When bytes last moved on any stream of this session.</summary>
    public SessionMovement Movement => _movement;

    /// <inheritdoc/>
    public async Task<Stream> OpenStreamAsync(CancellationToken cancellationToken) =>
        _movement.Watch(await _connection.OpenStreamAsync(cancellationToken).ConfigureAwait(false));

    /// <inheritdoc/>
    public void FailUnlessBusy(string reason, bool silence)
    {
        if (silence && _movement.MovedWithin(_requestTimeout))
        {
            return;
        }
        Fail(reason);
    }

    /// <summary>Declares the session over, for the first caller to say so.</summary>
    public void Fail(string reason)
    {
        if (!_ended.TrySetResult(reason))
        {
            return;
        }

        // A channel is not durable: it ends with the session carrying it, and
        // the application is told so here rather than on a send it may never
        // make. Not awaited, because failing is what every path that touches
        // the network does on its way out and none of them may block on an
        // application's Closed handler; EndChannelsAsync swallows what those
        // handlers throw for the same reason.
        _ = EndChannelsAsync(reason);
    }

    /// <summary>
    /// Takes on a channel opened on this session, so that the session ending
    /// ends the channel too.
    /// </summary>
    /// <remarks>
    /// The channel the peer opens is ended by the loop serving it, which owns
    /// the handler and the stream; this is for the one this end opened, which
    /// nothing else is watching.
    /// </remarks>
    public async Task<TChannel> HoldAsync<TChannel>(TChannel channel)
        where TChannel : LinkChannelBase
    {
        bool tooLate;
        lock (_channelsMu)
        {
            tooLate = _channelsEnded;
            if (!tooLate)
            {
                _openChannels.Add(channel);
            }
        }

        if (tooLate)
        {
            // The session died while the channel was being opened. Nothing
            // else is going to say so: the ending has already been through
            // every channel this session was holding.
            await channel.EndWithSessionAsync(_endedReason).ConfigureAwait(false);
            return channel;
        }

        // A session may carry thousands of channels one after another, so each
        // is let go of as it closes rather than kept until the session ends.
        channel.Closed += ForgetChannel;
        return channel;
    }

    private void ForgetChannel(object? sender, ChannelClosedEventArgs ending)
    {
        if (sender is not LinkChannelBase channel)
        {
            return;
        }
        lock (_channelsMu)
        {
            _openChannels.Remove(channel);
        }
    }

    private async Task EndChannelsAsync(string reason)
    {
        LinkChannelBase[] open;
        lock (_channelsMu)
        {
            _channelsEnded = true;
            _endedReason = reason;
            open = [.. _openChannels];
            _openChannels.Clear();
        }

        foreach (LinkChannelBase channel in open)
        {
            try
            {
                await channel.EndWithSessionAsync(reason).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // One channel's ending, or an application's handler for it, must not stop the rest.
            catch (Exception)
#pragma warning restore CA1031
            {
            }
        }
    }

    private async Task<(byte Status, byte[] Payload)> ExchangeAsync(
        LinkFrameKind kind,
        Guid exchange,
        ReadOnlyMemory<byte> payload,
        bool expectAnswer,
        bool silenceEndsSession,
        CancellationToken cancellationToken)
    {
        // Silence rather than duration, so that a payload too large to move in
        // one window is not confused with a peer that has gone away.
        using IdleTimeout idle = new(_requestTimeout, _time);

        try
        {
            // Inside the try on purpose: a session disposed by the supervisor
            // makes reading _cts.Token throw, and outside it that would reach
            // the application instead of being retried on the next session.
            using CancellationTokenSource cts =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token, idle.Token);
            // A ping goes on a stream nobody counts, so that the ping itself is
            // never taken for the other bytes that would excuse its silence.
            Stream stream = kind == LinkFrameKind.Ping
                ? await _connection.OpenStreamAsync(cts.Token).ConfigureAwait(false)
                : await OpenStreamAsync(cts.Token).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                idle.Restart();
                await LinkFrame.WriteAsync(stream, (byte)kind, exchange, payload, idle, cts.Token)
                    .ConfigureAwait(false);
                if (!expectAnswer)
                {
                    return ((byte)LinkFrameStatus.Ok, []);
                }

                (byte status, _, byte[] answer) =
                    await LinkFrame.ReadAsync(stream, idle, cts.Token).ConfigureAwait(false);
                return (status, answer);
            }
        }
        catch (Exception ex) when (SessionFailure.EndsTheSession(ex) && !cancellationToken.IsCancellationRequested)
        {
            bool timedOut = idle.Expired;
            string reason = timedOut
                ? $"the other machine sent nothing for {_requestTimeout}"
                : ex.Message;

            // A request that ran out of time says nothing about the session: an
            // application handler is allowed to take longer than one request
            // window, and condemning the session for it would break every other
            // exchange sharing it. Deciding that the peer is gone is the
            // heartbeat's job — its ping is answered by the frame loop, so its
            // silence really does mean silence.
            if (timedOut && silenceEndsSession && _movement.MovedWithin(_requestTimeout))
            {
                // The ping was outpaced, not ignored: bytes are moving on this
                // session, which is proof enough that the peer is there.
                throw new SessionBusyException(
                    $"{reason}, while the session was busy moving other bytes", ex);
            }
            if (!timedOut || silenceEndsSession)
            {
                Fail(reason);
            }
            throw new LinkException(reason, ex);
        }
    }

    private async Task ServeLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // The other machine's ping is not counted, for the reason this
                // machine's own is not: it must never excuse a ping's silence.
                Stream stream = _movement.Watch(
                    await _connection.AcceptStreamAsync(ct).ConfigureAwait(false),
                    countsStreamStartingWith: tag => tag != (byte)LinkFrameKind.Ping);
                _ = Task.Run(() => ServeOneAsync(stream, ct), CancellationToken.None);
            }
        }
        catch (Exception ex) when (SessionFailure.EndsTheSession(ex))
        {
            Fail(ex is OperationCanceledException ? "the link was closed" : ex.Message);
        }
    }

    private async Task ServeOneAsync(Stream stream, CancellationToken ct)
    {
        await using (stream.ConfigureAwait(false))
        {
            try
            {
                (byte tag, Guid exchange, byte[] payload) =
                    await LinkFrame.ReadAsync(stream, idle: null, ct).ConfigureAwait(false);
                await ServeAsync((LinkFrameKind)tag, exchange, payload, stream, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (SessionFailure.EndsTheSession(ex) || ex is LinkException)
            {
                // One exchange died. That is not the session: each exchange has
                // its own stream, so nothing is left half-read for the next one.
            }
        }
    }

    private async Task ServeAsync(
        LinkFrameKind kind,
        Guid exchange,
        byte[] payload,
        Stream stream,
        CancellationToken ct)
    {
        switch (kind)
        {
            case LinkFrameKind.Ping:
                // Answered with what this machine can take. An older machine
                // pinging this one reads the answer and ignores what is in it,
                // as it always has.
                await AnswerAsync(
                        stream,
                        exchange,
                        new LinkAnswer(LinkFrameStatus.Ok, PeerCapabilitiesCodec.Encode(_advertised)),
                        ct)
                    .ConfigureAwait(false);
                return;

            case LinkFrameKind.Notify:
                // Through the same isolation a request gets, minus the answer:
                // nobody is waiting for one, so a handler that throws here has
                // nowhere to report to and must not be allowed to fault the
                // task this runs on, which nobody observes either.
                _ = await RunHandlerAsync(payload, _handlerLifetime).ConfigureAwait(false);
                return;

            case LinkFrameKind.Exchange:
                // Not through the ledger: the registry is what keeps the
                // content, the handler and its answer alive between sessions,
                // and the ledger's memory of an answer would only cover the
                // last few seconds of an exchange that took an hour.
                await _exchanges.DeliverAsync(exchange, payload, stream, ct).ConfigureAwait(false);
                return;

            case LinkFrameKind.Channel:
                await ServeChannelAsync(exchange, payload, stream, ct).ConfigureAwait(false);
                return;

            case LinkFrameKind.Request:
                // Through the ledger, so that a request the sender is retrying
                // is answered from what its first arrival produced.
                LinkAnswer answer = await _ledger
                    .AnswerAsync(exchange, () => RunHandlerAsync(payload, _handlerLifetime)).ConfigureAwait(false);
                await AnswerAsync(stream, exchange, answer, ct).ConfigureAwait(false);
                return;

            default:
                await AnswerAsync(
                    stream,
                    exchange,
                    new LinkAnswer(
                        LinkFrameStatus.Failed,
                        Encoding.UTF8.GetBytes($"unknown message type 0x{(byte)kind:X2}")),
                    ct).ConfigureAwait(false);
                return;
        }
    }

    /// <summary>
    /// Answers whether anything here takes this channel, and if so hands the
    /// stream to it for as long as it keeps reading.
    /// </summary>
    private async Task ServeChannelAsync(Guid exchange, byte[] payload, Stream stream, CancellationToken ct)
    {
        string name = ChannelFrame.DecodeName(payload);
        if (_channels(name) is not { } serve)
        {
            await AnswerAsync(
                stream,
                exchange,
                new LinkAnswer(
                    LinkFrameStatus.Failed,
                    Encoding.UTF8.GetBytes($"the other machine has no \"{name}\" channel")),
                ct).ConfigureAwait(false);
            return;
        }

        await AnswerAsync(stream, exchange, new LinkAnswer(LinkFrameStatus.Ok, default), ct).ConfigureAwait(false);
        // The session's own token, not the link's: a channel is not resumed,
        // so a handler still reading one when the session dies is told so
        // rather than left waiting for frames that will never come.
        await serve(stream, _cts.Token).ConfigureAwait(false);
    }

    /// <summary>Runs the application's handler and turns whatever it does into an answer.</summary>
    private async Task<LinkAnswer> RunHandlerAsync(byte[] payload, CancellationToken ct)
    {
        try
        {
            return new LinkAnswer(LinkFrameStatus.Ok, await InvokeHandlerAsync(payload, ct).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Application code threw. The peer is waiting and deserves to be
            // told why rather than left to time out, and this machine's link
            // must survive its own handler's bugs. This is a real answer, so
            // it is remembered like one: a retry is told the same thing.
            return new LinkAnswer(LinkFrameStatus.Failed, Encoding.UTF8.GetBytes(ex.Message));
        }
    }

    private async Task<ReadOnlyMemory<byte>> InvokeHandlerAsync(byte[] payload, CancellationToken ct)
    {
        LinkRequestHandler? handler = _handler()
            ?? throw new LinkException("the other machine is not handling requests");
        return await handler(payload, ct).ConfigureAwait(false);
    }

    private static Task AnswerAsync(Stream stream, Guid exchange, LinkAnswer answer, CancellationToken ct) =>
        LinkFrame.WriteAsync(stream, (byte)answer.Status, exchange, answer.Payload, idle: null, ct);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        Fail("the link was closed");
        await _cts.CancelAsync().ConfigureAwait(false);
        await _connection.DisposeAsync().ConfigureAwait(false);
        if (_serveLoop is not null)
        {
            try
            {
                await _serveLoop.ConfigureAwait(false);
            }
            catch (Exception ex) when (SessionFailure.EndsTheSession(ex))
            {
            }
        }
        _cts.Dispose();
    }
}

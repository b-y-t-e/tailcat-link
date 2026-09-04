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
internal sealed class LinkSession : IAsyncDisposable
{
    private readonly ITailcatConnection _connection;
    private readonly Func<LinkRequestHandler?> _handler;
    private readonly CancellationToken _handlerLifetime;
    private readonly ExchangeLedger _ledger;
    private readonly TransferRegistry _transfers;
    private readonly TimeSpan _requestTimeout;
    private readonly TimeSpan _transferStall;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource<string> _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _serveLoop;
    private bool _disposed;

    /// <param name="connection">The session's connection, which it takes ownership of.</param>
    /// <param name="handler">
    /// Read on every inbound request rather than captured, so an application
    /// that sets its handler after connecting still answers.
    /// </param>
    /// <param name="ledger">
    /// Shared with every other session of the same link, because that is where
    /// a request retried after this session dies will arrive.
    /// </param>
    /// <param name="transfers">
    /// Shared for the same reason, and for longer: a transfer resumed on a
    /// later session continues into the handler that is still reading it here.
    /// </param>
    /// <param name="requestTimeout">
    /// How long an exchange may go without a byte moving before it is given
    /// up on. It bounds silence, not the exchange, so a payload that takes
    /// minutes to move is fine while a peer that stopped answering is not.
    /// </param>
    /// <param name="transferStall">
    /// The same bound for a transfer, which is generously larger: a transfer
    /// is expected to be held back by a slow disk at the far end, and only
    /// silence for this long means the attempt is worth starting again.
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
        ExchangeLedger ledger,
        TransferRegistry transfers,
        TimeSpan requestTimeout,
        TimeSpan transferStall,
        TimeProvider time,
        CancellationToken linkClosed)
    {
        _connection = connection;
        _handler = handler;
        _handlerLifetime = linkClosed;
        _ledger = ledger;
        _transfers = transfers;
        _requestTimeout = requestTimeout;
        _transferStall = transferStall;
        _time = time;
    }

    /// <summary>The machine at the other end.</summary>
    public NodePublic Peer => _connection.Peer;

    /// <summary>Completes with the reason this session ended.</summary>
    public Task<string> Ended => _ended.Task;

    /// <summary>Starts answering the peer.</summary>
    public void Start() => _serveLoop ??= Task.Run(() => ServeLoopAsync(_cts.Token), CancellationToken.None);

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
    /// Moves as much of a transfer as this session lives long enough to move.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One attempt, not the whole transfer: it offers the content, is told
    /// where the receiving machine wants it to start — zero the first time,
    /// and mid-file for every attempt after a session died — and then streams
    /// blocks until they run out. A failure here is the caller's cue to try
    /// again on the next session, which is where the resuming happens.
    /// </para>
    /// <para>
    /// Nothing is held in memory: one block at a time is read from the
    /// content and written to the stream, and the transport's own flow
    /// control is what keeps a fast disk from running ahead of a slow relay.
    /// </para>
    /// </remarks>
    /// <exception cref="RemoteHandlerException">
    /// If the peer refused the transfer, or its handler threw. Neither is
    /// worth retrying.
    /// </exception>
    /// <exception cref="LinkException">If this attempt failed.</exception>
    public async Task SendTransferAsync(OutboundTransfer transfer, CancellationToken cancellationToken)
    {
        using IdleTimeout idle = new(_transferStall, _time);
        try
        {
            using CancellationTokenSource alive =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
            using CancellationTokenSource moving =
                CancellationTokenSource.CreateLinkedTokenSource(alive.Token, idle.Token);

            Stream stream = await _connection.OpenStreamAsync(moving.Token).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                idle.Restart();
                await LinkFrame.WriteAsync(
                    stream,
                    (byte)LinkFrameKind.Transfer,
                    transfer.Id,
                    TransferFrame.EncodeOffer(transfer.Offer),
                    idle,
                    moving.Token).ConfigureAwait(false);

                (byte taken, _, byte[] where) =
                    await LinkFrame.ReadAsync(stream, idle, moving.Token).ConfigureAwait(false);
                Refused(taken, where);
                transfer.RewindTo(TransferFrame.DecodeOffset(where, transfer.Offer.Length));

                await WriteContentAsync(stream, transfer, idle, moving.Token).ConfigureAwait(false);

                // Read without the idle bound: what is being waited for now is
                // the receiving application's handler finishing with the
                // content, and a handler is allowed to take as long as it
                // takes. A peer that has gone away is the heartbeat's business.
                (byte done, _, byte[] reason) =
                    await LinkFrame.ReadAsync(stream, idle: null, alive.Token).ConfigureAwait(false);
                Refused(done, reason);
            }
        }
        catch (Exception ex) when (IsSessionFailure(ex) && !cancellationToken.IsCancellationRequested)
        {
            string reason = idle.Expired
                ? $"the transfer moved nothing for {_transferStall}"
                : ex.Message;
            Fail(reason);
            throw new LinkException(reason, ex);
        }
    }

    private static void Refused(byte status, byte[] answer)
    {
        if (status == (byte)LinkFrameStatus.Failed)
        {
            throw new RemoteHandlerException(
                $"the other machine would not take the transfer: {Encoding.UTF8.GetString(answer)}");
        }
    }

    private static async Task WriteContentAsync(
        Stream stream,
        OutboundTransfer transfer,
        IdleTimeout idle,
        CancellationToken cancellationToken)
    {
        // Header and block in one buffer, so each block is one write: on the
        // relayed transport a separate four-byte write would be a whole
        // record of its own.
        byte[] buffer = ArrayPool<byte>.Shared.Rent(TransferFrame.BlockHeaderLength + TransferFrame.BlockBytes);
        try
        {
            while (true)
            {
                int read = await transfer
                    .ReadAsync(
                        buffer.AsMemory(TransferFrame.BlockHeaderLength, TransferFrame.BlockBytes),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                TransferFrame.WriteBlockHeader(buffer, read);
                await stream
                    .WriteAsync(buffer.AsMemory(0, TransferFrame.BlockHeaderLength + read), cancellationToken)
                    .ConfigureAwait(false);
                idle.Restart();
                transfer.Advance(read);
            }

            // The end marker goes out even when the content stopped short of
            // the length that was announced. Retrying that would read the same
            // short content again; the receiver is the end that can tell the
            // difference between a truncated transfer and a finished one, and
            // it refuses it as an answer, which stops the sender for good.
            TransferFrame.WriteBlockHeader(buffer, 0);
            await stream
                .WriteAsync(buffer.AsMemory(0, TransferFrame.BlockHeaderLength), cancellationToken)
                .ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

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
    public async Task PingAsync(CancellationToken cancellationToken) =>
        await ExchangeAsync(
                LinkFrameKind.Ping,
                Guid.NewGuid(),
                ReadOnlyMemory<byte>.Empty,
                expectAnswer: true,
                silenceEndsSession: true,
                cancellationToken)
            .ConfigureAwait(false);

    /// <summary>Declares the session over, for the first caller to say so.</summary>
    public void Fail(string reason) => _ended.TrySetResult(reason);

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
            Stream stream = await _connection.OpenStreamAsync(cts.Token).ConfigureAwait(false);
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
        catch (Exception ex) when (IsSessionFailure(ex) && !cancellationToken.IsCancellationRequested)
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
                Stream stream = await _connection.AcceptStreamAsync(ct).ConfigureAwait(false);
                _ = Task.Run(() => ServeOneAsync(stream, ct), CancellationToken.None);
            }
        }
        catch (Exception ex) when (IsSessionFailure(ex))
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
            catch (Exception ex) when (IsSessionFailure(ex) || ex is LinkException)
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
                await AnswerAsync(stream, exchange, new LinkAnswer(LinkFrameStatus.Ok, default), ct)
                    .ConfigureAwait(false);
                return;

            case LinkFrameKind.Notify:
                // Through the same isolation a request gets, minus the answer:
                // nobody is waiting for one, so a handler that throws here has
                // nowhere to report to and must not be allowed to fault the
                // task this runs on, which nobody observes either.
                _ = await RunHandlerAsync(payload, _handlerLifetime).ConfigureAwait(false);
                return;

            case LinkFrameKind.Transfer:
                // Not through the ledger: what makes a transfer arrive once
                // is the registry, which keeps the half-delivered content and
                // the handler reading it alive between sessions. The ledger's
                // memory of an answer would only cover the last few seconds
                // of a transfer that took an hour.
                await _transfers.DeliverAsync(exchange, payload, stream, ct).ConfigureAwait(false);
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

    // What "the session is gone" looks like from every layer underneath: QUIC,
    // the socket, a disposed connection, or a timeout that has run out.
    private static bool IsSessionFailure(Exception ex) =>
        ex is QuicException or IOException or SocketException or ObjectDisposedException
            or OperationCanceledException or InvalidOperationException;

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
            catch (Exception ex) when (IsSessionFailure(ex))
            {
            }
        }
        _cts.Dispose();
    }
}

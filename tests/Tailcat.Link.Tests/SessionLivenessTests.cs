// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using Tailcat.Keys;
using Tailcat.Link.Protocol;
using Tailcat.Net;

namespace Tailcat.Link.Tests;

/// <summary>
/// Covers what counts as a session being alive: an answer to a ping, or any
/// bytes moving on it at all.
/// </summary>
/// <remarks>
/// On a link saturated by a large exchange, a ping's answer queues behind the
/// very bytes that prove the other machine is there. Condemning the session
/// for that ended the exchange it was carrying — a large upload dropped at 0%,
/// again and again, reported as "sent nothing". These run a session over a
/// connection that never answers anything, so the only thing that can keep it
/// alive is movement this test reports by hand.
/// </remarks>
public class SessionLivenessTests
{
    private static readonly TimeSpan Window = TimeSpan.FromMilliseconds(500);

    private static LinkSession SessionOver(ITailcatConnection connection) =>
        new(
            connection,
            handler: () => null,
            channels: _ => null,
            new ExchangeLedger(LinkProtocol.ExchangeRetention, TimeProvider.System),
            new ExchangeRegistry(
                () => null, () => null, LinkProtocol.TransferRetention, Window, TimeProvider.System, CancellationToken.None),
            PeerCapabilitiesCodec.ThisBuild,
            requestTimeout: Window,
            TimeProvider.System,
            CancellationToken.None);

    /// <summary>
    /// A heartbeat outpaced by the session's own traffic does not end the
    /// session; the same silence with nothing moving does.
    /// </summary>
    [Fact]
    public async Task AHeartbeatOutpacedByTheSessionsOwnTrafficDoesNotEndIt()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using LinkSession session = SessionOver(new SilentConnection());
        session.Start();

        using (CancellationTokenSource busy = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            Task moving = KeepMovingAsync(session, busy.Token);
            await Assert.ThrowsAsync<SessionBusyException>(() => session.PingAsync(ct));
            Assert.False(session.Ended.IsCompleted, "a busy session was ended by its heartbeat");
            await busy.CancelAsync();
            await moving;
        }

        await Task.Delay(Window * 2, ct);
        await Assert.ThrowsAsync<LinkException>(() => session.PingAsync(ct));
        Assert.True(session.Ended.IsCompleted, "a silent session with nothing moving was kept");
    }

    /// <summary>
    /// One exchange falling silent on a session that other bytes are moving
    /// on ends that attempt, not the session and everything else it carries.
    /// </summary>
    [Fact]
    public async Task AnExchangeThatFallsSilentOnABusySessionDoesNotEndIt()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using LinkSession session = SessionOver(new SilentConnection());
        session.Start();

        OutboundExchange exchange = new(
            LinkContent.FromString("anything"), ExchangeFlags.Answer, Window, TimeProvider.System);
        using CancellationTokenSource busy = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task moving = KeepMovingAsync(session, busy.Token);

        await Assert.ThrowsAsync<LinkException>(() => new ExchangeAttempt(session, TimeProvider.System).RunAsync(exchange, ct));
        Assert.False(session.Ended.IsCompleted, "one silent exchange ended a session that was moving other bytes");

        await busy.CancelAsync();
        await moving;
    }

    /// <summary>
    /// Sending into a machine that has gone does not look like traffic: every
    /// new stream has credit a dead peer never granted, so a write counts only
    /// once the peer has sent something on the same stream.
    /// </summary>
    [Fact]
    public async Task WritesNobodyHasAnsweredDoNotCountAsMovement()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        int moved = 0;
        await using MovingStream stream = new(new MemoryStream(), () => moved++);

        await stream.WriteAsync(new byte[] { 1, 2, 3 }, ct);
        Assert.Equal(0, moved);
    }

    /// <summary>
    /// Once the peer has answered on a stream, writes there are paced by that
    /// peer and count.
    /// </summary>
    [Fact]
    public async Task WritesAfterThePeerHasAnsweredCountAsMovement()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        int moved = 0;
        await using MovingStream stream = new(PeerHasSent(7), () => moved++);

        _ = await stream.ReadAsync(new byte[1], ct);
        await stream.WriteAsync(new byte[] { 1 }, ct);
        Assert.Equal(2, moved);
    }

    /// <summary>
    /// The other machine's ping, read and answered, is not movement: it would
    /// otherwise excuse the silence of this machine's own ping when packets
    /// are lost in one direction only.
    /// </summary>
    [Fact]
    public async Task AStreamThePeerOpensWithAPingDoesNotCountAsMovement()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        int moved = 0;
        await using MovingStream stream = new(
            PeerHasSent((byte)LinkFrameKind.Ping, 0, 0),
            () => moved++,
            countsStreamStartingWith: tag => tag != (byte)LinkFrameKind.Ping);

        _ = await stream.ReadAsync(new byte[3], ct);
        await stream.WriteAsync(new byte[] { 0 }, ct);
        Assert.Equal(0, moved);
    }

    /// <summary>A stream the peer has already sent <paramref name="bytes"/> on, which still takes writes.</summary>
    private static MemoryStream PeerHasSent(params byte[] bytes)
    {
        MemoryStream stream = new();
        stream.Write(bytes);
        stream.Position = 0;
        return stream;
    }

    private static async Task KeepMovingAsync(LinkSession session, CancellationToken stop)
    {
        try
        {
            while (true)
            {
                session.Movement.NoteMoved();
                await Task.Delay(TimeSpan.FromMilliseconds(50), stop);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>A connection whose streams take everything and answer nothing.</summary>
    private sealed class SilentConnection : ITailcatConnection
    {
        public NodePublic Peer { get; } = NodePrivate.NewKey().Public();

        public PeerPath CurrentPath { get; } = new(PeerPathKind.Relay, null, null, default, 1024);

        public IReadOnlyList<PeerPath> Paths => [CurrentPath];

        public event Action<PeerPath>? PathChanged
        {
            add { }
            remove { }
        }

        public Task<Stream> OpenStreamAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<Stream>(new SilentStream());

        public async Task<Stream> AcceptStreamAsync(CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Accepts every write and never has anything to read.</summary>
    private sealed class SilentStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
        }
    }
}

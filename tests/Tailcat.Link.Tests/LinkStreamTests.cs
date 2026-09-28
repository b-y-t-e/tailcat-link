// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using Tailcat.Link.Storage;
using Tailcat.Net;

namespace Tailcat.Link.Tests;

using static LinkHarness;

/// <summary>
/// Covers the two-way stream: an ordinary <see cref="Stream"/> both ways, a
/// half-close, and endings that are never mistaken for one another.
/// </summary>
/// <remarks>
/// The case it exists for is a tunnel. There, a stream that stopped must not
/// look like one that finished — a truncated download handed on as complete is
/// the failure — so most of these are about how a stream ends.
/// </remarks>
public class LinkStreamTests
{
    /// <summary>
    /// Bytes cross both ways at once and in order, larger than one piece, and
    /// completing writes ends only that direction: the echo keeps answering
    /// after the request has ended, which is the half-close a request/response
    /// protocol over a tunnel needs.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BytesCrossBothWaysAndCompletingWritesEndsOnlyOneDirection(bool relayed)
    {
        using CancellationTokenSource cts = Deadline(TimeSpan.FromMinutes(2));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        // relay1 is where a stream is framed into records and closed with a FIN,
        // and QUIC where a close reaches a writer before the reader: both, then.
        FakeRelayGatewayFactory gateways = relayed ? new(relay, [PeerTransport.Relay1]) : new(relay);
        await using ILinkHost host = await TailcatLink.HostManyAsync(
            "demo", OptionsFor(gateways, new InMemoryLinkStore()), ct);
        host.OnStream("echo", (_, stream, token) => stream.CopyToAsync(stream, token));

        await using ILink phone = await JoinAsync(gateways, host, ct);
        await host.WaitForPeerAsync(ct);

        // Past a piece, so the chunking is exercised both ways.
        byte[] sent = new byte[700 * 1024];
        Random.Shared.NextBytes(sent);

        await using LinkStream echo = await phone.OpenStreamAsync("echo", ct);
        // Read while writing: an echo paces its reading on this end's, as a
        // socket would, so a writer that never reads would wait for good.
        Task<byte[]> answer = ReadToEndAsync(echo, ct);
        await echo.WriteAsync(sent, ct);
        await echo.CompleteWritesAsync(ct);

        Assert.Equal(sent, await answer.WaitAsync(ct));
        Assert.Equal(0, await echo.ReadAsync(new byte[1], ct));
    }

    /// <summary>
    /// A stream opened by the host into the machine that joined works the same
    /// way: after pairing both ends are equal.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheHostCanOpenAStreamIntoTheMachineThatJoined(bool relayed)
    {
        using CancellationTokenSource cts = Deadline(TimeSpan.FromMinutes(2));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        // relay1 is where a stream is framed into records and closed with a FIN,
        // and QUIC where a close reaches a writer before the reader: both, then.
        FakeRelayGatewayFactory gateways = relayed ? new(relay, [PeerTransport.Relay1]) : new(relay);
        await using ILinkHost host = await TailcatLink.HostManyAsync(
            "demo", OptionsFor(gateways, new InMemoryLinkStore()), ct);

        await using ILink phone = await JoinAsync(gateways, host, ct);
        phone.OnStream("greet", async (stream, token) =>
        {
            byte[] name = await ReadToEndAsync(stream, token);
            await stream.WriteAsync("hello "u8.ToArray().Concat(name).ToArray(), token);
        });
        ILinkPeer joined = await host.WaitForPeerAsync(ct);

        await using LinkStream greet = await joined.OpenStreamAsync("greet", ct);
        await greet.WriteAsync("host"u8.ToArray(), ct);
        await greet.CompleteWritesAsync(ct);

        Assert.Equal("hello host"u8.ToArray(), await ReadToEndAsync(greet, ct));
    }

    /// <summary>
    /// A handler that throws aborts its stream, and the other end hears an
    /// abort rather than a clean end after the bytes that did arrive.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AHandlerThatThrowsIsAnAbortNotAnEnd(bool relayed)
    {
        using CancellationTokenSource cts = Deadline(TimeSpan.FromMinutes(2));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        // relay1 is where a stream is framed into records and closed with a FIN,
        // and QUIC where a close reaches a writer before the reader: both, then.
        FakeRelayGatewayFactory gateways = relayed ? new(relay, [PeerTransport.Relay1]) : new(relay);
        await using ILinkHost host = await TailcatLink.HostManyAsync(
            "demo", OptionsFor(gateways, new InMemoryLinkStore()), ct);
        host.OnStream("download", async (_, stream, token) =>
        {
            await stream.WriteAsync("the first half"u8.ToArray(), token);
            throw new InvalidOperationException("the disk went away");
        });

        await using ILink phone = await JoinAsync(gateways, host, ct);
        await using LinkStream download = await phone.OpenStreamAsync("download", ct);

        byte[] first = new byte["the first half".Length];
        await download.ReadExactlyAsync(first, ct);
        LinkStreamException ended = await Assert.ThrowsAsync<LinkStreamException>(
            async () => await download.ReadExactlyAsync(new byte[1], ct));
        Assert.Equal(LinkStreamEnding.PeerAborted, ended.Ending);
    }

    /// <summary>
    /// A handler that gives up through its own cancellation — a timeout, a
    /// connect it abandoned — has failed as surely as one that threw anything
    /// else, and the other end hears an abort rather than a clean end.
    /// </summary>
    [Fact]
    public async Task AHandlersOwnCancellationIsAnAbortNotAnEnd()
    {
        using CancellationTokenSource cts = Deadline(TimeSpan.FromMinutes(2));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        FakeRelayGatewayFactory gateways = new(relay);
        await using ILinkHost host = await TailcatLink.HostManyAsync(
            "demo", OptionsFor(gateways, new InMemoryLinkStore()), ct);
        host.OnStream("download", async (_, stream, token) =>
        {
            await stream.WriteAsync("the first half"u8.ToArray(), token);
            throw new TaskCanceledException("the upstream connect timed out");
        });

        await using ILink phone = await JoinAsync(gateways, host, ct);
        await using LinkStream download = await phone.OpenStreamAsync("download", ct);

        await download.ReadExactlyAsync(new byte["the first half".Length], ct);
        LinkStreamException ended = await Assert.ThrowsAsync<LinkStreamException>(
            async () => await download.ReadExactlyAsync(new byte[1], ct));
        Assert.Equal(LinkStreamEnding.PeerAborted, ended.Ending);
    }

    /// <summary>
    /// An end that aborts is told so on its own reads and writes, and the
    /// other end on both of its: a write that nobody will read fails instead
    /// of waiting on flow control until the session goes.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnAbortReachesTheOtherEndsWritesToo(bool relayed)
    {
        using CancellationTokenSource cts = Deadline(TimeSpan.FromMinutes(2));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        // relay1 is where a stream is framed into records and closed with a FIN,
        // and QUIC where a close reaches a writer before the reader: both, then.
        FakeRelayGatewayFactory gateways = relayed ? new(relay, [PeerTransport.Relay1]) : new(relay);
        await using ILinkHost host = await TailcatLink.HostManyAsync(
            "demo", OptionsFor(gateways, new InMemoryLinkStore()), ct);
        TaskCompletionSource<LinkStreamEnding> uploader = new(TaskCreationOptions.RunContinuationsAsynchronously);
        host.OnStream("upload", async (_, stream, token) =>
        {
            byte[] block = new byte[64 * 1024];
            try
            {
                while (true)
                {
                    await stream.WriteAsync(block, token);
                }
            }
            catch (LinkStreamException ex)
            {
                uploader.TrySetResult(ex.Ending);
            }
        });

        await using ILink phone = await JoinAsync(gateways, host, ct);
        LinkStream upload = await phone.OpenStreamAsync("upload", ct);
        await upload.ReadExactlyAsync(new byte[1024], ct);
        await upload.AbortAsync();

        Assert.Equal(LinkStreamEnding.PeerAborted, await uploader.Task.WaitAsync(ct));
        LinkStreamException local = await Assert.ThrowsAsync<LinkStreamException>(
            async () => await upload.WriteAsync(new byte[1], ct));
        Assert.Equal(LinkStreamEnding.Aborted, local.Ending);
        await upload.DisposeAsync();
    }

    /// <summary>
    /// An end that only writes, holding bytes it never read, still hears an
    /// abort at once: its read-ahead waits for the application only before a
    /// piece's body, never before the next header.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnAbortReachesAWriterThatNeverReads(bool relayed)
    {
        using CancellationTokenSource cts = Deadline(TimeSpan.FromMinutes(2));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        FakeRelayGatewayFactory gateways = relayed ? new(relay, [PeerTransport.Relay1]) : new(relay);
        await using ILinkHost host = await TailcatLink.HostManyAsync(
            "demo", OptionsFor(gateways, new InMemoryLinkStore()), ct);
        TaskCompletionSource<LinkStreamEnding> uploader = new(TaskCreationOptions.RunContinuationsAsynchronously);
        host.OnStream("upload", async (_, stream, token) =>
        {
            byte[] block = new byte[64 * 1024];
            try
            {
                while (true)
                {
                    await stream.WriteAsync(block, token);
                }
            }
            catch (LinkStreamException ex)
            {
                uploader.TrySetResult(ex.Ending);
            }
        });

        await using ILink phone = await JoinAsync(gateways, host, ct);
        LinkStream upload = await phone.OpenStreamAsync("upload", ct);
        await upload.WriteAsync("never read"u8.ToArray(), ct);
        await upload.ReadExactlyAsync(new byte[1024], ct);
        Task aborting = upload.AbortAsync();

        // Well inside the patience an unheard abort waits out.
        Assert.Equal(LinkStreamEnding.PeerAborted, await uploader.Task.WaitAsync(TimeSpan.FromSeconds(5), ct));
        await aborting.WaitAsync(ct);
        await upload.DisposeAsync();
    }

    /// <summary>
    /// An end that disposes while the other is still writing ends the stream
    /// the way closing a socket does: the writer is told the stream was closed,
    /// and nothing it wrote before is taken for lost.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposingWhileThePeerStillWritesTellsTheWriterItWasClosed(bool relayed)
    {
        using CancellationTokenSource cts = Deadline(TimeSpan.FromMinutes(2));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        // relay1 is where a stream is framed into records and closed with a FIN,
        // and QUIC where a close reaches a writer before the reader: both, then.
        FakeRelayGatewayFactory gateways = relayed ? new(relay, [PeerTransport.Relay1]) : new(relay);
        await using ILinkHost host = await TailcatLink.HostManyAsync(
            "demo", OptionsFor(gateways, new InMemoryLinkStore()), ct);
        // Takes the first bytes and returns, which disposes the stream.
        host.OnStream("sink", async (_, stream, token) =>
            await stream.ReadExactlyAsync(new byte[16], token));

        await using ILink phone = await JoinAsync(gateways, host, ct);
        await using LinkStream sink = await phone.OpenStreamAsync("sink", ct);

        byte[] block = new byte[64 * 1024];
        LinkStreamException closed = await Assert.ThrowsAsync<LinkStreamException>(async () =>
        {
            while (true)
            {
                await sink.WriteAsync(block, ct);
            }
        });
        Assert.Equal(LinkStreamEnding.PeerClosed, closed.Ending);
        // The host wrote nothing and ended its half on the way out: a clean end.
        Assert.Equal(0, await sink.ReadAsync(new byte[1], ct));
    }

    /// <summary>
    /// An end that only writes hears the other close even while pieces that
    /// end sent before closing are still unread, and reads them afterwards.
    /// </summary>
    /// <remarks>
    /// More pieces than the reading end holds at once, so its read-ahead is
    /// pacing when the close comes: on relay1 as on QUIC, the writer hears
    /// the other end let go from the transport and the ending from the marker.
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AWriterHearsACloseBehindPiecesItHasNotRead(bool relayed)
    {
        using CancellationTokenSource cts = Deadline(TimeSpan.FromMinutes(2));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        FakeRelayGatewayFactory gateways = relayed ? new(relay, [PeerTransport.Relay1]) : new(relay);
        await using ILinkHost host = await TailcatLink.HostManyAsync(
            "demo", OptionsFor(gateways, new InMemoryLinkStore()), ct);
        // More pieces than the reading end holds at once, then a close.
        host.OnStream("banner", async (_, stream, token) =>
        {
            await stream.ReadExactlyAsync(new byte[1], token);
            foreach (string piece in (string[])["first ", "second ", "third ", "fourth"])
            {
                await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(piece), token);
            }
        });

        await using ILink phone = await JoinAsync(gateways, host, ct);
        await using LinkStream banner = await phone.OpenStreamAsync("banner", ct);

        byte[] block = new byte[64 * 1024];
        LinkStreamException closed = await Assert.ThrowsAsync<LinkStreamException>(async () =>
        {
            while (true)
            {
                await banner.WriteAsync(block, ct);
            }
        });
        Assert.Equal(LinkStreamEnding.PeerClosed, closed.Ending);
        Assert.Equal("first second third fourth"u8.ToArray(), await ReadToEndAsync(banner, ct));
    }

    /// <summary>
    /// A writer told the other end closed still reads everything that end sent
    /// before closing, however late it starts reading: what it wrote was all
    /// there was, and it arrived.
    /// </summary>
    /// <remarks>
    /// Both transports tell a writer the other end let go before its reader
    /// has read up to it.
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AWriterToldOfACloseStillReadsEverythingSentBeforeIt(bool relayed)
    {
        using CancellationTokenSource cts = Deadline(TimeSpan.FromMinutes(2));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        FakeRelayGatewayFactory gateways = relayed ? new(relay, [PeerTransport.Relay1]) : new(relay);
        await using ILinkHost host = await TailcatLink.HostManyAsync(
            "demo", OptionsFor(gateways, new InMemoryLinkStore()), ct);
        int piece = Tailcat.Link.Protocol.TransferFrame.BlockBytes;
        // Several pieces, then an end, then a close once this end starts writing.
        byte[] answer = [.. Enumerable.Range(0, 4 * piece + 17).Select(i => (byte)(i % 251))];
        host.OnStream("talker", async (_, stream, token) =>
        {
            await stream.WriteAsync(answer, token);
            await stream.CompleteWritesAsync(token);
            await stream.ReadExactlyAsync(new byte[16], token);
        });

        await using ILink phone = await JoinAsync(gateways, host, ct);
        await using LinkStream talker = await phone.OpenStreamAsync("talker", ct);

        // Enough read for the host to finish its writes, and the last two pieces left
        // unread until after this end has heard the close: one held by the
        // read-ahead, and one behind it in the transport.
        byte[] early = new byte[3 * piece];
        await talker.ReadExactlyAsync(early, ct);

        byte[] block = new byte[64 * 1024];
        LinkStreamException closed = await Assert.ThrowsAsync<LinkStreamException>(async () =>
        {
            while (true)
            {
                await talker.WriteAsync(block, ct);
            }
        });
        Assert.Equal(LinkStreamEnding.PeerClosed, closed.Ending);
        byte[] rest = await ReadToEndAsync(talker, ct);
        Assert.Equal(answer, early.Concat(rest).ToArray());
    }

    /// <summary>
    /// A close behind more pieces than the other end holds is still a close,
    /// and everything sent before it is still read.
    /// </summary>
    /// <remarks>
    /// The pair of <see cref="AnAbortBehindPiecesNobodyReadIsStillAnAbort"/>:
    /// both endings are out of the read-ahead's sight behind pieces nobody has
    /// taken, and the writer is told of them by the transport alone, which
    /// says the same thing for either. Letting the read-ahead past its pacing
    /// is what tells them apart, and this is the half that a writer taking
    /// every ending for an abort would fail.
    /// </remarks>
    [Fact]
    public async Task ACloseBehindPiecesNobodyReadIsStillAClose()
    {
        using CancellationTokenSource cts = Deadline(TimeSpan.FromMinutes(2));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        FakeRelayGatewayFactory gateways = new(relay);
        await using ILinkHost host = await TailcatLink.HostManyAsync(
            "demo", OptionsFor(gateways, new InMemoryLinkStore()), ct);
        host.OnStream("banner", async (_, stream, token) =>
        {
            await stream.ReadExactlyAsync(new byte[1], token);
            await stream.WriteAsync("first "u8.ToArray(), token);
            await stream.WriteAsync("second "u8.ToArray(), token);
            await stream.WriteAsync("third"u8.ToArray(), token);
        });

        await using ILink phone = await JoinAsync(gateways, host, ct);
        await using LinkStream banner = await phone.OpenStreamAsync("banner", ct);

        byte[] block = new byte[64 * 1024];
        LinkStreamException closed = await Assert.ThrowsAsync<LinkStreamException>(async () =>
        {
            while (true)
            {
                await banner.WriteAsync(block, ct);
            }
        });
        Assert.Equal(LinkStreamEnding.PeerClosed, closed.Ending);
        Assert.Equal("first second third"u8.ToArray(), await ReadToEndAsync(banner, ct));
    }

    /// <summary>
    /// A close behind pieces nobody read, followed by the session ending before
    /// they are read, is the session ending: the pieces it took with it must
    /// not be followed by a clean end, or a tunnel would finish a cut connection.
    /// </summary>
    [Fact]
    public async Task PiecesLostWithTheSessionAfterACloseAreNotACleanEnd()
    {
        using CancellationTokenSource cts = Deadline(TimeSpan.FromMinutes(2));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        FakeRelayGatewayFactory gateways = new(relay);
        await using ILinkHost host = await TailcatLink.HostManyAsync(
            "demo", OptionsFor(gateways, new InMemoryLinkStore()), ct);
        host.OnStream("banner", async (_, stream, token) =>
        {
            await stream.ReadExactlyAsync(new byte[1], token);
            await stream.WriteAsync("first "u8.ToArray(), token);
            await stream.WriteAsync("second "u8.ToArray(), token);
            await stream.WriteAsync("third"u8.ToArray(), token);
        });

        ILink phone = await JoinAsync(gateways, host, ct);
        await using LinkStream banner = await phone.OpenStreamAsync("banner", ct);

        byte[] block = new byte[64 * 1024];
        LinkStreamException closed = await Assert.ThrowsAsync<LinkStreamException>(async () =>
        {
            while (true)
            {
                await banner.WriteAsync(block, ct);
            }
        });
        Assert.Equal(LinkStreamEnding.PeerClosed, closed.Ending);

        await phone.DisposeAsync();

        LinkStreamException cut = await Assert.ThrowsAsync<LinkStreamException>(() => ReadToEndAsync(banner, ct));
        Assert.Equal(LinkStreamEnding.SessionEnded, cut.Ending);
    }

    /// <summary>
    /// An abort behind pieces the other end has not read is still an abort,
    /// even when the aborting machine gave up waiting to be heard and let go
    /// of the transport first.
    /// </summary>
    /// <remarks>
    /// QUIC only, because there a writer hears the other end let go before its
    /// own read-ahead has read what came before it. The read-ahead holds two
    /// pieces at most and then paces the other end, so past that the abort
    /// marker is out of its sight: reported from the transport alone, letting
    /// go looks exactly like a close, and a tunnel told of a close sends a
    /// clean end for a connection that was cut.
    /// </remarks>
    [Fact]
    public async Task AnAbortBehindPiecesNobodyReadIsStillAnAbort()
    {
        using CancellationTokenSource cts = Deadline(TimeSpan.FromMinutes(2));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        FakeRelayGatewayFactory gateways = new(relay);
        await using ILinkHost host = await TailcatLink.HostManyAsync(
            "demo", OptionsFor(gateways, new InMemoryLinkStore()), ct);
        // More pieces than the reading end holds at once, so its read-ahead is
        // waiting for the application when the abort marker goes out, and the
        // patience the abort waits out runs through before it is read.
        host.OnStream("cut", async (_, stream, token) =>
        {
            await stream.ReadExactlyAsync(new byte[1], token);
            await stream.WriteAsync("first"u8.ToArray(), token);
            await stream.WriteAsync("second"u8.ToArray(), token);
            await stream.WriteAsync("third"u8.ToArray(), token);
            await stream.AbortAsync();
        });

        await using ILink phone = await JoinAsync(gateways, host, ct);
        await using LinkStream cut = await phone.OpenStreamAsync("cut", ct);

        byte[] block = new byte[64 * 1024];
        LinkStreamException ended = await Assert.ThrowsAsync<LinkStreamException>(async () =>
        {
            while (true)
            {
                await cut.WriteAsync(block, ct);
            }
        });
        Assert.Equal(LinkStreamEnding.PeerAborted, ended.Ending);
    }

    /// <summary>
    /// A stream nobody is listening for is refused outright, in the words a
    /// channel is.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AStreamNobodyTakesIsRefused(bool relayed)
    {
        using CancellationTokenSource cts = Deadline(TimeSpan.FromMinutes(2));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        // relay1 is where a stream is framed into records and closed with a FIN,
        // and QUIC where a close reaches a writer before the reader: both, then.
        FakeRelayGatewayFactory gateways = relayed ? new(relay, [PeerTransport.Relay1]) : new(relay);
        await using ILinkHost host = await TailcatLink.HostManyAsync(
            "demo", OptionsFor(gateways, new InMemoryLinkStore()), ct);

        await using ILink phone = await JoinAsync(gateways, host, ct);
        await host.WaitForPeerAsync(ct);

        RemoteHandlerException refused = await Assert.ThrowsAsync<RemoteHandlerException>(
            async () => await phone.OpenStreamAsync("tunnel", ct));
        Assert.Contains("\"tunnel\" stream", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A stream goes with the session that carried it, and says so on both
    /// ends rather than going quiet: a tunnel whose far side vanished must
    /// close its own connection, not hold it open.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AStreamEndsWithItsSession(bool relayed)
    {
        using CancellationTokenSource cts = Deadline(TimeSpan.FromMinutes(2));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        // relay1 is where a stream is framed into records and closed with a FIN,
        // and QUIC where a close reaches a writer before the reader: both, then.
        FakeRelayGatewayFactory gateways = relayed ? new(relay, [PeerTransport.Relay1]) : new(relay);
        ILinkHost host = await TailcatLink.HostManyAsync(
            "demo", OptionsFor(gateways, new InMemoryLinkStore()), ct);
        TaskCompletionSource opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
        host.OnStream("tunnel", async (_, stream, token) =>
        {
            opened.TrySetResult();
            await ReadToEndAsync(stream, token);
        });

        await using ILink phone = await JoinAsync(gateways, host, ct);
        await using LinkStream tunnel = await phone.OpenStreamAsync("tunnel", ct);
        await tunnel.WriteAsync(new byte[] { 1 }, ct);
        await opened.Task.WaitAsync(ct);

        await host.DisposeAsync();

        LinkStreamException ended = await Assert.ThrowsAsync<LinkStreamException>(
            async () => await tunnel.ReadExactlyAsync(new byte[1], ct));
        Assert.Equal(LinkStreamEnding.SessionEnded, ended.Ending);
    }

    /// <summary>
    /// A write held up because the other end stopped reading still returns on
    /// its caller's cancellation, and a stream cut part-way through a piece is
    /// not written into again.
    /// </summary>
    [Fact]
    public async Task AWriteBlockedOnFlowControlHearsItsCallersCancellation()
    {
        using CancellationTokenSource cts = Deadline(TimeSpan.FromMinutes(2));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        FakeRelayGatewayFactory gateways = new(relay);
        await using ILinkHost host = await TailcatLink.HostManyAsync(
            "demo", OptionsFor(gateways, new InMemoryLinkStore()), ct);
        TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        host.OnStream("stalled", (_, _, token) => released.Task.WaitAsync(token));

        await using ILink phone = await JoinAsync(gateways, host, ct);
        await using LinkStream stalled = await phone.OpenStreamAsync("stalled", ct);
        using CancellationTokenSource giveUp = CancellationTokenSource.CreateLinkedTokenSource(ct);
        giveUp.CancelAfter(TimeSpan.FromSeconds(1));

        byte[] block = new byte[1024 * 1024];
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            while (true)
            {
                await stalled.WriteAsync(block, giveUp.Token);
            }
        });

        LinkStreamException after = await Assert.ThrowsAsync<LinkStreamException>(
            async () => await stalled.WriteAsync(new byte[1], ct));
        Assert.Equal(LinkStreamEnding.Aborted, after.Ending);
        released.TrySetResult();
    }

    /// <summary>
    /// Disposing twice does nothing the second time, as with every
    /// <see cref="IAsyncDisposable"/> here.
    /// </summary>
    [Fact]
    public async Task DisposingTwiceIsHarmless()
    {
        using CancellationTokenSource cts = Deadline(TimeSpan.FromMinutes(2));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        FakeRelayGatewayFactory gateways = new(relay);
        await using ILinkHost host = await TailcatLink.HostManyAsync(
            "demo", OptionsFor(gateways, new InMemoryLinkStore()), ct);
        host.OnStream("echo", (_, stream, token) => stream.CopyToAsync(stream, token));

        await using ILink phone = await JoinAsync(gateways, host, ct);
        LinkStream echo = await phone.OpenStreamAsync("echo", ct);

        await echo.DisposeAsync();
        await echo.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await echo.WriteAsync(new byte[1], ct));
    }

    /// <summary>
    /// A read waiting when its stream is disposed is told so, rather than
    /// waiting for bytes nobody will hand it.
    /// </summary>
    [Fact]
    public async Task AReadWaitingWhenTheStreamIsDisposedDoesNotHang()
    {
        using CancellationTokenSource cts = Deadline(TimeSpan.FromMinutes(2));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        FakeRelayGatewayFactory gateways = new(relay);
        await using ILinkHost host = await TailcatLink.HostManyAsync(
            "demo", OptionsFor(gateways, new InMemoryLinkStore()), ct);
        host.OnStream("silent", (_, stream, token) => ReadToEndAsync(stream, token));

        await using ILink phone = await JoinAsync(gateways, host, ct);
        LinkStream silent = await phone.OpenStreamAsync("silent", ct);
        Task<int> waiting = silent.ReadAsync(new byte[1], ct).AsTask();

        await silent.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => waiting.WaitAsync(ct));
    }

    private static async Task<ILink> JoinAsync(
        FakeRelayGatewayFactory gateways,
        ILinkHost host,
        CancellationToken ct)
    {
        ILink link = await TailcatLink.JoinAsync(
            "demo",
            host.InvitationCode.Value,
            OptionsFor(gateways, new InMemoryLinkStore()),
            ct);
        await link.WaitUntilConnectedAsync(ct);
        return link;
    }

    private static async Task<byte[]> ReadToEndAsync(Stream stream, CancellationToken ct)
    {
        using MemoryStream all = new();
        await stream.CopyToAsync(all, ct);
        return all.ToArray();
    }
}

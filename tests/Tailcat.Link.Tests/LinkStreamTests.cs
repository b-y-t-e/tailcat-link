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

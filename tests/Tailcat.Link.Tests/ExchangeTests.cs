// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Tailcat.Keys;
using Tailcat.Link.Protocol;
using Tailcat.Link.Storage;
using Tailcat.Link.Transport;
using Tailcat.Net;

namespace Tailcat.Link.Tests;

using static LinkHarness;

/// <summary>
/// Covers the one way content crosses a link: a request, a notification or a
/// transfer, of any size, carrying on in both directions from where it stopped
/// when a session dies, with the handler run once.
/// </summary>
/// <remarks>
/// Twenty-odd megabytes stands in for twenty gigabytes here. The blocks, the
/// resuming and the back-pressure behave the same at either size, and only
/// this size runs in CI in seconds.
/// </remarks>
public class ExchangeTests
{
    private const int Large = 24 * 1024 * 1024;

    // Given longer to stall than the shared harness allows: what these tests
    // break and resume is measured against it.
    private static LinkOptions OptionsFor(FakeRelayGatewayFactory gateways, ILinkStore store) =>
        LinkHarness.OptionsFor(gateways, store) with { TransferStallTimeout = TimeSpan.FromMinutes(1) };

    /// <summary>Compared by hash: a failed assertion on megabytes is unreadable.</summary>
    private static string Digest(ReadOnlySpan<byte> content) => Convert.ToHexString(SHA256.HashData(content));

    private static async Task<(ILink Host, ILink Peer)> PairAsync(
        FakeRelayGatewayFactory gateways,
        CancellationToken ct,
        LinkOptions? hostOptions = null,
        LinkOptions? peerOptions = null)
    {
        ILink host = await TailcatLink.HostAsync(
            "demo", hostOptions ?? OptionsFor(gateways, new InMemoryLinkStore()), ct);
        ILink peer = await TailcatLink.JoinAsync(
            "demo", host.InvitationCode.Value, peerOptions ?? OptionsFor(gateways, new InMemoryLinkStore()), ct);
        return (host, peer);
    }

    /// <summary>
    /// A request larger than a message could once be goes one way and an answer
    /// that size comes back, as content, over either transport — with what the
    /// sender said about it arriving beside it.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ARequestOfAnySizeGoesBothWaysAsContent(bool relayed)
    {
        using CancellationTokenSource cts = Deadline(TimeSpan.FromMinutes(3));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        FakeRelayGatewayFactory gateways = relayed ? new(relay, [PeerTransport.Relay1]) : new(relay);
        (ILink host, ILink peer) = await PairAsync(gateways, ct);
        await using (host)
        await using (peer)
        {
            TaskCompletionSource<(string Name, string Metadata)> described =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            host.OnRequest(async (request, token) =>
            {
                described.TrySetResult((request.Name, Convert.ToHexString(request.Metadata.Span)));
                byte[] whole = await request.ReadAllBytesAsync(token);
                Array.Reverse(whole);
                return LinkContent.FromBytes(whole) with { ContentType = "application/x-reversed" };
            });

            byte[] content = RandomNumberGenerator.GetBytes(Large);
            await using IncomingTransfer answer = await peer.RequestAsync(
                LinkContent.FromBytes(content) with { Name = "zrzut.bin", Metadata = new byte[] { 1, 2, 3 } },
                ct);

            Assert.Equal("application/x-reversed", answer.ContentType);
            Assert.Equal(Large, answer.Length);
            byte[] reply = await answer.ReadAllBytesAsync(ct);

            Array.Reverse(content);
            Assert.Equal(Digest(content), Digest(reply));
            Assert.Equal(("zrzut.bin", "010203"), await described.Task.WaitAsync(ct));
        }
    }

    /// <summary>
    /// A request whose content is cut by the link breaking carries on from
    /// where the other machine got to, into the handler already reading it.
    /// </summary>
    [Fact]
    public async Task ARequestBrokenMidContentCarriesOnIntoTheSameHandler()
    {
        using CancellationTokenSource cts = Deadline(TimeSpan.FromMinutes(3));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        FakeRelayGatewayFactory gateways = new(relay);
        (ILink host, ILink peer) = await PairAsync(gateways, ct);
        await using (host)
        await using (peer)
        {
            byte[] content = RandomNumberGenerator.GetBytes(Large);
            TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource broken = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<string> arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
            int handlerRuns = 0;

            host.OnRequest(async (request, token) =>
            {
                Interlocked.Increment(ref handlerRuns);
                arrived.TrySetResult(Digest(await ReadHoldingAfterFirstChunkAsync(request.Content, started, broken, token)));
                return LinkContent.FromString("done");
            });

            CountingStream source = new(new MemoryStream(content, writable: false));
            Task<IncomingTransfer> requesting = peer.RequestAsync(LinkContent.FromStream(source), ct);

            await started.Task.WaitAsync(ct);
            await gateways.BreakEveryNodeAsync();
            broken.TrySetResult();

            await using IncomingTransfer answer = await requesting.WaitAsync(ct);
            Assert.Equal("done", await answer.ReadAllTextAsync(ct));
            Assert.Equal(Digest(content), await arrived.Task.WaitAsync(ct));

            // Once, however many sessions it took; and carried on rather than
            // started again, which a count of what was read out of the content
            // tells apart from the outside.
            Assert.Equal(1, handlerRuns);
            Assert.True(gateways.NodesCreated > 2, "the link should have had to rebuild at least one node");
            Assert.True(
                source.TotalRead < content.Length * 3L / 2,
                $"{source.TotalRead} bytes were read to send {content.Length}: the request started again");
        }
    }

    /// <summary>
    /// An answer cut by the link breaking carries on from where this machine got
    /// to, and is sent again rather than made again.
    /// </summary>
    [Fact]
    public async Task AnAnswerBrokenMidwayCarriesOnAndIsNotMadeAgain()
    {
        using CancellationTokenSource cts = Deadline(TimeSpan.FromMinutes(3));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        FakeRelayGatewayFactory gateways = new(relay);
        (ILink host, ILink peer) = await PairAsync(gateways, ct);
        await using (host)
        await using (peer)
        {
            byte[] content = RandomNumberGenerator.GetBytes(Large);
            int handlerRuns = 0;
            host.OnRequest((_, _) =>
            {
                Interlocked.Increment(ref handlerRuns);
                return Task.FromResult(LinkContent.FromBytes(content));
            });

            await using IncomingTransfer answer = await peer.RequestAsync(LinkContent.FromString("the recording"), ct);
            TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource broken = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<byte[]> reading = ReadHoldingAfterFirstChunkAsync(answer.Content, started, broken, ct);

            await started.Task.WaitAsync(ct);
            await gateways.BreakEveryNodeAsync();
            broken.TrySetResult();

            Assert.Equal(Digest(content), Digest(await reading.WaitAsync(ct)));
            Assert.Equal(1, handlerRuns);
        }
    }

    /// <summary>
    /// A notification used to be lost when its session died. Now it arrives,
    /// and its handler runs once however many sessions that took.
    /// </summary>
    [Fact]
    public async Task ANotificationSurvivesTheLinkBreakingAndIsHandledOnce()
    {
        using CancellationTokenSource cts = Deadline(TimeSpan.FromMinutes(3));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        FakeRelayGatewayFactory gateways = new(relay);
        (ILink host, ILink peer) = await PairAsync(gateways, ct);
        await using (host)
        await using (peer)
        {
            byte[] content = RandomNumberGenerator.GetBytes(Large);
            TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource broken = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<string> arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
            int handlerRuns = 0;

            host.OnRequest(async (request, token) =>
            {
                Interlocked.Increment(ref handlerRuns);
                arrived.TrySetResult(Digest(await ReadHoldingAfterFirstChunkAsync(request.Content, started, broken, token)));
                return LinkContent.Empty;
            });

            Task notifying = peer.NotifyAsync(LinkContent.FromBytes(content), ct);

            await started.Task.WaitAsync(ct);
            await gateways.BreakEveryNodeAsync();
            broken.TrySetResult();

            await notifying.WaitAsync(ct);
            Assert.Equal(Digest(content), await arrived.Task.WaitAsync(ct));
            Assert.Equal(1, handlerRuns);
        }
    }

    /// <summary>
    /// An exchange with a machine that has gone for good ends, in bounded time,
    /// rather than waiting for a session that is never coming — which is what
    /// a transfer used to do.
    /// </summary>
    [Fact]
    public async Task AnExchangeWithAMachineThatIsGoneForGoodEndsInsteadOfWaiting()
    {
        using CancellationTokenSource cts = Deadline(TimeSpan.FromMinutes(2));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        FakeRelayGatewayFactory gateways = new(relay);
        LinkOptions impatient = OptionsFor(gateways, new InMemoryLinkStore()) with
        {
            TransferStallTimeout = TimeSpan.FromSeconds(3),
            RequestDeadline = TimeSpan.FromSeconds(3),
        };
        (ILink host, ILink peer) = await PairAsync(gateways, ct, peerOptions: impatient);
        await using (peer)
        {
            // Paired and up before the host goes, so that what is measured is
            // an exchange with a machine that has left, and not one with a
            // machine this end has not reached yet.
            await peer.WaitUntilConnectedAsync(ct);
            await host.DisposeAsync();

            byte[] content = RandomNumberGenerator.GetBytes(1024 * 1024);
            long startedAt = System.Diagnostics.Stopwatch.GetTimestamp();

            await Assert.ThrowsAsync<LinkTimeoutException>(
                () => peer.RequestAsync(LinkContent.FromBytes(content), ct));
            await Assert.ThrowsAsync<LinkTimeoutException>(
                () => peer.SendBytesAsync(content, offer: null, progress: null, ct));

            Assert.True(
                System.Diagnostics.Stopwatch.GetElapsedTime(startedAt) < TimeSpan.FromSeconds(60),
                "an exchange with a machine that is gone waited far past its patience");
        }
    }

    /// <summary>
    /// An answer read from a stream that cannot go back ends with a reason when
    /// a session dies under it, rather than arriving with a hole in it.
    /// </summary>
    [Fact]
    public async Task AnAnswerThatCannotBeReadAgainEndsPlainlyWhenASessionDiesUnderIt()
    {
        using CancellationTokenSource cts = Deadline(TimeSpan.FromMinutes(3));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        FakeRelayGatewayFactory gateways = new(relay);
        (ILink host, ILink peer) = await PairAsync(gateways, ct);
        await using (host)
        await using (peer)
        {
            byte[] content = RandomNumberGenerator.GetBytes(Large);
            host.OnRequest((_, _) =>
                Task.FromResult(LinkContent.FromStream(new ForwardOnlyStream(new MemoryStream(content)))));

            await using IncomingTransfer answer = await peer.RequestAsync(LinkContent.FromString("stream it"), ct);
            TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource broken = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<byte[]> reading = ReadHoldingAfterFirstChunkAsync(answer.Content, started, broken, ct);

            await started.Task.WaitAsync(ct);
            await gateways.BreakEveryNodeAsync();
            broken.TrySetResult();

            LinkException ended = await Assert.ThrowsAnyAsync<LinkException>(() => reading.WaitAsync(ct));
            Assert.Contains("rewound", ended.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Letting go of an answer before its end stops asking for it, leaves the
    /// link as it was, and does not make the handler run again.
    /// </summary>
    [Fact]
    public async Task LettingGoOfAnAnswerEarlyStopsItWithoutHarm()
    {
        using CancellationTokenSource cts = Deadline(TimeSpan.FromMinutes(2));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        FakeRelayGatewayFactory gateways = new(relay);
        (ILink host, ILink peer) = await PairAsync(gateways, ct);
        await using (host)
        await using (peer)
        {
            byte[] content = RandomNumberGenerator.GetBytes(Large);
            int largeRuns = 0;
            host.OnRequest(async (request, token) =>
            {
                if (await request.ReadAllTextAsync(token) == "ping")
                {
                    return LinkContent.FromString("pong");
                }
                Interlocked.Increment(ref largeRuns);
                return LinkContent.FromBytes(content);
            });

            IncomingTransfer answer = await peer.RequestAsync(LinkContent.FromString("everything"), ct);
            byte[] first = new byte[64 * 1024];
            await answer.Content.ReadExactlyAsync(first, ct);

            await answer.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30), ct);
            await answer.DisposeAsync();

            await using IncomingTransfer pong = await peer.RequestAsync(LinkContent.FromString("ping"), ct);
            Assert.Equal("pong", await pong.ReadAllTextAsync(ct));
            Assert.Equal(1, largeRuns);
        }
    }

    /// <summary>
    /// The browser client takes frames of any size but does not speak
    /// exchanges. It still gets requests and answers of any size, the way it
    /// can take them.
    /// </summary>
    [Fact]
    public async Task AMachineThatTakesLargeFramesButNotExchangesStillGetsEverything()
    {
        using CancellationTokenSource cts = Deadline(TimeSpan.FromMinutes(3));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        FakeRelayGatewayFactory gateways = new(relay);
        LinkOptions likeABrowser = OptionsFor(gateways, new InMemoryLinkStore()) with
        {
            AdvertisedCapabilities = PeerCapabilities.LargeFrames,
        };
        (ILink host, ILink peer) = await PairAsync(gateways, ct, hostOptions: likeABrowser);
        await using (host)
        await using (peer)
        {
            host.OnRequest((request, _) =>
            {
                byte[] reversed = request.ToArray();
                Array.Reverse(reversed);
                return Task.FromResult<ReadOnlyMemory<byte>>(reversed);
            });

            // Past the sixteen megabytes messages used to stop at.
            byte[] content = RandomNumberGenerator.GetBytes((16 * 1024 * 1024) + 1024);
            byte[] expected = (byte[])content.Clone();
            Array.Reverse(expected);

            Assert.Equal(Digest(expected), Digest(await peer.RequestAsync(content, ct)));

            await using IncomingTransfer answer = await peer.RequestAsync(LinkContent.FromBytes(content), ct);
            Assert.Equal(Digest(expected), Digest(await answer.ReadAllBytesAsync(ct)));
        }
    }

    /// <summary>
    /// The browser client takes no transfers, so one sent to a machine like it
    /// is refused at once with the reason rather than retried into silence.
    /// </summary>
    [Fact]
    public async Task AMachineWithoutExchangesIsRefusedATransferAtOnce()
    {
        using CancellationTokenSource cts = Deadline(TimeSpan.FromMinutes(2));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        FakeRelayGatewayFactory gateways = new(relay);
        LinkOptions likeABrowser = OptionsFor(gateways, new InMemoryLinkStore()) with
        {
            AdvertisedCapabilities = PeerCapabilities.LargeFrames,
        };
        (ILink host, ILink peer) = await PairAsync(gateways, ct, hostOptions: likeABrowser);
        await using (host)
        await using (peer)
        {
            RemoteHandlerException refused = await Assert.ThrowsAsync<RemoteHandlerException>(
                () => peer.SendBytesAsync(new byte[] { 1, 2, 3 },new TransferOffer { Name = "x.bin" }, progress: null, ct));
            Assert.Contains("does not take transfers", refused.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A machine that says it takes neither exchanges nor large frames is
    /// refused a request at once, rather than sent a shape it did not ask for.
    /// </summary>
    [Fact]
    public async Task AMachineThatSaysItTakesNothingIsRefusedAtOnce()
    {
        using CancellationTokenSource cts = Deadline(TimeSpan.FromMinutes(2));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        FakeRelayGatewayFactory gateways = new(relay);
        LinkOptions takingNothing = OptionsFor(gateways, new InMemoryLinkStore()) with
        {
            AdvertisedCapabilities = PeerCapabilities.None,
        };
        (ILink host, ILink peer) = await PairAsync(gateways, ct, hostOptions: takingNothing);
        await using (host)
        await using (peer)
        {
            RemoteHandlerException refused = await Assert.ThrowsAsync<RemoteHandlerException>(
                () => peer.RequestAsync(new byte[] { 1, 2, 3 }, ct));
            Assert.Contains("neither exchanges nor frames", refused.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>A host answers each of its peers as content, told which one is asking.</summary>
    [Fact]
    public async Task AHostAnswersEachPeerAsContent()
    {
        using CancellationTokenSource cts = Deadline(TimeSpan.FromMinutes(2));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        FakeRelayGatewayFactory gateways = new(relay);
        await using ILinkHost host = await TailcatLink.HostManyAsync(
            "demo", OptionsFor(gateways, new InMemoryLinkStore()) with { MaxPeers = 2 }, ct);
        host.SetRequestHandler(async (peer, request, token) =>
            LinkContent.FromString($"{peer.Name}: {await request.ReadAllTextAsync(token)}"));

        await using ILink kitchen = await TailcatLink.JoinAsync(
            "demo",
            host.InvitationCode.Value,
            new JoinRequest { DisplayName = "kitchen" },
            OptionsFor(gateways, new InMemoryLinkStore()),
            ct);

        await using IncomingTransfer answer = await kitchen.RequestAsync(LinkContent.FromString("hello"), ct);
        Assert.Equal("kitchen: hello", await answer.ReadAllTextAsync(ct));
    }

    /// <summary>
    /// The one read left with a bound: a machine nobody knows yet announcing
    /// an enormous hello is turned away before anything is allocated for it,
    /// and the host goes on letting the right machines in.
    /// </summary>
    [Fact]
    public async Task AStrangerAnnouncingAnEnormousHelloIsTurnedAwayAndTheHostCarriesOn()
    {
        using CancellationTokenSource cts = Deadline(TimeSpan.FromMinutes(2));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        FakeRelayGatewayFactory gateways = new(relay);
        CapturingLoggerFactory logs = new();
        await using ILink host = await TailcatLink.HostAsync(
            "demo", OptionsFor(gateways, new InMemoryLinkStore()) with { LoggerFactory = logs }, ct);
        host.OnRequest(_ => "pong");

        await using (INodeGateway stranger = await gateways.CreateAsync(NodePrivate.NewKey(), FakeRelayGatewayFactory.RegionId, ct))
        {
            ITailcatConnection connection = await stranger.ConnectAsync(host.InvitationCode.Address, ct);
            await using (connection)
            {
                Stream stream = await connection.OpenStreamAsync(ct);
                await using (stream)
                {
                    byte[] header = new byte[LinkFrame.HeaderLength];
                    header[0] = (byte)LinkFrameKind.Hello;
                    BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(LinkFrame.HeaderLength - 4), int.MaxValue);
                    await stream.WriteAsync(header, ct);
                    await stream.WriteAsync(new byte[1024], ct);
                    await stream.FlushAsync(ct);

                    while (!logs.Said(LogLevel.Information, $"at most {LinkProtocol.HelloFrameBytes} is read"))
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(50), ct);
                    }
                }
            }
        }

        await using ILink peer = await TailcatLink.JoinAsync(
            "demo", host.InvitationCode.Value, OptionsFor(gateways, new InMemoryLinkStore()), ct);
        Assert.Equal("pong", await peer.RequestAsync("ping", ct));
    }

    /// <summary>Letting go of content twice is as harmless as letting go of it once.</summary>
    [Fact]
    public async Task DisposingTwiceIsHarmless()
    {
        IncomingTransfer content = new(Guid.NewGuid(), "", "", length: null, metadata: default, TimeProvider.System);

        await content.DisposeAsync();
        await content.DisposeAsync();
    }

    /// <summary>
    /// Reads a stream to its end, stopping after the first chunk until told to
    /// go on — which holds the sender back, so that a break made meanwhile
    /// lands in the middle of the content rather than after it.
    /// </summary>
    private static async Task<byte[]> ReadHoldingAfterFirstChunkAsync(
        Stream content,
        TaskCompletionSource started,
        TaskCompletionSource broken,
        CancellationToken ct)
    {
        using MemoryStream sink = new();
        byte[] buffer = new byte[64 * 1024];
        bool first = true;
        while (true)
        {
            int read = await content.ReadAsync(buffer, ct);
            if (read == 0)
            {
                return sink.ToArray();
            }
            sink.Write(buffer, 0, read);
            if (first)
            {
                first = false;
                started.TrySetResult();
                await broken.Task.WaitAsync(ct);
            }
        }
    }

    /// <summary>Counts what was read out of a stream, however many times over.</summary>
    private sealed class CountingStream(Stream inner) : Stream
    {
        private long _read;

        public long TotalRead => Interlocked.Read(ref _read);

        public override bool CanRead => true;

        public override bool CanSeek => inner.CanSeek;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = inner.Read(buffer, offset, count);
            Interlocked.Add(ref _read, read);
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int read = await inner.ReadAsync(buffer, cancellationToken);
            Interlocked.Add(ref _read, read);
            return read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>A stream that only goes forwards, like a socket or a pipe.</summary>
    private sealed class ForwardOnlyStream(Stream inner) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}

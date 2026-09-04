// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Security.Cryptography;
using System.Text;
using Tailcat.Link.Storage;
using Tailcat.Net;

namespace Tailcat.Link.Tests;

/// <summary>
/// Covers the promise the transfer API makes: hand it a stream of any size
/// and it arrives, whatever the link does in the meantime.
/// </summary>
/// <remarks>
/// The sizes here are small — tens of megabytes, not the twenty gigabytes the
/// API is for — because what has to be proved is that nothing scales with the
/// content: the blocks, the resume, and the back-pressure behave the same at
/// either size, and only these run in CI in seconds.
/// </remarks>
public class TransferTests
{
    private static LinkOptions OptionsFor(FakeRelayGatewayFactory gateways, ILinkStore store) => new()
    {
        Store = store,
        Gateway = gateways,
        RequestTimeout = TimeSpan.FromSeconds(5),
        RequestDeadline = TimeSpan.FromSeconds(45),
        TransferStallTimeout = TimeSpan.FromMinutes(1),
        HeartbeatInterval = TimeSpan.FromSeconds(1),
        MinReconnectDelay = TimeSpan.FromMilliseconds(200),
        MaxReconnectDelay = TimeSpan.FromSeconds(2),
    };

    private static CancellationTokenSource Deadline(TimeSpan limit)
    {
        CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        cts.CancelAfter(limit);
        return cts;
    }

    /// <summary>Compared by hash: a failed assertion on twenty megabytes is unreadable.</summary>
    private static string Digest(ReadOnlySpan<byte> content) => Convert.ToHexString(SHA256.HashData(content));

    private static async Task<(ILink Host, ILink Peer)> PairAsync(
        FakeRelayGatewayFactory gateways,
        CancellationToken ct)
    {
        ILink host = await TailcatLink.HostAsync("demo", OptionsFor(gateways, new InMemoryLinkStore()), ct);
        ILink peer = await TailcatLink.JoinAsync(
            "demo", host.InvitationCode.Value, OptionsFor(gateways, new InMemoryLinkStore()), ct);
        return (host, peer);
    }

    /// <summary>
    /// The size a request cannot be. A transfer is a different thing from a
    /// message and the limit that makes a message safe — both machines hold
    /// all of it — is exactly what a transfer is designed not to need.
    /// </summary>
    [Fact]
    public async Task ContentTooLargeToBeARequestIsAnOrdinaryTransfer()
    {
        using CancellationTokenSource cts = Deadline(TimeSpan.FromMinutes(2));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        FakeRelayGatewayFactory gateways = new(relay);
        (ILink host, ILink peer) = await PairAsync(gateways, ct);
        await using (host)
        await using (peer)
        {
            byte[] content = RandomNumberGenerator.GetBytes(20 * 1024 * 1024);
            TaskCompletionSource<byte[]> arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<string> named = new(TaskCreationOptions.RunContinuationsAsynchronously);
            host.OnTransfer(async (transfer, token) =>
            {
                named.TrySetResult(transfer.Name);
                using MemoryStream sink = new();
                await transfer.CopyToAsync(sink, progress: null, token);
                arrived.TrySetResult(sink.ToArray());
            });

            List<TransferProgress> reported = [];
            await peer.SendBytesAsync(
                content,
                new TransferOffer { Name = "recording.bin", ContentType = "application/octet-stream" },
                new Progress<TransferProgress>(reported.Add),
                ct);

            Assert.Equal("recording.bin", await named.Task.WaitAsync(ct));
            Assert.Equal(Digest(content), Digest(await arrived.Task.WaitAsync(ct)));

            // The same bytes as a request are refused before they are sent:
            // a message that large is what this API exists instead of.
            LinkException tooLarge = await Assert.ThrowsAsync<LinkException>(
                async () => await peer.RequestAsync(content, ct));
            Assert.Contains("at most", tooLarge.Message, StringComparison.Ordinal);

            // Progress is per block and ends at the whole of it.
            Assert.NotEmpty(reported);
            Assert.Equal(content.Length, reported[^1].Transferred);
            Assert.Equal(1d, reported[^1].Fraction);
        }
    }

    /// <summary>
    /// The point of the whole design: a transfer is not a request that happens
    /// to be large. It crosses a link that broke in the middle of it, and it
    /// crosses from where it got to — into the same handler, which never
    /// learns that anything happened.
    /// </summary>
    [Fact]
    public async Task ATransferResumesMidContentAfterTheLinkBreaks()
    {
        using CancellationTokenSource cts = Deadline(TimeSpan.FromMinutes(3));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        FakeRelayGatewayFactory gateways = new(relay);
        (ILink host, ILink peer) = await PairAsync(gateways, ct);
        await using (host)
        await using (peer)
        {
            byte[] content = RandomNumberGenerator.GetBytes(24 * 1024 * 1024);
            TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource broken = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<byte[]> arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
            int handlerRuns = 0;

            host.OnTransfer(async (transfer, token) =>
            {
                Interlocked.Increment(ref handlerRuns);
                using MemoryStream sink = new();
                byte[] buffer = new byte[64 * 1024];
                bool first = true;
                while (true)
                {
                    int read = await transfer.Content.ReadAsync(buffer, token);
                    if (read == 0)
                    {
                        break;
                    }
                    sink.Write(buffer, 0, read);
                    if (first)
                    {
                        // Held here on purpose: with the reader stopped the
                        // sender cannot run away with the rest of the content,
                        // so the break below lands in the middle of it rather
                        // than after it.
                        first = false;
                        started.TrySetResult();
                        await broken.Task.WaitAsync(token);
                    }
                }
                arrived.TrySetResult(sink.ToArray());
            });

            Task sending = peer.SendBytesAsync(content, new TransferOffer { Name = "video.mkv" }, null, ct);

            await started.Task.WaitAsync(ct);
            await gateways.BreakEveryNodeAsync();
            broken.TrySetResult();

            await sending.WaitAsync(ct);
            Assert.Equal(Digest(content), Digest(await arrived.Task.WaitAsync(ct)));

            // Once, however many sessions it took: the handler is the link's,
            // not the session's, and a resumed transfer continues into it.
            Assert.Equal(1, handlerRuns);
            Assert.True(gateways.NodesCreated > 2, "the link should have had to rebuild at least one node");
        }
    }

    /// <summary>
    /// A file, which is what most transfers are, and the file name handled the
    /// way anything off a network has to be.
    /// </summary>
    [Fact]
    public async Task AFileArrivesAsAFileAndCannotNameItsOwnPath()
    {
        using CancellationTokenSource cts = Deadline(TimeSpan.FromMinutes(2));
        CancellationToken ct = cts.Token;

        string workspace = Directory.CreateTempSubdirectory("tailcat-transfer").FullName;
        try
        {
            byte[] content = RandomNumberGenerator.GetBytes(3 * 1024 * 1024);
            string source = Path.Combine(workspace, "source.bin");
            await File.WriteAllBytesAsync(source, content, ct);
            string inbox = Path.Combine(workspace, "inbox");

            await using FakeDerpRelay relay = new();
            FakeRelayGatewayFactory gateways = new(relay);
            (ILink host, ILink peer) = await PairAsync(gateways, ct);
            await using (host)
            await using (peer)
            {
                host.SaveTransfersTo(inbox);

                // A name that is a path into somewhere else, which is what a
                // peer is free to send and what SuggestedFileName is for.
                await peer.SendFileAsync(source, new TransferOffer { Name = "../../escaped.bin" }, null, ct);

                string[] written = Directory.GetFiles(inbox);
                Assert.Equal(Path.Combine(inbox, "escaped.bin"), Assert.Single(written));
                Assert.Equal(Digest(content), Digest(await File.ReadAllBytesAsync(written[0], ct)));
            }
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    /// <summary>
    /// A machine that is not taking transfers, and one whose handler threw,
    /// both answer rather than going quiet — and neither is retried, since
    /// sending it again would reach the same decision.
    /// </summary>
    [Fact]
    public async Task ARefusedTransferIsAnAnswerAndNotARetry()
    {
        using CancellationTokenSource cts = Deadline(TimeSpan.FromMinutes(2));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        FakeRelayGatewayFactory gateways = new(relay);
        (ILink host, ILink peer) = await PairAsync(gateways, ct);
        await using (host)
        await using (peer)
        {
            await peer.WaitUntilConnectedAsync(ct);

            RemoteHandlerException notReceiving = await Assert.ThrowsAsync<RemoteHandlerException>(
                async () => await peer.SendBytesAsync(Encoding.UTF8.GetBytes("anything"), null, null, ct));
            Assert.Contains("not receiving transfers", notReceiving.Message, StringComparison.Ordinal);

            int runs = 0;
            host.OnTransfer((_, _) =>
            {
                Interlocked.Increment(ref runs);
                throw new InvalidOperationException("the disk is full");
            });

            RemoteHandlerException refused = await Assert.ThrowsAsync<RemoteHandlerException>(
                async () => await peer.SendBytesAsync(Encoding.UTF8.GetBytes("anything"), null, null, ct));
            Assert.Contains("the disk is full", refused.Message, StringComparison.Ordinal);
            Assert.Equal(1, runs);
        }
    }

    /// <summary>
    /// Content that stops short of the length it announced is a broken
    /// transfer, and the receiving handler is told so rather than being handed
    /// half a file that reads like a whole one.
    /// </summary>
    [Fact]
    public async Task ContentThatRunsOutEarlyFailsRatherThanArrivingHalfDone()
    {
        using CancellationTokenSource cts = Deadline(TimeSpan.FromMinutes(2));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        FakeRelayGatewayFactory gateways = new(relay);
        (ILink host, ILink peer) = await PairAsync(gateways, ct);
        await using (host)
        await using (peer)
        {
            host.OnTransfer(async (transfer, token) =>
            {
                using MemoryStream sink = new();
                await transfer.CopyToAsync(sink, progress: null, token);
            });

            // A stream that is shorter than the offer says. The check that
            // catches this before the network is only possible when the
            // content can be measured, so this one goes through a stream that
            // cannot be.
            using MemoryStream shorter = new(RandomNumberGenerator.GetBytes(1024), writable: false);
            RemoteHandlerException truncated = await Assert.ThrowsAsync<RemoteHandlerException>(
                async () => await peer.SendAsync(
                    new ForwardOnlyStream(shorter),
                    new TransferOffer { Name = "half.bin", Length = 4096 },
                    null,
                    ct));
            Assert.Contains("1024 of the 4096", truncated.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The one case the API cannot rescue, said plainly instead of retried:
    /// a transfer whose content only goes forwards has nothing to resume from.
    /// </summary>
    [Fact]
    public async Task ContentThatCannotBeRewoundSaysSoWhenTheLinkBreaks()
    {
        using CancellationTokenSource cts = Deadline(TimeSpan.FromMinutes(3));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        FakeRelayGatewayFactory gateways = new(relay);
        (ILink host, ILink peer) = await PairAsync(gateways, ct);
        await using (host)
        await using (peer)
        {
            TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource broken = new(TaskCreationOptions.RunContinuationsAsynchronously);
            host.OnTransfer(async (transfer, token) =>
            {
                byte[] buffer = new byte[64 * 1024];
                bool first = true;
                while (await transfer.Content.ReadAsync(buffer, token) > 0)
                {
                    if (first)
                    {
                        first = false;
                        started.TrySetResult();
                        await broken.Task.WaitAsync(token);
                    }
                }
            });

            using MemoryStream content = new(RandomNumberGenerator.GetBytes(24 * 1024 * 1024), writable: false);
            Task sending = peer.SendAsync(
                new ForwardOnlyStream(content), new TransferOffer { Name = "live.raw" }, null, ct);

            await started.Task.WaitAsync(ct);
            await gateways.BreakEveryNodeAsync();
            broken.TrySetResult();

            LinkException lost = await Assert.ThrowsAsync<LinkException>(async () => await sending.WaitAsync(ct));
            Assert.Contains("cannot be rewound", lost.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>A stream with no length and no seeking, like a socket or a codec.</summary>
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

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

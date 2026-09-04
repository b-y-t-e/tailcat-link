// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using Tailcat.Link.Protocol;

namespace Tailcat.Link.Tests;

/// <summary>
/// The sending end's own bookkeeping: where the content has to go back to
/// when a session dies, which is not always where the peer got to.
/// </summary>
public class OutboundTransferTests
{
    private static OutboundTransfer Sending(byte[] content) =>
        new(
            new TransferOffer { Name = "plik", Length = content.Length },
            new MemoryStream(content),
            progress: null,
            TimeProvider.System);

    [Fact]
    public async Task ABlockReadButNeverAcknowledgedIsSentAgain()
    {
        OutboundTransfer transfer = Sending([1, 2, 3, 4]);
        byte[] block = new byte[2];

        await transfer.ReadAsync(block, TestContext.Current.CancellationToken);
        transfer.Advance(2);

        // Read, and then the session dies before the block is written: the
        // peer still has two bytes, but the content has given away four.
        await transfer.ReadAsync(block, TestContext.Current.CancellationToken);
        transfer.RewindTo(2);

        int read = await transfer.ReadAsync(block, TestContext.Current.CancellationToken);

        Assert.Equal(2, read);
        Assert.Equal<byte[]>([3, 4], block);
    }

    [Fact]
    public async Task ARewindToWhereTheContentAlreadyIsLeavesItAlone()
    {
        OutboundTransfer transfer = Sending([1, 2, 3, 4]);

        transfer.RewindTo(0);

        Assert.Equal(4, await transfer.ReadAsync(new byte[4], TestContext.Current.CancellationToken));
    }
}

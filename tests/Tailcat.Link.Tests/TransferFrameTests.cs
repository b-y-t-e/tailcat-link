// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Buffers.Binary;
using Tailcat.Link.Protocol;

namespace Tailcat.Link.Tests;

/// <summary>
/// The block format content moves in, on its own: where it is told to start,
/// and what it does with a peer that sends nonsense.
/// </summary>
public class TransferFrameTests
{
    /// <summary>
    /// The offset comes from the other machine, so a transfer must not be
    /// pointed past the end of its own content by one.
    /// </summary>
    [Theory]
    [InlineData(-1L)]
    [InlineData(4097L)]
    public void AnOffsetOutsideTheContentIsRefused(long offset)
    {
        Assert.Throws<LinkException>(() => TransferFrame.DecodeOffset(TransferFrame.EncodeOffset(offset), 4096));
    }

    [Fact]
    public void AnOffsetSurvivesTheRoundTrip()
    {
        Assert.Equal(21_474_836_480, TransferFrame.DecodeOffset(TransferFrame.EncodeOffset(21_474_836_480), null));
    }

    [Fact]
    public async Task ABlockLargerThanTheProtocolAllowsIsRefused()
    {
        byte[] header = new byte[TransferFrame.BlockHeaderLength];
        BinaryPrimitives.WriteInt32BigEndian(header, TransferFrame.BlockBytes + 1);
        using MemoryStream stream = new(header, writable: false);

        await Assert.ThrowsAsync<LinkException>(async () =>
            await TransferFrame.ReadBlockHeaderAsync(stream, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// The end of the content is a block of no length, so a stream that simply
    /// stops is a truncation and reads as one.
    /// </summary>
    [Fact]
    public async Task TheEndOfTheContentIsABlockOfNoLength()
    {
        byte[] header = new byte[TransferFrame.BlockHeaderLength];
        TransferFrame.WriteBlockHeader(header, 0);
        using MemoryStream stream = new(header, writable: false);

        Assert.Equal(0, await TransferFrame.ReadBlockHeaderAsync(stream, TestContext.Current.CancellationToken));
    }
}

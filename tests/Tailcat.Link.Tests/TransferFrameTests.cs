// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Buffers.Binary;
using System.Text;
using Tailcat.Link.Protocol;

namespace Tailcat.Link.Tests;

/// <summary>
/// The transfer wire format, on its own: what opens a transfer, where it is
/// told to start, and what it does with a peer that sends nonsense.
/// </summary>
public class TransferFrameTests
{
    [Fact]
    public void AnOfferSurvivesTheRoundTrip()
    {
        TransferOffer offer = new()
        {
            Name = "wakacje/film.mkv",
            ContentType = "video/x-matroska",
            Length = 21_474_836_480,
            Metadata = Encoding.UTF8.GetBytes("sha256:abc"),
        };

        TransferOffer read = TransferFrame.DecodeOffer(TransferFrame.EncodeOffer(offer));

        Assert.Equal(offer.Name, read.Name);
        Assert.Equal(offer.ContentType, read.ContentType);
        Assert.Equal(offer.Length, read.Length);
        Assert.Equal(offer.Metadata.ToArray(), read.Metadata.ToArray());
    }

    /// <summary>
    /// A length nobody knows is the ordinary case for content being generated
    /// as it is sent, and has to survive as "unknown" rather than as zero.
    /// </summary>
    [Fact]
    public void AnOfferWithNoLengthStaysWithoutOne()
    {
        TransferOffer read = TransferFrame.DecodeOffer(TransferFrame.EncodeOffer(new TransferOffer()));

        Assert.Null(read.Length);
        Assert.Equal(string.Empty, read.Name);
        Assert.Empty(read.Metadata.ToArray());
    }

    [Fact]
    public void AnOfferThatStopsInTheMiddleIsRefused()
    {
        byte[] encoded = TransferFrame.EncodeOffer(new TransferOffer { Name = "film.mkv" });

        Assert.Throws<LinkException>(() => TransferFrame.DecodeOffer(encoded.AsSpan(0, encoded.Length - 3)));
    }

    /// <summary>
    /// The limits are a defence rather than a preference: without them a peer
    /// makes this machine allocate whatever it names.
    /// </summary>
    [Fact]
    public void AnOfferThatClaimsMoreMetadataThanItCarriesIsRefused()
    {
        byte[] encoded = TransferFrame.EncodeOffer(new TransferOffer());
        BinaryPrimitives.WriteInt32BigEndian(encoded.AsSpan(encoded.Length - 4), int.MaxValue);

        Assert.Throws<LinkException>(() => TransferFrame.DecodeOffer(encoded));
    }

    [Fact]
    public void MetadataOverTheCapIsRefusedBeforeItIsSent()
    {
        TransferOffer offer = new() { Metadata = new byte[TransferFrame.MaxMetadataBytes + 1] };

        LinkException tooBig = Assert.Throws<LinkException>(() => TransferFrame.EncodeOffer(offer));
        Assert.Contains("metadata", tooBig.Message, StringComparison.Ordinal);
    }

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

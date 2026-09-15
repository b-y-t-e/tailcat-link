// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using Tailcat.Link.Protocol;

namespace Tailcat.Link.Tests;

/// <summary>
/// Covers what an exchange header refuses. The bytes it accepts are held to
/// the shared vectors in <see cref="LinkVectorTests"/>; this is what a peer
/// could send that must not be taken for something it is not.
/// </summary>
public class ExchangeFrameTests
{
    private static readonly ExchangeHeader Ordinary = new(
        ExchangeFlags.Answer, Length: 10, AnswerOffset: 0, Name: "a", ContentType: "b", Metadata: new byte[] { 1 });

    /// <summary>
    /// A flag is an instruction. One this build does not know is refused rather
    /// than ignored, since carrying on without it is doing something other than
    /// what the sender asked for.
    /// </summary>
    [Fact]
    public void AFlagThisBuildDoesNotKnowIsRefused()
    {
        byte[] encoded = ExchangeFrame.EncodeHeader(Ordinary);
        encoded[1] |= 0x80;

        Assert.Throws<LinkException>(() => ExchangeFrame.DecodeHeader(encoded));
    }

    /// <summary>A header cut short anywhere is refused, not read as a shorter one.</summary>
    [Fact]
    public void AHeaderCutShortAnywhereIsRefused()
    {
        byte[] encoded = ExchangeFrame.EncodeHeader(Ordinary);
        for (int length = 0; length < encoded.Length; length++)
        {
            Assert.Throws<LinkException>(() => ExchangeFrame.DecodeHeader(encoded.AsSpan(0, length)));
        }
    }

    /// <summary>A field claiming more bytes than follow it is refused.</summary>
    [Fact]
    public void AFieldLongerThanWhatFollowsIsRefused()
    {
        byte[] encoded = ExchangeFrame.EncodeHeader(Ordinary);
        // The name's length prefix, after version, flags and two 64-bit fields.
        encoded[1 + 1 + 8 + 8 + 3] = 0x7F;

        Assert.Throws<LinkException>(() => ExchangeFrame.DecodeHeader(encoded));
    }

    /// <summary>A version this build does not speak is refused.</summary>
    [Fact]
    public void AnotherVersionIsRefused()
    {
        byte[] encoded = ExchangeFrame.EncodeHeader(Ordinary);
        encoded[0] = 2;

        Assert.Throws<LinkException>(() => ExchangeFrame.DecodeHeader(encoded));
        byte[] answer = ExchangeFrame.EncodeAnswer(new AnswerHeader(1, "", default));
        answer[0] = 2;
        Assert.Throws<LinkException>(() => ExchangeFrame.DecodeAnswer(answer));
    }

    /// <summary>A sender claiming a negative share of the answer is refused.</summary>
    [Fact]
    public void ANegativeAnswerOffsetIsRefused()
    {
        byte[] encoded = ExchangeFrame.EncodeHeader(Ordinary with { AnswerOffset = 0 });
        encoded[1 + 1 + 8] = 0xFF;

        Assert.Throws<LinkException>(() => ExchangeFrame.DecodeHeader(encoded));
    }

    /// <summary>Names and metadata have no size limit, past what an array holds.</summary>
    [Fact]
    public void FieldsHaveNoSizeLimit()
    {
        string name = new('n', 70_000);
        byte[] metadata = new byte[200_000];
        Random.Shared.NextBytes(metadata);

        ExchangeHeader read = ExchangeFrame.DecodeHeader(
            ExchangeFrame.EncodeHeader(Ordinary with { Name = name, Metadata = metadata }));

        Assert.Equal(name, read.Name);
        Assert.Equal(metadata, read.Metadata.ToArray());
    }
}

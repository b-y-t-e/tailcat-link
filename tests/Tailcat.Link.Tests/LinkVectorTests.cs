// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Text.Json;
using Tailcat.Link.Protocol;

namespace Tailcat.Link.Tests;

/// <summary>
/// Holds both implementations of the link's own wire formats to the same
/// bytes: the hello that opens a session, and the frames a channel carries.
/// </summary>
/// <remarks>
/// <para>
/// The vectors are one file, read here and by the JavaScript client's own unit
/// tests (<c>clients/browser/test/unit/link-frames.test.mjs</c>). A round trip
/// inside one implementation stays green through a change of prefix width,
/// field order or endianness that would leave every browser refused, so these
/// assert the bytes instead.
/// </para>
/// <para>
/// <c>Relay1VectorTests</c> does the same job for the transport underneath.
/// Nothing else keeps the two sides in step offline: the interop run needs a
/// live relay and a host.
/// </para>
/// </remarks>
public class LinkVectorTests
{
    private sealed record LinkVectors(
        LimitVector Limits,
        byte HelloVersionByte,
        IReadOnlyList<HelloVector> Hellos,
        IReadOnlyList<HelloVector> LegacyHellos,
        IReadOnlyList<ChannelNameVector> ChannelNames,
        IReadOnlyList<ChannelFrameVector> ChannelFrames,
        CapabilityVectors Capabilities,
        FlagVector ExchangeFlags,
        IReadOnlyList<ExchangeHeaderVector> ExchangeHeaders,
        IReadOnlyList<AnswerHeaderVector> AnswerHeaders);

    private sealed record LimitVector(
        int MaxDisplayNameBytes,
        int MaxChannelNameBytes,
        int HelloFrameBytes);

    private sealed record CapabilityVectors(byte LargeFrames, byte Exchanges, IReadOnlyList<PingAnswerVector> PingAnswers);

    private sealed record PingAnswerVector(string Name, string AnswerHex, byte Capabilities);

    private sealed record FlagVector(byte Answer, byte Transfer, byte Pipelined, byte AckOnDelivery);

    private sealed record ExchangeHeaderVector(
        string Name,
        byte Flags,
        long? Length,
        long AnswerOffset,
        string ContentName,
        string ContentType,
        string MetadataHex,
        string EncodedHex);

    private sealed record AnswerHeaderVector(string Name, long? Length, string ContentType, string MetadataHex, string EncodedHex);

    private sealed record HelloVector(string Name, string PairingToken, string? DisplayName, string EncodedHex);

    private sealed record ChannelNameVector(string Name, string ChannelName, string EncodedHex);

    private sealed record ChannelFrameVector(string Name, string PayloadHex, string FrameHex);

    // The file is written by JavaScript, so its names are camelCase.
    private static readonly JsonSerializerOptions AsWritten = new() { PropertyNameCaseInsensitive = true };

    private static LinkVectors Load()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "link-frames.json");
        LinkVectors? vectors = JsonSerializer.Deserialize<LinkVectors>(File.ReadAllText(path), AsWritten);
        Assert.NotNull(vectors);
        return vectors;
    }

    /// <summary>A hello is written as the browser client reads it.</summary>
    [Fact]
    public void HellosMatchTheSharedVectors()
    {
        foreach (HelloVector vector in Load().Hellos)
        {
            byte[] encoded = new LinkHello(vector.PairingToken, vector.DisplayName).Encode();

            Assert.Equal(vector.EncodedHex, Convert.ToHexStringLower(encoded));
        }
    }

    /// <summary>
    /// And read as the browser client writes it, both shapes — so the vector
    /// pins each direction rather than only what this side puts out.
    /// </summary>
    [Fact]
    public void HellosAreReadFromTheSharedVectors()
    {
        LinkVectors vectors = Load();

        foreach (HelloVector vector in vectors.Hellos.Concat(vectors.LegacyHellos))
        {
            LinkHello decoded = LinkHello.Decode(Convert.FromHexString(vector.EncodedHex));

            Assert.Equal(new LinkHello(vector.PairingToken, vector.DisplayName), decoded);
        }
    }

    /// <summary>
    /// The version byte is the whole of what separates the envelope from the
    /// bare token that came before it, so the vectors pin that it is outside
    /// the alphabet a token is written in.
    /// </summary>
    [Fact]
    public void TheVersionByteIsWhatTellsTheTwoShapesApart()
    {
        LinkVectors vectors = Load();

        Assert.Equal(vectors.HelloVersionByte, Convert.FromHexString(vectors.Hellos[0].EncodedHex)[0]);
        Assert.NotEqual(vectors.HelloVersionByte, Convert.FromHexString(vectors.LegacyHellos[0].EncodedHex)[0]);
    }

    /// <summary>A channel name goes on the wire as its UTF-8 bytes, both ways.</summary>
    [Fact]
    public void ChannelNamesMatchTheSharedVectors()
    {
        foreach (ChannelNameVector vector in Load().ChannelNames)
        {
            Assert.Equal(vector.EncodedHex, Convert.ToHexStringLower(ChannelFrame.EncodeName(vector.ChannelName)));
            Assert.Equal(vector.ChannelName, ChannelFrame.DecodeName(Convert.FromHexString(vector.EncodedHex)));
        }
    }

    /// <summary>A frame carries its length in front of it, big-endian.</summary>
    [Fact]
    public async Task ChannelFramesMatchTheSharedVectors()
    {
        foreach (ChannelFrameVector vector in Load().ChannelFrames)
        {
            using MemoryStream written = new();
            await ChannelFrame.WriteAsync(
                written,
                Convert.FromHexString(vector.PayloadHex),
                TestContext.Current.CancellationToken);

            Assert.Equal(vector.FrameHex, Convert.ToHexStringLower(written.ToArray()));
        }
    }

    /// <summary>
    /// And read back the same way, one after another on one stream — which is
    /// what a channel is — ending on the zero-length marker.
    /// </summary>
    [Fact]
    public async Task ChannelFramesAreReadFromTheSharedVectors()
    {
        ChannelFrameVector[] vectors = [.. Load().ChannelFrames];
        ChannelFrameVector[] carrying = [.. vectors.Where(vector => vector.PayloadHex.Length > 0)];
        ChannelFrameVector ending = vectors.Single(vector => vector.PayloadHex.Length == 0);

        using MemoryStream stream = new(
            Convert.FromHexString(string.Concat(carrying.Select(v => v.FrameHex)) + ending.FrameHex));

        foreach (ChannelFrameVector vector in carrying)
        {
            byte[]? frame = await ChannelFrame.ReadAsync(stream, TestContext.Current.CancellationToken);

            Assert.NotNull(frame);
            Assert.Equal(vector.PayloadHex, Convert.ToHexStringLower(frame));
        }

        // The marker, which is how the other end says the frames stopped on
        // purpose rather than with the session.
        Assert.Null(await ChannelFrame.ReadAsync(stream, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// The bounds that are left are part of the format: one side bounding a
    /// name at a size the other will not accept is a peer refused for no
    /// visible reason.
    /// </summary>
    [Fact]
    public void BothSidesAgreeOnTheBoundsThatAreLeft()
    {
        LimitVector limits = Load().Limits;

        Assert.Equal(LinkHello.MaxDisplayNameBytes, limits.MaxDisplayNameBytes);
        Assert.Equal(ChannelFrame.MaxNameBytes, limits.MaxChannelNameBytes);
        Assert.Equal(LinkProtocol.HelloFrameBytes, limits.HelloFrameBytes);
    }

    /// <summary>
    /// A ping answer reads the same on both sides, including the empty one
    /// every older machine sends and the bits of a newer one that are ignored.
    /// </summary>
    [Fact]
    public void PingAnswersMatchTheSharedVectors()
    {
        CapabilityVectors capabilities = Load().Capabilities;
        Assert.Equal((byte)PeerCapabilities.LargeFrames, capabilities.LargeFrames);
        Assert.Equal((byte)PeerCapabilities.Exchanges, capabilities.Exchanges);

        foreach (PingAnswerVector vector in capabilities.PingAnswers)
        {
            Assert.Equal(
                (PeerCapabilities)vector.Capabilities,
                PeerCapabilitiesCodec.Decode(Convert.FromHexString(vector.AnswerHex)));
        }
        Assert.Equal("03", Convert.ToHexStringLower(PeerCapabilitiesCodec.Encode(PeerCapabilitiesCodec.ThisBuild)));
    }

    /// <summary>An exchange header is written byte for byte as specified, and read back.</summary>
    [Fact]
    public void ExchangeHeadersMatchTheSharedVectors()
    {
        LinkVectors vectors = Load();
        Assert.Equal((byte)ExchangeFlags.Answer, vectors.ExchangeFlags.Answer);
        Assert.Equal((byte)ExchangeFlags.Transfer, vectors.ExchangeFlags.Transfer);
        Assert.Equal((byte)ExchangeFlags.Pipelined, vectors.ExchangeFlags.Pipelined);
        Assert.Equal((byte)ExchangeFlags.AckOnDelivery, vectors.ExchangeFlags.AckOnDelivery);

        foreach (ExchangeHeaderVector vector in vectors.ExchangeHeaders)
        {
            ExchangeHeader header = new(
                (ExchangeFlags)vector.Flags,
                vector.Length,
                vector.AnswerOffset,
                vector.ContentName,
                vector.ContentType,
                Convert.FromHexString(vector.MetadataHex));

            Assert.Equal(vector.EncodedHex, Convert.ToHexStringLower(ExchangeFrame.EncodeHeader(header)));

            ExchangeHeader read = ExchangeFrame.DecodeHeader(Convert.FromHexString(vector.EncodedHex));
            Assert.Equal(header.Flags, read.Flags);
            Assert.Equal(header.Length, read.Length);
            Assert.Equal(header.AnswerOffset, read.AnswerOffset);
            Assert.Equal(header.Name, read.Name);
            Assert.Equal(header.ContentType, read.ContentType);
            Assert.Equal(vector.MetadataHex, Convert.ToHexStringLower(read.Metadata.Span));
        }
    }

    /// <summary>An answer header is written byte for byte as specified, and read back.</summary>
    [Fact]
    public void AnswerHeadersMatchTheSharedVectors()
    {
        foreach (AnswerHeaderVector vector in Load().AnswerHeaders)
        {
            AnswerHeader header = new(vector.Length, vector.ContentType, Convert.FromHexString(vector.MetadataHex));
            Assert.Equal(vector.EncodedHex, Convert.ToHexStringLower(ExchangeFrame.EncodeAnswer(header)));

            AnswerHeader read = ExchangeFrame.DecodeAnswer(Convert.FromHexString(vector.EncodedHex));
            Assert.Equal(header.Length, read.Length);
            Assert.Equal(header.ContentType, read.ContentType);
            Assert.Equal(vector.MetadataHex, Convert.ToHexStringLower(read.Metadata.Span));
        }
    }
}

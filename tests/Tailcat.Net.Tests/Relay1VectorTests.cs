// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Globalization;
using System.Net;
using System.Text.Json;
using Tailcat.Keys;
using Tailcat.Net.Relay1;

namespace Tailcat.Net.Tests;

/// <summary>
/// Holds both implementations of <c>relay1</c> to the same bytes.
/// </summary>
/// <remarks>
/// The vectors are one file, read here and by the JavaScript client's own
/// unit tests (<c>clients/browser/test/unit/relay1-records.test.mjs</c>).
/// Nothing else keeps the two in step offline: the interop run needs a live
/// relay and a host, so without this a disagreement about a varint or a
/// nonce would wait for somebody to notice it by hand.
/// </remarks>
public class Relay1VectorTests
{
    private sealed record RecordVectors(
        string KeyHex,
        IReadOnlyList<RecordVector> Cases,
        KeyScheduleVector KeySchedule,
        IReadOnlyList<HelloVector> HelloCases);

    private sealed record KeyScheduleVector(
        string SharedHex,
        string SessionId,
        string DialerPublicHex,
        string HostPublicHex,
        string DialerToHostHex,
        string HostToDialerHex);

    private sealed record HelloVector(
        string Name,
        string HelloHex,
        string SessionId,
        string FingerprintHex,
        int HomeRegionId,
        IReadOnlyList<EndpointVector> Endpoints,
        IReadOnlyList<byte> Transports,
        string? EphemeralHex,
        bool EncodedByDotnet);

    private sealed record EndpointVector(string AddressHex, int Port);

    private sealed record RecordVector(
        string Name,
        ulong StreamId,
        byte Flags,
        string PayloadHex,
        string Counter,
        string FrameHex,
        string RecordHex);

    // The file is written by JavaScript, so its names are camelCase.
    private static readonly JsonSerializerOptions AsWritten = new() { PropertyNameCaseInsensitive = true };

    private static RecordVectors Load()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "relay1-records.json");
        RecordVectors? vectors = JsonSerializer.Deserialize<RecordVectors>(File.ReadAllText(path), AsWritten);
        Assert.NotNull(vectors);
        return vectors;
    }

    /// <summary>A frame's stream id, flags and payload are laid out as the vectors say.</summary>
    [Fact]
    public void FramesMatchTheSharedVectors()
    {
        foreach (RecordVector vector in Load().Cases)
        {
            byte[] frame = Relay1Frame.Encode(
                vector.StreamId,
                (Relay1FrameFlags)vector.Flags,
                Convert.FromHexString(vector.PayloadHex));

            Assert.Equal(vector.FrameHex, Convert.ToHexStringLower(frame));
        }
    }

    /// <summary>A sealed record — header, counter, nonce and tag — is byte for byte the same.</summary>
    [Fact]
    public void RecordsMatchTheSharedVectors()
    {
        RecordVectors vectors = Load();
        byte[] key = Convert.FromHexString(vectors.KeyHex);

        foreach (RecordVector vector in vectors.Cases)
        {
            byte[] record = Relay1Record.Seal(
                Convert.FromHexString(vector.FrameHex),
                key,
                ulong.Parse(vector.Counter, CultureInfo.InvariantCulture));

            Assert.Equal(vector.RecordHex, Convert.ToHexStringLower(record));
        }
    }

    /// <summary>
    /// The key schedule — salt order, HKDF labels, one Expand block — is the
    /// same on both sides.
    /// </summary>
    /// <remarks>
    /// From a fixed shared secret rather than a real exchange: the ephemeral
    /// keys are chosen by whoever is talking, so nothing else here could be
    /// compared byte for byte. A label edited on one side lands here.
    /// </remarks>
    [Fact]
    public void TheKeyScheduleMatchesTheSharedVector()
    {
        KeyScheduleVector vector = Load().KeySchedule;

        Relay1Keys keys = Relay1Ephemeral.Schedule(
            Convert.FromHexString(vector.SharedHex),
            ulong.Parse(vector.SessionId, CultureInfo.InvariantCulture),
            NodePublic.FromRaw32(Convert.FromHexString(vector.DialerPublicHex)),
            NodePublic.FromRaw32(Convert.FromHexString(vector.HostPublicHex)));

        Assert.Equal(vector.DialerToHostHex, Convert.ToHexStringLower(keys.DialerToHost));
        Assert.Equal(vector.HostToDialerHex, Convert.ToHexStringLower(keys.HostToDialer));
    }

    /// <summary>A hello is laid out as the vectors say, field for field.</summary>
    /// <remarks>
    /// The hello is where the transport is agreed, so a browser that reads it
    /// one byte out never gets as far as a record to disagree about.
    /// </remarks>
    [Fact]
    public void HellosMatchTheSharedVectors()
    {
        foreach (HelloVector vector in Load().HelloCases.Where(v => v.EncodedByDotnet))
        {
            PeerHello hello = new(
                ulong.Parse(vector.SessionId, CultureInfo.InvariantCulture),
                Convert.FromHexString(vector.FingerprintHex),
                [.. vector.Endpoints.Select(ToEndPoint)],
                vector.HomeRegionId,
                [.. vector.Transports.Select(t => (PeerTransport)t)],
                vector.EphemeralHex is null ? null : Convert.FromHexString(vector.EphemeralHex));

            Assert.Equal(vector.HelloHex, Convert.ToHexStringLower(hello.Encode()));
        }
    }

    /// <summary>A hello the JavaScript client wrote reads back field for field.</summary>
    [Fact]
    public void HellosFromTheOtherImplementationDecode()
    {
        foreach (HelloVector vector in Load().HelloCases)
        {
            Assert.True(PeerHello.TryDecode(Convert.FromHexString(vector.HelloHex), out PeerHello? hello), vector.Name);
            Assert.Equal(ulong.Parse(vector.SessionId, CultureInfo.InvariantCulture), hello.SessionId);
            Assert.Equal(vector.FingerprintHex, Convert.ToHexStringLower(hello.CertificateFingerprint));
            Assert.Equal(vector.HomeRegionId, hello.HomeRegionId);
            Assert.Equal(vector.Endpoints.Select(ToEndPoint), hello.Endpoints);
            Assert.Equal(vector.Transports.Select(t => (PeerTransport)t), hello.Transports);
            Assert.Equal(
                vector.EphemeralHex,
                hello.Ephemeral is null ? null : Convert.ToHexStringLower(hello.Ephemeral));
        }
    }

    private static IPEndPoint ToEndPoint(EndpointVector endpoint) =>
        new(new IPAddress(Convert.FromHexString(endpoint.AddressHex)), endpoint.Port);

    /// <summary>What the JavaScript client sealed, this side opens.</summary>
    [Fact]
    public void RecordsFromTheOtherImplementationOpen()
    {
        RecordVectors vectors = Load();
        byte[] key = Convert.FromHexString(vectors.KeyHex);

        foreach (RecordVector vector in vectors.Cases)
        {
            Assert.True(
                Relay1Record.TryOpen(Convert.FromHexString(vector.RecordHex), key, out ulong counter, out byte[] frame),
                vector.Name);
            Assert.Equal(ulong.Parse(vector.Counter, CultureInfo.InvariantCulture), counter);

            Assert.True(Relay1Frame.TryDecode(frame, out ulong streamId, out Relay1FrameFlags flags, out ReadOnlyMemory<byte> payload));
            Assert.Equal(vector.StreamId, streamId);
            Assert.Equal(vector.Flags, (byte)flags);
            Assert.Equal(vector.PayloadHex, Convert.ToHexStringLower(payload.Span));
        }
    }
}

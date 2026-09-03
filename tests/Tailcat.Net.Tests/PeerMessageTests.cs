// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Net;
using System.Security.Cryptography;
using Tailcat.Keys;

namespace Tailcat.Net.Tests;

/// <summary>
/// Covers the framing and sealing of peer messages. These are the guarantees
/// that let two nodes trust each other while a relay they don't control sits
/// in the middle.
/// </summary>
public class PeerMessageTests
{
    private static (NodePrivate Priv, NodePublic Pub) Node()
    {
        NodePrivate priv = NodePrivate.NewKey();
        return (priv, priv.Public());
    }

    [Fact]
    public void SealedControlMessageRoundTrips()
    {
        (NodePrivate aPriv, NodePublic aPub) = Node();
        (NodePrivate bPriv, NodePublic bPub) = Node();
        byte[] payload = "the secret"u8.ToArray();

        byte[] msg = PeerMessage.Seal(PeerMessageType.Hello, payload, aPriv, bPub);

        Assert.True(PeerMessage.IsPeerMessage(msg));
        Assert.True(PeerMessage.TryOpen(msg, bPriv, aPub, out PeerMessageType type, out byte[]? got));
        Assert.Equal(PeerMessageType.Hello, type);
        Assert.Equal(payload, got);
    }

    /// <summary>The payload must not be readable by anyone else.</summary>
    [Fact]
    public void SealedMessageIsUnreadableByAThirdParty()
    {
        (NodePrivate aPriv, NodePublic aPub) = Node();
        (_, NodePublic bPub) = Node();
        (NodePrivate eavesdropperPriv, _) = Node();

        byte[] msg = PeerMessage.Seal(PeerMessageType.Hello, "the secret"u8, aPriv, bPub);

        Assert.False(PeerMessage.TryOpen(msg, eavesdropperPriv, aPub, out _, out _));
        Assert.DoesNotContain("the secret", System.Text.Encoding.ASCII.GetString(msg), StringComparison.Ordinal);
    }

    /// <summary>
    /// A relay that tried to pass off its own message as the peer's must fail:
    /// this is what stops it handing us an attacker's certificate fingerprint.
    /// </summary>
    [Fact]
    public void MessageFromTheWrongSenderIsRejected()
    {
        (_, NodePublic aPub) = Node();
        (NodePrivate bPriv, NodePublic bPub) = Node();
        (NodePrivate impostorPriv, _) = Node();

        byte[] forged = PeerMessage.Seal(PeerMessageType.Hello, "trust me"u8, impostorPriv, bPub);

        Assert.False(PeerMessage.TryOpen(forged, bPriv, aPub, out _, out _));
    }

    [Fact]
    public void TamperedMessageIsRejected()
    {
        (NodePrivate aPriv, NodePublic aPub) = Node();
        (NodePrivate bPriv, NodePublic bPub) = Node();

        byte[] msg = PeerMessage.Seal(PeerMessageType.Ping, "payload"u8, aPriv, bPub);
        msg[^1] ^= 0xff;

        Assert.False(PeerMessage.TryOpen(msg, bPriv, aPub, out _, out _));
    }

    /// <summary>Two seals of the same payload must differ, or traffic becomes trivially trackable.</summary>
    [Fact]
    public void SealingTwiceProducesDifferentBytes()
    {
        (NodePrivate aPriv, _) = Node();
        (_, NodePublic bPub) = Node();

        byte[] first = PeerMessage.Seal(PeerMessageType.Ping, "same"u8, aPriv, bPub);
        byte[] second = PeerMessage.Seal(PeerMessageType.Ping, "same"u8, aPriv, bPub);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void DataMessagesAreNotSealed()
    {
        byte[] datagram = RandomNumberGenerator.GetBytes(1200);

        byte[] msg = PeerMessage.EncodeData(datagram);

        Assert.True(PeerMessage.IsPeerMessage(msg));
        Assert.Equal(PeerMessageType.Data, PeerMessage.TypeOf(msg));
        Assert.Equal(datagram, PeerMessage.DecodeData(msg).ToArray());

        // Data carries QUIC, which is already encrypted; TryOpen must refuse
        // to treat it as a control message.
        Assert.False(PeerMessage.TryOpen(msg, NodePrivate.NewKey(), NodePrivate.NewKey().Public(), out _, out _));
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 1, 2, 3 })]
    [InlineData(new byte[] { (byte)'T', (byte)'C', (byte)'N', (byte)'0', 1 })]
    public void ForeignPacketsAreNotPeerMessages(byte[] packet) =>
        Assert.False(PeerMessage.IsPeerMessage(packet));

    [Fact]
    public void HelloRoundTrips()
    {
        PeerHello hello = new(
            SessionId: 0xDEADBEEFCAFEF00D,
            CertificateFingerprint: RandomNumberGenerator.GetBytes(PeerHello.FingerprintLen),
            Endpoints:
            [
                new IPEndPoint(IPAddress.Parse("192.168.1.5"), 41641),
                new IPEndPoint(IPAddress.Parse("203.0.113.9"), 51820),
                new IPEndPoint(IPAddress.Parse("2001:db8::1"), 443),
            ]);

        Assert.True(PeerHello.TryDecode(hello.Encode(), out PeerHello? got));
        Assert.Equal(hello.SessionId, got.SessionId);
        Assert.Equal(hello.CertificateFingerprint, got.CertificateFingerprint);
        Assert.Equal(hello.Endpoints, got.Endpoints);
    }

    [Fact]
    public void HelloWithNoEndpointsRoundTrips()
    {
        PeerHello hello = new(1, new byte[PeerHello.FingerprintLen], []);

        Assert.True(PeerHello.TryDecode(hello.Encode(), out PeerHello? got));
        Assert.Empty(got.Endpoints);
    }

    /// <summary>A peer can't make us probe an unbounded list of addresses.</summary>
    [Fact]
    public void HelloEndpointsAreCapped()
    {
        List<IPEndPoint> many = [.. Enumerable.Range(1, 200).Select(i => new IPEndPoint(IPAddress.Parse($"10.0.0.{i % 254 + 1}"), i))];
        PeerHello hello = new(1, new byte[PeerHello.FingerprintLen], many);

        Assert.True(PeerHello.TryDecode(hello.Encode(), out PeerHello? got));
        Assert.True(got.Endpoints.Count <= 32, $"encoded {got.Endpoints.Count} endpoints; want at most 32");
    }

    [Fact]
    public void TruncatedHelloIsRejected()
    {
        byte[] encoded = new PeerHello(1, new byte[PeerHello.FingerprintLen],
            [new IPEndPoint(IPAddress.Loopback, 1234)]).Encode();

        Assert.False(PeerHello.TryDecode(encoded.AsSpan(0, encoded.Length - 2), out _));
        Assert.False(PeerHello.TryDecode([], out _));
    }

    [Fact]
    public void PingRoundTrips()
    {
        PeerPing ping = new(Id: 0x0102030405060708, SessionId: 0x1122334455667788);

        Assert.True(PeerPing.TryDecode(ping.Encode(), out PeerPing got));
        Assert.Equal(ping, got);
    }

    [Fact]
    public void TruncatedPingIsRejected() =>
        Assert.False(PeerPing.TryDecode(new byte[PeerPing.EncodedLen - 1], out _));
}

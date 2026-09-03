// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Buffers.Binary;
using System.Net;

namespace Tailcat.Net.Tests;

/// <summary>Covers the STUN Binding exchange used to learn a node's public address.</summary>
public class StunTests
{
    // BuildResponse assembles a Binding success response carrying an
    // XOR-MAPPED-ADDRESS, the way a STUN server answers.
    private static byte[] BuildResponse(ReadOnlySpan<byte> transactionId, IPEndPoint mapped, ushort attrType = Stun.XorMappedAddress)
    {
        byte[] addr = mapped.Address.GetAddressBytes();
        byte[] value = new byte[4 + addr.Length];
        value[0] = 0;
        value[1] = (byte)(addr.Length == 4 ? 0x01 : 0x02);
        BinaryPrimitives.WriteUInt16BigEndian(value.AsSpan(2), (ushort)(mapped.Port ^ (Stun.MagicCookie >> 16)));

        Span<byte> cookieAndTid = stackalloc byte[4 + Stun.TransactionIdLen];
        BinaryPrimitives.WriteUInt32BigEndian(cookieAndTid, Stun.MagicCookie);
        transactionId.CopyTo(cookieAndTid[4..]);
        for (int i = 0; i < addr.Length; i++)
        {
            value[4 + i] = (byte)(addr[i] ^ cookieAndTid[i]);
        }

        byte[] msg = new byte[Stun.HeaderLen + 4 + value.Length];
        BinaryPrimitives.WriteUInt16BigEndian(msg, Stun.BindingSuccessResponse);
        BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(2), (ushort)(4 + value.Length));
        BinaryPrimitives.WriteUInt32BigEndian(msg.AsSpan(4), Stun.MagicCookie);
        transactionId.CopyTo(msg.AsSpan(8));
        BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(Stun.HeaderLen), attrType);
        BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(Stun.HeaderLen + 2), (ushort)value.Length);
        value.CopyTo(msg.AsSpan(Stun.HeaderLen + 4));
        return msg;
    }

    [Fact]
    public void BindingRequestHasTheRightShape()
    {
        byte[] req = Stun.BuildBindingRequest(out byte[] transactionId);

        Assert.Equal(Stun.HeaderLen, req.Length);
        Assert.Equal(Stun.BindingRequest, BinaryPrimitives.ReadUInt16BigEndian(req));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(req.AsSpan(2)));
        Assert.Equal(Stun.MagicCookie, BinaryPrimitives.ReadUInt32BigEndian(req.AsSpan(4)));
        Assert.Equal(Stun.TransactionIdLen, transactionId.Length);
        Assert.Equal(transactionId, req[8..]);
        Assert.True(Stun.IsStunPacket(req));
    }

    /// <summary>Two requests must not share a transaction ID.</summary>
    [Fact]
    public void TransactionIdsAreUnique()
    {
        Stun.BuildBindingRequest(out byte[] first);
        Stun.BuildBindingRequest(out byte[] second);

        Assert.NotEqual(first, second);
    }

    [Theory]
    [InlineData("203.0.113.7", 51234)]
    [InlineData("198.51.100.1", 1)]
    [InlineData("192.0.2.55", 65535)]
    public void Ipv4MappedAddressIsUnxored(string ip, int port)
    {
        Stun.BuildBindingRequest(out byte[] tid);
        IPEndPoint expected = new(IPAddress.Parse(ip), port);

        Assert.True(Stun.TryParseBindingResponse(BuildResponse(tid, expected), tid, out IPEndPoint? got));
        Assert.Equal(expected, got);
    }

    [Fact]
    public void Ipv6MappedAddressIsUnxored()
    {
        Stun.BuildBindingRequest(out byte[] tid);
        IPEndPoint expected = new(IPAddress.Parse("2001:db8::dead:beef"), 4433);

        Assert.True(Stun.TryParseBindingResponse(BuildResponse(tid, expected), tid, out IPEndPoint? got));
        Assert.Equal(expected, got);
    }

    /// <summary>
    /// An answer to somebody else's request must be ignored, or an attacker
    /// could tell us we live at an address of their choosing.
    /// </summary>
    [Fact]
    public void ResponseForAnotherTransactionIsRejected()
    {
        Stun.BuildBindingRequest(out byte[] ours);
        Stun.BuildBindingRequest(out byte[] theirs);

        byte[] response = BuildResponse(theirs, new IPEndPoint(IPAddress.Parse("203.0.113.7"), 1234));

        Assert.False(Stun.TryParseBindingResponse(response, ours, out _));
    }

    [Fact]
    public void ResponseWithoutMappedAddressIsRejected()
    {
        Stun.BuildBindingRequest(out byte[] tid);

        // A well-formed response, but carrying some other attribute.
        byte[] response = BuildResponse(tid, new IPEndPoint(IPAddress.Loopback, 1), attrType: 0x0022);

        Assert.False(Stun.TryParseBindingResponse(response, tid, out _));
    }

    [Fact]
    public void NonStunPacketsAreRejected()
    {
        Assert.False(Stun.IsStunPacket([]));
        Assert.False(Stun.IsStunPacket(new byte[Stun.HeaderLen])); // no magic cookie
        Assert.False(Stun.IsStunPacket("TCN1"u8));

        // Our own peer framing must never be mistaken for STUN, since both
        // arrive on the same socket.
        byte[] peerMessage = PeerMessage.EncodeData("some quic packet"u8);
        Assert.False(Stun.IsStunPacket(peerMessage));
    }

    [Fact]
    public void TransactionIdIsReadableFromAReply()
    {
        Stun.BuildBindingRequest(out byte[] tid);
        byte[] response = BuildResponse(tid, new IPEndPoint(IPAddress.Parse("203.0.113.7"), 999));

        Assert.True(Stun.TryGetTransactionId(response, out ReadOnlySpan<byte> got));
        Assert.True(got.SequenceEqual(tid));
    }

    [Fact]
    public void TruncatedResponseIsRejected()
    {
        Stun.BuildBindingRequest(out byte[] tid);
        byte[] response = BuildResponse(tid, new IPEndPoint(IPAddress.Parse("203.0.113.7"), 999));

        Assert.False(Stun.TryParseBindingResponse(response.AsSpan(0, response.Length - 4), tid, out _));
    }
}

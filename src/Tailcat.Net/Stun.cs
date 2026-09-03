// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace Tailcat.Net;

/// <summary>
/// The parts of STUN (RFC 5389) needed to learn how a NAT maps a local UDP
/// socket to a public address: a Binding request, and the XOR-MAPPED-ADDRESS
/// in the reply.
/// </summary>
/// <remarks>
/// The reply says "this is the address I saw your packet come from", which is
/// the address a peer must send to for hole punching to work. It must be asked
/// over the very socket that will carry the traffic: a NAT maps per source
/// port, so another socket would learn an address that routes nowhere useful.
/// </remarks>
public static class Stun
{
    /// <summary>The STUN message type for a Binding request.</summary>
    public const ushort BindingRequest = 0x0001;

    /// <summary>The STUN message type for a successful Binding response.</summary>
    public const ushort BindingSuccessResponse = 0x0101;

    /// <summary>The attribute type for XOR-MAPPED-ADDRESS.</summary>
    public const ushort XorMappedAddress = 0x0020;

    /// <summary>The magic cookie every STUN message carries.</summary>
    public const uint MagicCookie = 0x2112A442;

    /// <summary>The length of a STUN message header.</summary>
    public const int HeaderLen = 20;

    /// <summary>The length of a STUN transaction ID.</summary>
    public const int TransactionIdLen = 12;

    /// <summary>The default port a STUN server listens on.</summary>
    public const int DefaultPort = 3478;

    /// <summary>
    /// Builds a Binding request with a fresh random transaction ID, which the
    /// server echoes so replies can be matched to requests.
    /// </summary>
    public static byte[] BuildBindingRequest(out byte[] transactionId)
    {
        transactionId = RandomNumberGenerator.GetBytes(TransactionIdLen);
        byte[] msg = new byte[HeaderLen];
        BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(0), BindingRequest);
        BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(2), 0); // no attributes
        BinaryPrimitives.WriteUInt32BigEndian(msg.AsSpan(4), MagicCookie);
        transactionId.CopyTo(msg, 8);
        return msg;
    }

    /// <summary>
    /// Reports whether <paramref name="packet"/> looks like a STUN message,
    /// so a socket carrying both STUN and application traffic can tell them
    /// apart.
    /// </summary>
    public static bool IsStunPacket(ReadOnlySpan<byte> packet) =>
        packet.Length >= HeaderLen &&
        (packet[0] & 0xC0) == 0 && // the two most significant bits are always zero
        BinaryPrimitives.ReadUInt32BigEndian(packet[4..]) == MagicCookie;

    /// <summary>
    /// Parses the public address out of a Binding response, checking that it
    /// answers <paramref name="transactionId"/>.
    /// </summary>
    /// <returns>False if the message isn't a matching, well-formed response.</returns>
    public static bool TryParseBindingResponse(
        ReadOnlySpan<byte> message,
        ReadOnlySpan<byte> transactionId,
        [NotNullWhen(true)] out IPEndPoint? mappedAddress)
    {
        mappedAddress = null;
        if (!IsStunPacket(message))
        {
            return false;
        }
        if (BinaryPrimitives.ReadUInt16BigEndian(message) != BindingSuccessResponse)
        {
            return false;
        }
        if (!message.Slice(8, TransactionIdLen).SequenceEqual(transactionId))
        {
            // An answer to someone else's request, or a stale one of ours.
            return false;
        }

        int bodyLen = BinaryPrimitives.ReadUInt16BigEndian(message[2..]);
        if (HeaderLen + bodyLen > message.Length)
        {
            return false;
        }

        ReadOnlySpan<byte> body = message.Slice(HeaderLen, bodyLen);
        while (body.Length >= 4)
        {
            ushort attrType = BinaryPrimitives.ReadUInt16BigEndian(body);
            int attrLen = BinaryPrimitives.ReadUInt16BigEndian(body[2..]);
            if (4 + attrLen > body.Length)
            {
                return false;
            }
            ReadOnlySpan<byte> value = body.Slice(4, attrLen);

            if (attrType == XorMappedAddress &&
                TryParseXorMappedAddress(value, message.Slice(4, 4 + TransactionIdLen), out mappedAddress))
            {
                return true;
            }

            // Attributes are padded to a 4-byte boundary.
            int advance = 4 + attrLen + ((4 - (attrLen % 4)) % 4);
            if (advance > body.Length)
            {
                return false;
            }
            body = body[advance..];
        }
        return false;
    }

    /// <summary>
    /// Returns the transaction ID of a STUN message, so a reply can be matched
    /// to the request that caused it by a shared receive loop.
    /// </summary>
    public static bool TryGetTransactionId(ReadOnlySpan<byte> message, out ReadOnlySpan<byte> transactionId)
    {
        if (!IsStunPacket(message))
        {
            transactionId = default;
            return false;
        }
        transactionId = message.Slice(8, TransactionIdLen);
        return true;
    }

    /// <summary>
    /// Asks <paramref name="stunServer"/> what public address it sees
    /// <paramref name="socket"/> as, retrying a few times because UDP.
    /// </summary>
    /// <param name="socket">The socket the traffic will use. Its NAT mapping is what we're measuring.</param>
    /// <param name="stunServer">The STUN server to ask.</param>
    /// <param name="timeout">How long to wait for each attempt.</param>
    /// <param name="attempts">How many times to ask before giving up.</param>
    /// <param name="cancellationToken">Cancels the probe.</param>
    /// <returns>The public address, or null if the server never answered.</returns>
    public static async Task<IPEndPoint?> DiscoverAsync(
        Socket socket,
        IPEndPoint stunServer,
        TimeSpan? timeout = null,
        int attempts = 3,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(socket);
        ArgumentNullException.ThrowIfNull(stunServer);

        TimeSpan wait = timeout ?? TimeSpan.FromSeconds(2);
        byte[] buffer = new byte[1500];

        for (int attempt = 0; attempt < attempts; attempt++)
        {
            byte[] request = BuildBindingRequest(out byte[] transactionId);
            await socket.SendToAsync(request, SocketFlags.None, stunServer, cancellationToken).ConfigureAwait(false);

            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(wait);
            try
            {
                while (true)
                {
                    SocketReceiveFromResult res = await socket
                        .ReceiveFromAsync(buffer, SocketFlags.None, stunServer, cts.Token)
                        .ConfigureAwait(false);
                    if (TryParseBindingResponse(
                            buffer.AsSpan(0, res.ReceivedBytes), transactionId, out IPEndPoint? mapped))
                    {
                        return mapped;
                    }
                    // Something else arrived on this socket; keep waiting for our answer.
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // This attempt timed out; try again.
            }
        }
        return null;
    }

    // The address is XORed with the magic cookie (and, for IPv6, the
    // transaction ID) so that NATs rewriting payloads can't corrupt it.
    private static bool TryParseXorMappedAddress(
        ReadOnlySpan<byte> value,
        ReadOnlySpan<byte> cookieAndTransactionId,
        [NotNullWhen(true)] out IPEndPoint? endPoint)
    {
        endPoint = null;
        if (value.Length < 4)
        {
            return false;
        }

        byte family = value[1];
        ushort xorPort = BinaryPrimitives.ReadUInt16BigEndian(value[2..]);
        ushort port = (ushort)(xorPort ^ (MagicCookie >> 16));

        switch (family)
        {
            case 0x01 when value.Length >= 8:
            {
                Span<byte> addr = stackalloc byte[4];
                for (int i = 0; i < 4; i++)
                {
                    addr[i] = (byte)(value[4 + i] ^ cookieAndTransactionId[i]);
                }
                endPoint = new IPEndPoint(new IPAddress(addr), port);
                return true;
            }
            case 0x02 when value.Length >= 20:
            {
                Span<byte> addr = stackalloc byte[16];
                for (int i = 0; i < 16; i++)
                {
                    addr[i] = (byte)(value[4 + i] ^ cookieAndTransactionId[i]);
                }
                endPoint = new IPEndPoint(new IPAddress(addr), port);
                return true;
            }
            default:
                return false;
        }
    }
}

// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sodium;
using Tailcat.Keys;
using Tailcat.Tailcfg;

namespace Tailcat.Derp;

/// <summary>A packet received from another node through a DERP relay.</summary>
/// <param name="Source">The public key of the node that sent it.</param>
/// <param name="Payload">The packet bytes.</param>
public readonly record struct DerpReceivedPacket(NodePublic Source, ReadOnlyMemory<byte> Payload);

/// <summary>
/// A client of a DERP relay: the "designated encrypted relay for packets"
/// that Tailscale runs on port 443, which routes packets between nodes by
/// their Curve25519 public keys.
/// </summary>
/// <remarks>
/// <para>
/// A DERP relay is what makes two hosts on mutually invisible networks
/// reachable: both connect outbound to the relay, so no inbound port,
/// firewall rule, or NAT mapping is needed. The relay sees only opaque
/// packets, so whatever rides on top must provide its own end-to-end
/// encryption.
/// </para>
/// <para>
/// The relay does not guarantee delivery: packets may be dropped, and a
/// congested relay drops them deliberately. Anything needing reliability
/// must layer it on top.
/// </para>
/// </remarks>
public sealed class DerpClient : IAsyncDisposable
{
    private readonly DerpFrameStream _frames;

    private DerpClient(DerpFrameStream frames, NodePrivate privateKey, NodePublic publicKey, NodePublic serverKey, DerpServerInfo serverInfo)
    {
        _frames = frames;
        PublicKey = publicKey;
        ServerPublicKey = serverKey;
        ServerInfo = serverInfo;
    }

    /// <summary>This client's public key: the address other nodes send to.</summary>
    public NodePublic PublicKey { get; }

    /// <summary>The relay's own public key, learned during the handshake.</summary>
    public NodePublic ServerPublicKey { get; }

    /// <summary>What the relay told us about itself at login.</summary>
    public DerpServerInfo ServerInfo { get; }

    /// <summary>
    /// Connects to <paramref name="node"/> and completes the DERP login
    /// handshake, so the connection is ready to send and receive packets.
    /// </summary>
    /// <param name="node">The relay node to dial, from a DERP map.</param>
    /// <param name="privateKey">This node's identity; its public half is our address.</param>
    /// <param name="cancellationToken">Cancels the connection attempt.</param>
    /// <exception cref="DerpProtocolException">If the relay doesn't speak DERP as expected.</exception>
    public static async Task<DerpClient> ConnectAsync(
        DerpNode node,
        NodePrivate privateKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(node);

        Socket socket = await DialAsync(node, cancellationToken).ConfigureAwait(false);
        Stream stream = new NetworkStream(socket, ownsSocket: true);
        try
        {
            string tlsHost = node.CertName.Length != 0 ? node.CertName : node.HostName;
            DerpTlsConnector.TlsSession session = await DerpTlsConnector
                .ConnectAsync(stream, tlsHost, node.InsecureForTests, cancellationToken)
                .ConfigureAwait(false);

            await UpgradeToDerpAsync(session.Stream, node, cancellationToken).ConfigureAwait(false);

            DerpFrameStream frames = new(session.Stream);
            return await HandshakeAsync(frames, privateKey, session.ServerKeyFromMetaCert, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Completes the DERP login handshake over an already-established
    /// stream, for callers that bring their own transport (and for tests
    /// against an in-memory relay).
    /// </summary>
    /// <param name="stream">The stream to a relay, past TLS and the HTTP upgrade.</param>
    /// <param name="privateKey">This node's identity.</param>
    /// <param name="expectedServerKey">
    /// The relay key learned out of band (from its TLS meta certificate), if
    /// any. A mismatch with the key the relay greets us with fails the
    /// handshake.
    /// </param>
    /// <param name="cancellationToken">Cancels the handshake.</param>
    public static Task<DerpClient> ConnectOverStreamAsync(
        Stream stream,
        NodePrivate privateKey,
        NodePublic? expectedServerKey = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return HandshakeAsync(new DerpFrameStream(stream), privateKey, expectedServerKey, cancellationToken);
    }

    /// <summary>
    /// Sends <paramref name="packet"/> to the node with public key
    /// <paramref name="destination"/>. Delivery is best effort.
    /// </summary>
    /// <exception cref="ArgumentException">If the packet exceeds the protocol's size limit.</exception>
    public async Task SendAsync(NodePublic destination, ReadOnlyMemory<byte> packet, CancellationToken cancellationToken = default)
    {
        if (packet.Length > DerpProtocol.MaxPacketSize)
        {
            throw new ArgumentException(
                $"packet of {packet.Length} bytes exceeds the DERP limit of {DerpProtocol.MaxPacketSize}",
                nameof(packet));
        }

        byte[] frame = new byte[DerpProtocol.KeyLen + packet.Length];
        destination.Raw32().CopyTo(frame, 0);
        packet.Span.CopyTo(frame.AsSpan(DerpProtocol.KeyLen));
        await _frames.WriteFrameAsync(DerpFrameType.SendPacket, frame, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads until another node's packet arrives, answering the relay's
    /// pings and ignoring its bookkeeping frames along the way.
    /// </summary>
    /// <exception cref="DerpProtocolException">If the relay reports the connection unhealthy in a fatal way.</exception>
    /// <exception cref="EndOfStreamException">If the relay closes the connection.</exception>
    public async Task<DerpReceivedPacket> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            DerpFrame frame = await _frames.ReadFrameAsync(cancellationToken).ConfigureAwait(false);
            switch (frame.Type)
            {
                case DerpFrameType.RecvPacket:
                    if (frame.Payload.Length < DerpProtocol.KeyLen)
                    {
                        throw new DerpProtocolException($"RecvPacket frame of {frame.Payload.Length} bytes is too short to carry a source key");
                    }
                    return new DerpReceivedPacket(
                        NodePublic.FromRaw32(frame.Payload.Span[..DerpProtocol.KeyLen]),
                        frame.Payload[DerpProtocol.KeyLen..]);

                case DerpFrameType.Ping:
                    // The relay measures liveness with these; echo the payload back.
                    await _frames.WriteFrameAsync(DerpFrameType.Pong, frame.Payload, cancellationToken).ConfigureAwait(false);
                    break;

                case DerpFrameType.KeepAlive:
                case DerpFrameType.Pong:
                case DerpFrameType.PeerGone:
                case DerpFrameType.PeerPresent:
                case DerpFrameType.Health:
                case DerpFrameType.Restarting:
                    // Bookkeeping the caller doesn't need in order to receive.
                    break;

                default:
                    // Unknown frames are skipped, so a newer relay can add some.
                    break;
            }
        }
    }

    /// <summary>Closes the connection to the relay.</summary>
    public ValueTask DisposeAsync() => _frames.DisposeAsync();

    private static async Task<Socket> DialAsync(DerpNode node, CancellationToken cancellationToken)
    {
        int port = node.DERPPort != 0 ? node.DERPPort : 443;
        Socket socket = new(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            // A DERP map may pin the relay's IP, saving a DNS lookup; if it
            // doesn't, or says "none", fall back to resolving the host name.
            if (IPAddress.TryParse(node.IPv4, out IPAddress? ip) || IPAddress.TryParse(node.IPv6, out ip))
            {
                await socket.ConnectAsync(new IPEndPoint(ip, port), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await socket.ConnectAsync(node.HostName, port, cancellationToken).ConfigureAwait(false);
            }
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static async Task UpgradeToDerpAsync(Stream stream, DerpNode node, CancellationToken cancellationToken)
    {
        string host = node.HostName;
        string request =
            $"GET {DerpProtocol.HttpPath} HTTP/1.1\r\n" +
            $"Host: {host}\r\n" +
            "Connection: Upgrade\r\n" +
            "Upgrade: DERP\r\n" +
            "\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request), cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

        string statusLine = await ReadHttpResponseAsync(stream, cancellationToken).ConfigureAwait(false);
        if (!statusLine.StartsWith("HTTP/1.1 101", StringComparison.Ordinal))
        {
            throw new DerpProtocolException($"DERP upgrade failed: {statusLine}");
        }
    }

    // ReadHttpResponseAsync reads the response head one byte at a time, up to
    // the blank line. Byte-at-a-time keeps us from consuming any DERP frame
    // bytes that follow it in the same stream.
    private static async Task<string> ReadHttpResponseAsync(Stream stream, CancellationToken cancellationToken)
    {
        const int MaxHeadBytes = 8 << 10;
        StringBuilder head = new();
        byte[] one = new byte[1];
        while (head.Length < MaxHeadBytes)
        {
            await stream.ReadExactlyAsync(one, cancellationToken).ConfigureAwait(false);
            head.Append((char)one[0]);
            if (head.Length >= 4 &&
                head[^4] == '\r' && head[^3] == '\n' && head[^2] == '\r' && head[^1] == '\n')
            {
                string text = head.ToString();
                return text[..text.IndexOf('\r', StringComparison.Ordinal)];
            }
        }
        throw new DerpProtocolException("DERP upgrade response head exceeded 8 KB");
    }

    private static async Task<DerpClient> HandshakeAsync(
        DerpFrameStream frames,
        NodePrivate privateKey,
        NodePublic? keyFromMetaCert,
        CancellationToken cancellationToken)
    {
        // 1. The relay greets us with its magic and public key.
        DerpFrame greeting = await frames.ReadFrameAsync(cancellationToken).ConfigureAwait(false);
        if (greeting.Type != DerpFrameType.ServerKey)
        {
            throw new DerpProtocolException($"expected a ServerKey frame, got 0x{(byte)greeting.Type:X2}");
        }
        if (greeting.Payload.Length < DerpProtocol.MagicLen + DerpProtocol.KeyLen ||
            !greeting.Payload.Span[..DerpProtocol.MagicLen].SequenceEqual(DerpProtocol.Magic))
        {
            throw new DerpProtocolException("invalid server greeting");
        }
        NodePublic serverKey = NodePublic.FromRaw32(
            greeting.Payload.Span.Slice(DerpProtocol.MagicLen, DerpProtocol.KeyLen));

        // The relay also advertises this key in its TLS meta certificate. The
        // two must agree: a mismatch means something is rewriting the
        // connection between us and the relay.
        if (keyFromMetaCert is NodePublic advertised && advertised != serverKey)
        {
            throw new DerpProtocolException(
                $"relay greeted with key {serverKey} but its certificate advertises {advertised}");
        }

        // 2. We answer with our public key and a sealed box naming our
        //    protocol version. The box proves we hold the matching private
        //    key, and it is what the relay keys our address by.
        NodePublic publicKey = privateKey.Public();
        byte[] info = JsonSerializer.SerializeToUtf8Bytes(
            new DerpClientInfo { Version = DerpProtocol.ProtocolVersion, CanAckPings = true },
            DerpJson.Options);
        byte[] nonce = PublicKeyBox.GenerateNonce();
        byte[] sealedInfo = PublicKeyBox.Create(info, nonce, privateKey.Raw32(), serverKey.Raw32());

        byte[] clientInfoFrame = new byte[DerpProtocol.KeyLen + nonce.Length + sealedInfo.Length];
        publicKey.Raw32().CopyTo(clientInfoFrame, 0);
        nonce.CopyTo(clientInfoFrame, DerpProtocol.KeyLen);
        sealedInfo.CopyTo(clientInfoFrame, DerpProtocol.KeyLen + nonce.Length);
        await frames.WriteFrameAsync(DerpFrameType.ClientInfo, clientInfoFrame, cancellationToken).ConfigureAwait(false);

        // 3. The relay replies with its own sealed info, which also proves it
        //    holds the private key behind the public one it greeted us with.
        DerpFrame reply = await frames.ReadFrameAsync(cancellationToken).ConfigureAwait(false);
        if (reply.Type != DerpFrameType.ServerInfo)
        {
            throw new DerpProtocolException($"expected a ServerInfo frame, got 0x{(byte)reply.Type:X2}");
        }
        DerpServerInfo serverInfo = OpenServerInfo(reply.Payload.Span, privateKey, serverKey);

        return new DerpClient(frames, privateKey, publicKey, serverKey, serverInfo);
    }

    private static DerpServerInfo OpenServerInfo(ReadOnlySpan<byte> payload, NodePrivate privateKey, NodePublic serverKey)
    {
        if (payload.Length < DerpProtocol.NonceLen || payload.Length > DerpProtocol.NonceLen + DerpProtocol.MaxInfoLen)
        {
            throw new DerpProtocolException($"ServerInfo frame of {payload.Length} bytes is out of range");
        }
        byte[] nonce = payload[..DerpProtocol.NonceLen].ToArray();
        byte[] box = payload[DerpProtocol.NonceLen..].ToArray();

        byte[] json;
        try
        {
            json = PublicKeyBox.Open(box, nonce, privateKey.Raw32(), serverKey.Raw32());
        }
        catch (Exception ex)
        {
            throw new DerpProtocolException($"failed to open the sealed box from server key {serverKey}", ex);
        }

        try
        {
            return JsonSerializer.Deserialize<DerpServerInfo>(json, DerpJson.Options) ?? new DerpServerInfo();
        }
        catch (JsonException ex)
        {
            throw new DerpProtocolException($"invalid ServerInfo JSON: {ex.Message}", ex);
        }
    }
}

/// <summary>What a client tells a DERP relay about itself when it connects.</summary>
public sealed class DerpClientInfo
{
    /// <summary>The DERP protocol version the client speaks.</summary>
    [JsonPropertyName("version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Version { get; set; }

    /// <summary>Whether the client answers the relay's pings.</summary>
    [JsonPropertyName("CanAckPings")]
    public bool CanAckPings { get; set; }
}

/// <summary>What a DERP relay tells a client about itself at login.</summary>
public sealed class DerpServerInfo
{
    /// <summary>The DERP protocol version the relay speaks.</summary>
    [JsonPropertyName("version")]
    public int Version { get; set; }

    /// <summary>The relay's per-client rate limit in bytes per second, if any.</summary>
    [JsonPropertyName("TokenBucketBytesPerSecond")]
    public int TokenBucketBytesPerSecond { get; set; }

    /// <summary>How far the rate limit lets a client burst above the rate.</summary>
    [JsonPropertyName("TokenBucketBytesBurst")]
    public int TokenBucketBytesBurst { get; set; }
}

internal static class DerpJson
{
    // The relay's JSON uses Go's exact field names, so no naming policy.
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = null,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };
}

// Copyright (c) Tailscale Inc & contributors
// SPDX-License-Identifier: BSD-3-Clause

namespace Tailcat.Derp;

/// <summary>
/// The one-byte frame type at the beginning of a DERP frame header. The
/// header's second field is a big-endian uint32 giving the length of the rest
/// of the frame.
/// </summary>
/// <remarks>
/// Protocol flow — login: the client connects, the server sends
/// <see cref="ServerKey"/>, the client sends <see cref="ClientInfo"/>, the
/// server sends <see cref="ServerInfo"/>. Steady state: the server
/// occasionally sends <see cref="KeepAlive"/> or <see cref="Ping"/>, the
/// client answers any <see cref="Ping"/> with a <see cref="Pong"/>, the
/// client sends <see cref="SendPacket"/>, and the server delivers it to the
/// recipient as <see cref="RecvPacket"/>.
/// </remarks>
public enum DerpFrameType : byte
{
    /// <summary>8B magic + 32B public key (+ 0 or more bytes for future use).</summary>
    ServerKey = 0x01,

    /// <summary>32B public key + 24B nonce + NaCl box of the JSON client info.</summary>
    ClientInfo = 0x02,

    /// <summary>24B nonce + NaCl box of the JSON server info.</summary>
    ServerInfo = 0x03,

    /// <summary>32B destination public key + packet bytes.</summary>
    SendPacket = 0x04,

    /// <summary>32B source public key + packet bytes (protocol version 2).</summary>
    RecvPacket = 0x05,

    /// <summary>No payload; a no-op that keeps the connection alive.</summary>
    KeepAlive = 0x06,

    /// <summary>1 byte: whether this server is the client's home node.</summary>
    NotePreferred = 0x07,

    /// <summary>
    /// 32B public key of a peer that is gone + 1 byte reason. Sent so the
    /// receiver can forget that a reverse path to that peer exists.
    /// </summary>
    PeerGone = 0x08,

    /// <summary>32B public key of a peer that is present, plus optional details.</summary>
    PeerPresent = 0x09,

    /// <summary>32B source public key + 32B destination public key + packet bytes.</summary>
    ForwardPacket = 0x0a,

    /// <summary>Subscribes to the region mesh's connection list. Privileged.</summary>
    WatchConns = 0x10,

    /// <summary>32B public key of the peer whose connection to close. Privileged.</summary>
    ClosePeer = 0x11,

    /// <summary>8 byte payload, to be echoed back in a <see cref="Pong"/>.</summary>
    Ping = 0x12,

    /// <summary>8 byte payload: the contents of the ping being answered.</summary>
    Pong = 0x13,

    /// <summary>
    /// Server to client: the text of an error describing why the connection is
    /// unhealthy. An empty message clears the error state.
    /// </summary>
    Health = 0x14,

    /// <summary>
    /// Server to client: the server is restarting. The payload is two
    /// big-endian uint32 millisecond durations: when to reconnect, and how
    /// long to keep trying.
    /// </summary>
    Restarting = 0x15,
}

/// <summary>A one-byte reason explaining why a peer is no longer reachable.</summary>
public enum DerpPeerGoneReason : byte
{
    /// <summary>The peer disconnected from this server.</summary>
    Disconnected = 0x00,

    /// <summary>The server doesn't know about this peer at all.</summary>
    NotHere = 0x01,
}

/// <summary>Constants of the DERP wire protocol.</summary>
public static class DerpProtocol
{
    /// <summary>
    /// The maximum size of a packet sent over DERP. This counts only the
    /// data bytes, not the on-wire framing overhead.
    /// </summary>
    public const int MaxPacketSize = 64 << 10;

    /// <summary>The DERP magic number, sent in the ServerKey frame: "DERP🔑".</summary>
    public static ReadOnlySpan<byte> Magic => "DERP\U0001F511"u8;

    /// <summary>The length of the magic number in bytes.</summary>
    public const int MagicLen = 8;

    /// <summary>The length of a NaCl box nonce.</summary>
    public const int NonceLen = 24;

    /// <summary>The length of a frame header: a type byte plus a uint32 length.</summary>
    public const int FrameHeaderLen = 1 + 4;

    /// <summary>The length of a public key.</summary>
    public const int KeyLen = 32;

    /// <summary>The maximum length of the JSON info blobs exchanged at login.</summary>
    public const int MaxInfoLen = 1 << 20;

    /// <summary>
    /// The protocol version this client speaks. Version 2 means received
    /// packets carry the source key at the start of the RecvPacket frame.
    /// </summary>
    public const int ProtocolVersion = 2;

    /// <summary>
    /// The minimum frequency at which the server sends keep-alive frames. The
    /// server adds jitter, so twice this can be taken as a missed keep-alive.
    /// </summary>
    public static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The header (with value "1") that tells the server the client wants no
    /// HTTP 101 response and will start speaking DERP immediately.
    /// </summary>
    public const string FastStartHeader = "Derp-Fast-Start";

    /// <summary>The HTTP path a DERP server serves the protocol on.</summary>
    public const string HttpPath = "/derp";
}

// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Sodium;
using Tailcat.Derp;
using Tailcat.Keys;

namespace Tailcat.TestSupport;

/// <summary>
/// A minimal in-memory DERP relay: it speaks the login handshake and routes
/// packets between connected clients by public key.
/// </summary>
/// <remarks>
/// It listens on loopback TCP with no TLS and no HTTP upgrade, so tests
/// drive <see cref="DerpClient.ConnectOverStreamAsync"/> directly. That
/// covers the framing, the sealed-box handshake, and packet routing without
/// touching the network.
/// </remarks>
public sealed class FakeDerpRelay : IAsyncDisposable
{
    private readonly Socket _listener;
    private readonly NodePrivate _privateKey = NodePrivate.NewKey();
    private readonly CancellationTokenSource _cts = new();
    private readonly Lock _mu = new();
    private readonly Dictionary<NodePublic, DerpFrameStream> _clients = [];
    private readonly List<Socket> _sockets = [];
    private readonly Task _acceptLoop;

    /// <summary>Starts a relay on a loopback port of its own.</summary>
    public FakeDerpRelay()
    {
        _listener = new Socket(SocketType.Stream, ProtocolType.Tcp);
        _listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        _listener.Listen(8);
        PublicKey = _privateKey.Public();
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    /// <summary>The relay's own public key, as sent in its greeting.</summary>
    public NodePublic PublicKey { get; }

    /// <summary>What the relay reports in its ServerInfo.</summary>
    public DerpServerInfo ServerInfo { get; init; } = new()
    {
        Version = DerpProtocol.ProtocolVersion,
        TokenBucketBytesPerSecond = 1_000_000,
        TokenBucketBytesBurst = 2_000_000,
    };

    /// <summary>The client info the last connecting client sent.</summary>
    public DerpClientInfo? LastClientInfo { get; private set; }

    /// <summary>
    /// Where the relay says who logged in, who went, and what it could not
    /// deliver. Nothing, by default.
    /// </summary>
    /// <remarks>
    /// A machine that is not reachable and one that is reachable and silent
    /// look identical from either end of a link. Only the relay in the middle
    /// knows which it was.
    /// </remarks>
    public Action<string>? Log { get; init; }

    /// <summary>Opens a raw TCP stream to the relay, ready for the handshake.</summary>
    public async Task<Stream> DialAsync(CancellationToken cancellationToken = default)
    {
        Socket socket = new(SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync((IPEndPoint)_listener.LocalEndPoint!, cancellationToken);
        return new NetworkStream(socket, ownsSocket: true);
    }

    /// <summary>Stops the relay and drops every client it is holding.</summary>
    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _listener.Dispose();
        lock (_mu)
        {
            foreach (Socket s in _sockets)
            {
                s.Dispose();
            }
        }
        try
        {
            await _acceptLoop;
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        {
        }
        _cts.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Socket socket = await _listener.AcceptAsync(ct);
            lock (_mu)
            {
                _sockets.Add(socket);
            }
            _ = Task.Run(() => ServeAsync(socket, ct), ct);
        }
    }

    private async Task ServeAsync(Socket socket, CancellationToken ct)
    {
        DerpFrameStream frames = new(new NetworkStream(socket, ownsSocket: true));
        NodePublic? registered = null;
        try
        {
            NodePublic clientKey = await LoginAsync(frames, ct);
            lock (_mu)
            {
                _clients[clientKey] = frames;
            }
            registered = clientKey;
            Log?.Invoke($"{Short(clientKey)} logged in");
            await RouteAsync(frames, clientKey, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log?.Invoke($"{(registered is { } who ? Short(who) : "a client")} ended: {ex.Message}");
        }
        finally
        {
            // A client that has gone must stop being somewhere to deliver to.
            // Left in, its entry is handed to the next sender, whose write
            // into a dead socket used to end that sender's own connection —
            // so one machine's disappearance took the other's relay with it,
            // and both spent the test reconnecting past each other.
            if (registered is { } key)
            {
                Forget(key, frames);
            }
        }
    }

    /// <summary>Takes a client out, unless it has already been replaced.</summary>
    /// <remarks>
    /// Only when the entry is still this connection: a client that reconnected
    /// under the same key has overwritten it, and removing that would strand
    /// the connection which is now the live one.
    /// </remarks>
    private void Forget(NodePublic client, DerpFrameStream frames)
    {
        lock (_mu)
        {
            if (_clients.TryGetValue(client, out DerpFrameStream? current) && ReferenceEquals(current, frames))
            {
                _clients.Remove(client);
                Log?.Invoke($"{Short(client)} forgotten");
            }
        }
    }

    private async Task<NodePublic> LoginAsync(DerpFrameStream frames, CancellationToken ct)
    {
        // Greet with the magic and our public key.
        byte[] greeting = new byte[DerpProtocol.MagicLen + DerpProtocol.KeyLen];
        DerpProtocol.Magic.CopyTo(greeting);
        PublicKey.Raw32().CopyTo(greeting, DerpProtocol.MagicLen);
        await frames.WriteFrameAsync(DerpFrameType.ServerKey, greeting, ct);

        // Read the client's key and its sealed info.
        DerpFrame info = await frames.ReadFrameAsync(ct);
        if (info.Type != DerpFrameType.ClientInfo)
        {
            throw new DerpProtocolException($"expected ClientInfo, got 0x{(byte)info.Type:X2}");
        }
        NodePublic clientKey = NodePublic.FromRaw32(info.Payload.Span[..DerpProtocol.KeyLen]);
        byte[] nonce = info.Payload.Span.Slice(DerpProtocol.KeyLen, DerpProtocol.NonceLen).ToArray();
        byte[] box = info.Payload.Span[(DerpProtocol.KeyLen + DerpProtocol.NonceLen)..].ToArray();
        byte[] json = PublicKeyBox.Open(box, nonce, _privateKey.Raw32(), clientKey.Raw32());
        LastClientInfo = JsonSerializer.Deserialize<DerpClientInfo>(json);

        // Answer with our own sealed info.
        byte[] replyJson = JsonSerializer.SerializeToUtf8Bytes(ServerInfo);
        byte[] replyNonce = PublicKeyBox.GenerateNonce();
        byte[] replyBox = PublicKeyBox.Create(replyJson, replyNonce, _privateKey.Raw32(), clientKey.Raw32());
        byte[] reply = new byte[replyNonce.Length + replyBox.Length];
        replyNonce.CopyTo(reply, 0);
        replyBox.CopyTo(reply, replyNonce.Length);
        await frames.WriteFrameAsync(DerpFrameType.ServerInfo, reply, ct);

        return clientKey;
    }

    private async Task RouteAsync(DerpFrameStream frames, NodePublic clientKey, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            DerpFrame frame = await frames.ReadFrameAsync(ct);
            switch (frame.Type)
            {
                case DerpFrameType.SendPacket:
                    NodePublic dst = NodePublic.FromRaw32(frame.Payload.Span[..DerpProtocol.KeyLen]);
                    ReadOnlyMemory<byte> packet = frame.Payload[DerpProtocol.KeyLen..];

                    DerpFrameStream? peer;
                    lock (_mu)
                    {
                        _clients.TryGetValue(dst, out peer);
                    }
                    if (peer is null)
                    {
                        Log?.Invoke($"{Short(clientKey)} -> {Short(dst)}: nobody there");
                        await SayPeerGoneAsync(frames, dst, ct).ConfigureAwait(false);
                        break;
                    }

                    byte[] delivery = new byte[DerpProtocol.KeyLen + packet.Length];
                    clientKey.Raw32().CopyTo(delivery, 0);
                    packet.Span.CopyTo(delivery.AsSpan(DerpProtocol.KeyLen));
                    try
                    {
                        await peer.WriteFrameAsync(DerpFrameType.RecvPacket, delivery, ct).ConfigureAwait(false);
                    }
#pragma warning disable CA1031 // Whatever the destination did, it is not this sender's fault and must not be its ending.
                    catch (Exception) when (!ct.IsCancellationRequested)
#pragma warning restore CA1031
                    {
                        // The destination went while this packet was on its
                        // way. A real relay drops it and says so; letting the
                        // failure out of here would end the *sender's*
                        // connection for the destination's mistake.
                        Forget(dst, peer);
                        await SayPeerGoneAsync(frames, dst, ct).ConfigureAwait(false);
                    }
                    break;

                case DerpFrameType.Pong:
                    break;

                default:
                    break;
            }
        }
    }

    /// <summary>Enough of a key to follow it through a log.</summary>
    private static string Short(NodePublic key) => Convert.ToHexStringLower(key.Raw32())[..12];

    /// <summary>Tells a sender that the machine it aimed at is not here.</summary>
    private static Task SayPeerGoneAsync(DerpFrameStream frames, NodePublic gone, CancellationToken ct)
    {
        byte[] payload = new byte[DerpProtocol.KeyLen + 1];
        gone.Raw32().CopyTo(payload, 0);
        payload[^1] = (byte)DerpPeerGoneReason.NotHere;
        return frames.WriteFrameAsync(DerpFrameType.PeerGone, payload, ct);
    }

    /// <summary>Sends a ping the client is expected to echo back.</summary>
    public async Task PingAsync(NodePublic client, byte[] payload, CancellationToken ct = default)
    {
        DerpFrameStream? frames;
        lock (_mu)
        {
            _clients.TryGetValue(client, out frames);
        }
        ArgumentNullException.ThrowIfNull(frames);
        await frames.WriteFrameAsync(DerpFrameType.Ping, payload, ct);
    }

    /// <summary>
    /// Hangs up on one client, the way a restarting relay does, so callers can
    /// be tested for reconnecting.
    /// </summary>
    public void DisconnectClient(NodePublic client)
    {
        DerpFrameStream? frames;
        lock (_mu)
        {
            if (!_clients.Remove(client, out frames))
            {
                return;
            }
        }
        _ = frames.DisposeAsync().AsTask();
    }

    /// <summary>How many clients are logged in right now.</summary>
    public int ClientCount
    {
        get
        {
            lock (_mu)
            {
                return _clients.Count;
            }
        }
    }

    /// <summary>Waits until <paramref name="client"/> has finished logging in.</summary>
    public async Task WaitForClientAsync(NodePublic client, CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            lock (_mu)
            {
                if (_clients.ContainsKey(client))
                {
                    return;
                }
            }
            await Task.Delay(10, ct);
        }
    }
}

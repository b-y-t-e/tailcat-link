// Copyright (c) Tailscale Inc & contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Tailcat.Tests;

/// <summary>
/// Covers ProxyConns. It is the half-close behaviour TestHalfClose checks in
/// Go end-to-end through the tunnel; here it is checked directly over
/// loopback TCP, which is the part that doesn't need the WireGuard stack.
/// </summary>
public class ProxyTests
{
    /// <summary>
    /// A client's write shutdown must propagate through the proxy as a
    /// half-close rather than tearing down the whole connection: the backend
    /// must still be able to send its response after seeing the client's EOF,
    /// netcat style.
    /// </summary>
    [Fact]
    public async Task ProxyPropagatesHalfClose()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        // The backend reads until EOF and only then writes its response.
        using Socket backendListener = Listen();
        Task backendTask = Task.Run(async () =>
        {
            using Socket s = await backendListener.AcceptAsync(ct);
            await using NetworkStream ns = new(s, ownsSocket: false);
            byte[] got = await ReadAllAsync(ns, ct);
            await ns.WriteAsync(Encoding.UTF8.GetBytes($"read {got.Length} bytes"), ct);
            s.Shutdown(SocketShutdown.Send);
        }, ct);

        // The proxy accepts one connection and splices it to the backend.
        using Socket proxyListener = Listen();
        Task proxyTask = Task.Run(async () =>
        {
            Socket front = await proxyListener.AcceptAsync(ct);
            Socket back = new(SocketType.Stream, ProtocolType.Tcp);
            await back.ConnectAsync((IPEndPoint)backendListener.LocalEndPoint!, ct);
            await Proxy.ConnsAsync(new SocketStream(front), new SocketStream(back), ct);
        }, ct);

        using Socket client = new(SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync((IPEndPoint)proxyListener.LocalEndPoint!, ct);
        await using NetworkStream clientStream = new(client, ownsSocket: false);

        const string Request = "hello, backend";
        await clientStream.WriteAsync(Encoding.UTF8.GetBytes(Request), ct);
        client.Shutdown(SocketShutdown.Send);

        byte[] resp = await ReadAllAsync(clientStream, ct);
        Assert.Equal($"read {Request.Length} bytes", Encoding.UTF8.GetString(resp));

        await backendTask;
        await proxyTask;
    }

    /// <summary>Both directions are copied, not just one.</summary>
    [Fact]
    public async Task ProxyCopiesBothDirections()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        using Socket backendListener = Listen();
        Task backendTask = Task.Run(async () =>
        {
            using Socket s = await backendListener.AcceptAsync(ct);
            await using NetworkStream ns = new(s, ownsSocket: false);
            byte[] buf = new byte[5];
            await ns.ReadExactlyAsync(buf, ct);
            await ns.WriteAsync(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(buf).ToUpperInvariant()), ct);
            s.Shutdown(SocketShutdown.Send);
        }, ct);

        using Socket proxyListener = Listen();
        Task proxyTask = Task.Run(async () =>
        {
            Socket front = await proxyListener.AcceptAsync(ct);
            Socket back = new(SocketType.Stream, ProtocolType.Tcp);
            await back.ConnectAsync((IPEndPoint)backendListener.LocalEndPoint!, ct);
            await Proxy.ConnsAsync(new SocketStream(front), new SocketStream(back), ct);
        }, ct);

        using Socket client = new(SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync((IPEndPoint)proxyListener.LocalEndPoint!, ct);
        await using NetworkStream clientStream = new(client, ownsSocket: false);

        await clientStream.WriteAsync("meow!"u8.ToArray(), ct);
        byte[] buf = new byte[5];
        await clientStream.ReadExactlyAsync(buf, ct);
        Assert.Equal("MEOW!", Encoding.UTF8.GetString(buf));

        client.Shutdown(SocketShutdown.Send);
        await backendTask;
        await proxyTask;
    }

    private static Socket Listen()
    {
        Socket s = new(SocketType.Stream, ProtocolType.Tcp);
        s.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        s.Listen(1);
        return s;
    }

    private static async Task<byte[]> ReadAllAsync(Stream s, CancellationToken ct)
    {
        using MemoryStream buf = new();
        await s.CopyToAsync(buf, ct);
        return buf.ToArray();
    }
}

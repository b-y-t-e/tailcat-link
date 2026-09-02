// Copyright (c) Tailscale Inc & contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Diagnostics;
using System.Net;
using System.Text;
using Tailcat;
using Tailcat.Net;

// tailcat-demo brings up a node and either waits for a peer or dials one, so
// that a direct path can be verified between two real machines on different
// networks. Run "listen" on one, then "connect <key>" on the other.

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Console.WriteLine("""
        tailcat-demo — connect two machines through Tailscale's relays

          tailcat-demo listen
              Brings up a node and prints its public key. Echoes back
              whatever a peer sends, uppercased.

          tailcat-demo connect <address>
              Dials the node at that address, sends what you type, and prints
              the reply. Type "quit" to exit.

          tailcat-demo ping <address>
              Dials, then reports the path and round trip once a second.

        An address carries the peer public key and the relay region it
        listens in, so two machines far apart still find each other.

        Both sides connect outbound only, so neither needs an open port.
        """);
    return 0;
}

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

try
{
    switch (args[0])
    {
        case "listen":
            await ListenAsync(cts.Token);
            return 0;

        case "connect" when args.Length >= 2:
            await ConnectAsync(args[1], interactive: true, cts.Token);
            return 0;

        case "ping" when args.Length >= 2:
            await ConnectAsync(args[1], interactive: false, cts.Token);
            return 0;

        default:
            Console.Error.WriteLine($"unknown command: {string.Join(' ', args)}");
            return 2;
    }
}
catch (OperationCanceledException)
{
    Console.WriteLine("\ninterrupted");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 1;
}

static async Task<TailcatNode> StartNodeAsync(CancellationToken ct)
{
    Console.WriteLine("measuring relay regions and connecting ...");
    Stopwatch sw = Stopwatch.StartNew();
    // The observer is what makes a relayed-instead-of-direct session
    // explainable rather than just slow.
    TailcatNode node = await TailcatNode.CreateAsync(
        new TailcatNodeOptions
        {
            Observer = new TextTailcatObserver(line => Console.WriteLine($"  [{line}]")),
        },
        ct);
    Console.WriteLine($"node up in {sw.ElapsedMilliseconds} ms");
    Console.WriteLine($"  region:    {node.HomeRegionId}");

    IReadOnlyList<IPEndPoint> endpoints = await node.LocalEndpointsAsync(ct);
    Console.WriteLine($"  reachable: {string.Join(", ", endpoints)}");
    return node;
}

static async Task ListenAsync(CancellationToken ct)
{
    await using TailcatNode node = await StartNodeAsync(ct);

    Console.WriteLine();
    Console.WriteLine("Run this on the other machine:");
    Console.WriteLine();
    Console.WriteLine($"    tailcat-demo connect {node.Address}");
    Console.WriteLine();
    Console.WriteLine("waiting for a peer ...");

    await foreach (TailcatConnection conn in node.AcceptConnectionsAsync(ct))
    {
        Console.WriteLine($"peer connected: {conn.Peer}");
        Console.WriteLine($"  path: {conn.CurrentPath}");
        conn.PathChanged += p => Console.WriteLine($"  path changed -> {p}");
        _ = Task.Run(() => ServeAsync(conn, ct), ct);
    }
}

static async Task ServeAsync(TailcatConnection conn, CancellationToken ct)
{
    try
    {
        while (!ct.IsCancellationRequested)
        {
            Stream stream = await conn.AcceptStreamAsync(ct);
            _ = Task.Run(async () =>
            {
                await using (stream)
                {
                    byte[] buf = new byte[4096];
                    while (true)
                    {
                        int n = await stream.ReadAsync(buf, ct);
                        if (n == 0)
                        {
                            return;
                        }
                        string text = Encoding.UTF8.GetString(buf, 0, n);
                        Console.WriteLine($"  <- {text.TrimEnd()}  [{conn.CurrentPath}]");
                        await stream.WriteAsync(Encoding.UTF8.GetBytes(text.ToUpperInvariant()), ct);
                        await stream.FlushAsync(ct);
                    }
                }
            }, ct);
        }
    }
    catch (Exception ex) when (ex is OperationCanceledException or IOException)
    {
        Console.WriteLine($"peer {conn.Peer} disconnected");
    }
}

static async Task ConnectAsync(string peerAddress, bool interactive, CancellationToken ct)
{
    ConnBlob address = new(peerAddress.Trim());
    if (!address.TryParse(out ConnInfo? info))
    {
        Console.Error.WriteLine($"not a tailcat address: {peerAddress}");
        throw new InvalidOperationException("expected an address like tcomFwWC... as printed by \"tailcat-demo listen\"");
    }

    await using TailcatNode node = await StartNodeAsync(ct);
    Console.WriteLine($"  this node: {node.Address}");
    Console.WriteLine();
    Console.WriteLine($"dialing {info.ServerPublic} in region {info.RegionID} ...");

    Stopwatch sw = Stopwatch.StartNew();
    await using TailcatConnection conn = await node.ConnectAsync(address, ct);
    Console.WriteLine($"session up in {sw.ElapsedMilliseconds} ms over {conn.CurrentPath}");
    conn.PathChanged += p => Console.WriteLine($"path changed -> {p}");

    await using Stream stream = await conn.OpenStreamAsync(ct);
    byte[] buf = new byte[4096];

    if (!interactive)
    {
        // "ping" mode: keep the session busy and report where it is running,
        // which is what tells you whether the hole punch worked.
        for (int i = 1; !ct.IsCancellationRequested; i++)
        {
            Stopwatch rtt = Stopwatch.StartNew();
            await stream.WriteAsync(Encoding.UTF8.GetBytes($"ping {i}\n"), ct);
            await stream.FlushAsync(ct);
            int n = await stream.ReadAsync(buf, ct);
            if (n == 0)
            {
                Console.WriteLine("peer closed the stream");
                return;
            }
            Console.WriteLine(
                $"#{i,-4} {rtt.Elapsed.TotalMilliseconds,6:F1} ms  {conn.CurrentPath}");
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }
        return;
    }

    Console.WriteLine("type a line to send it; \"paths\" lists candidates; \"quit\" exits");
    while (!ct.IsCancellationRequested)
    {
        Console.Write("> ");
        string? line = await Console.In.ReadLineAsync(ct);
        if (line is null or "quit")
        {
            return;
        }
        if (line == "paths")
        {
            foreach (PeerPath path in conn.Paths)
            {
                Console.WriteLine($"    {path}");
            }
            continue;
        }

        await stream.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"), ct);
        await stream.FlushAsync(ct);
        int n = await stream.ReadAsync(buf, ct);
        if (n == 0)
        {
            Console.WriteLine("peer closed the stream");
            return;
        }
        Console.WriteLine($"  <- {Encoding.UTF8.GetString(buf, 0, n).TrimEnd()}  [{conn.CurrentPath}]");
    }
}

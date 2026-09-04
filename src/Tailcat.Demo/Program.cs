// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Tailcat;
using Tailcat.Link;
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

          tailcat-demo natcheck
              Asks several STUN servers what address this machine maps to,
              and says whether a direct path is possible at all from here.

          tailcat-demo punch
              Bare UDP hole punching, with none of this library involved.
              Prints this machine's public address, waits for the peer's,
              then sends and reports. Run it on both ends at once.

          tailcat-demo host [--forget]
              A Tailcat.Link host: prints an invitation code and echoes what
              it is asked, uppercased. What clients/browser is tested
              against. --forget drops the stored pairing first, so a second
              client can take the place of the first.

          --relay-only
              Offers only the relayed transport, whatever this machine can
              do. What a browser and a Windows 10 machine get, on a machine
              that could have used QUIC.

        An address carries the peer public key and the relay region it
        listens in, so two machines far apart still find each other.

        Both sides connect outbound only, so neither needs an open port.
        """);
    return 0;
}

// Anywhere in the arguments: it changes how the node is built, not what the
// command does with it.
bool relayOnly = args.Contains("--relay-only");
args = [.. args.Where(a => a != "--relay-only")];

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
            await ListenAsync(relayOnly, cts.Token);
            return 0;

        case "connect" when args.Length >= 2:
            await ConnectAsync(args[1], interactive: true, relayOnly, cts.Token);
            return 0;

        case "ping" when args.Length >= 2:
            await ConnectAsync(args[1], interactive: false, relayOnly, cts.Token);
            return 0;

        case "natcheck":
            return await NatCheckAsync(cts.Token);

        case "punch":
            return await PunchAsync(args.Length >= 2 ? args[1] : null, cts.Token);

        case "host":
            return await HostAsync(args.Contains("--forget"), cts.Token);

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

static async Task<TailcatNode> StartNodeAsync(bool relayOnly, CancellationToken ct)
{
    Console.WriteLine("measuring relay regions and connecting ...");
    Stopwatch sw = Stopwatch.StartNew();
    // The observer is what makes a relayed-instead-of-direct session
    // explainable rather than just slow.
    TailcatNode node = await TailcatNode.CreateAsync(
        new TailcatNodeOptions
        {
            // Holding the node to relay1 is how a machine with QUIC behaves
            // like one without: the same thing Windows 10 and a browser get,
            // reproducible on a machine that has QUIC.
            Transports = relayOnly ? [PeerTransport.Relay1] : null,
            Observer = new TextTailcatObserver(line => Console.WriteLine($"  [{line}]")),
        },
        ct);
    Console.WriteLine($"node up in {sw.ElapsedMilliseconds} ms");
    Console.WriteLine($"  region:    {node.HomeRegionId}");
    // The raw key, not just the address: a client that has not implemented
    // ConnBlob yet still needs something to dial.
    Console.WriteLine($"  key:       {node.PublicKey}");

    IReadOnlyList<IPEndPoint> endpoints = await node.LocalEndpointsAsync(ct);
    Console.WriteLine($"  reachable: {string.Join(", ", endpoints)}");
    return node;
}

static async Task ListenAsync(bool relayOnly, CancellationToken ct)
{
    await using TailcatNode node = await StartNodeAsync(relayOnly, ct);

    Console.WriteLine();
    Console.WriteLine("Run this on the other machine:");
    Console.WriteLine();
    Console.WriteLine($"    tailcat-demo connect {node.Address}");
    Console.WriteLine();
    Console.WriteLine("waiting for a peer ...");

    await foreach (ITailcatConnection conn in node.AcceptConnectionsAsync(ct))
    {
        Console.WriteLine($"peer connected: {conn.Peer}");
        Console.WriteLine($"  path: {conn.CurrentPath}");
        conn.PathChanged += p => Console.WriteLine($"  path changed -> {p}");
        _ = Task.Run(() => ServeAsync(conn, ct), ct);
    }
}

static async Task ServeAsync(ITailcatConnection conn, CancellationToken ct)
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

static async Task ConnectAsync(string peerAddress, bool interactive, bool relayOnly, CancellationToken ct)
{
    ConnBlob address = new(peerAddress.Trim());
    if (!address.TryParse(out ConnInfo? info))
    {
        Console.Error.WriteLine($"not a tailcat address: {peerAddress}");
        throw new InvalidOperationException("expected an address like tcomFwWC... as printed by \"tailcat-demo listen\"");
    }

    await using TailcatNode node = await StartNodeAsync(relayOnly, ct);
    Console.WriteLine($"  this node: {node.Address}");
    Console.WriteLine();
    Console.WriteLine($"dialing {info.ServerPublic} in region {info.RegionID} ...");

    Stopwatch sw = Stopwatch.StartNew();
    await using ITailcatConnection conn = await node.ConnectAsync(address, ct);
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

// Classifies the NAT this machine sits behind, which is what decides whether
// a direct path is possible at all. One socket asks several servers in turn:
// if they all report the same port, the mapping does not depend on who is
// being talked to and a peer can be told where to aim. If the port changes
// per server, the NAT is symmetric — the address a peer is given is one that
// was only ever valid toward the server that reported it, so no amount of
// probing will land, and the relay is the only way through.
static async Task<int> NatCheckAsync(CancellationToken ct)
{
    (string Host, int Port)[] servers =
    [
        ("stun.cloudflare.com", 3478),
        ("stun.l.google.com", 19302),
        ("stun.nextcloud.com", 443),
    ];

    using Socket udp = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    udp.Bind(new IPEndPoint(IPAddress.Any, 0));
    int localPort = ((IPEndPoint)udp.LocalEndPoint!).Port;
    Console.WriteLine($"local udp port : {localPort}");
    Console.WriteLine();

    List<IPEndPoint> mapped = [];
    foreach ((string host, int port) in servers)
    {
        IPEndPoint? answer = await AskStunAsync(udp, host, port, ct);
        Console.WriteLine($"{host}:{port,-6} -> {(answer is null ? "brak odpowiedzi" : answer.ToString())}");
        if (answer is not null)
        {
            mapped.Add(answer);
        }
    }

    Console.WriteLine();
    if (mapped.Count == 0)
    {
        Console.WriteLine("WNIOSEK: zaden serwer nie odpowiedzial — ta siec blokuje wychodzace UDP.");
        Console.WriteLine("         Sciezka bezposrednia jest niemozliwa, zostaje relay.");
        return 0;
    }
    if (mapped.Count < 2)
    {
        Console.WriteLine("WNIOSEK: tylko jedna odpowiedz, za malo by rozpoznac typ NAT-u.");
        return 0;
    }

    bool sameAddress = mapped.TrueForAll(m => m.Address.Equals(mapped[0].Address));
    bool samePort = mapped.TrueForAll(m => m.Port == mapped[0].Port);

    Console.WriteLine($"publiczny adres: {mapped[0].Address}");
    Console.WriteLine($"port zachowany : {(mapped[0].Port == localPort ? "tak" : "nie")}");
    Console.WriteLine();

    if (samePort && sameAddress)
    {
        Console.WriteLine("WNIOSEK: mapowanie NIEZALEZNE od celu (cone NAT).");
        Console.WriteLine("         Przebicie z tej sieci jest mozliwe.");
    }
    else
    {
        Console.WriteLine("WNIOSEK: NAT SYMETRYCZNY — kazdy cel dostaje inny port zewnetrzny.");
        Console.WriteLine($"         Zaobserwowane porty: {string.Join(", ", mapped.Select(m => m.Port))}");
        Console.WriteLine("         Adres podany peerowi jest wazny tylko wobec serwera STUN,");
        Console.WriteLine("         wiec przebicie z tej sieci jest niemozliwe. Relay to jedyna droga.");
    }
    return 0;
}

static async Task<IPEndPoint?> AskStunAsync(Socket udp, string host, int port, CancellationToken ct)
{
    IPAddress[] addresses;
    try
    {
        addresses = await Dns.GetHostAddressesAsync(host, ct);
    }
    catch (SocketException)
    {
        return null;
    }
    if (Array.Find(addresses, a => a.AddressFamily == AddressFamily.InterNetwork) is not IPAddress ip)
    {
        return null;
    }

    byte[] request = Stun.BuildBindingRequest(out byte[] transactionId);
    using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
    timeout.CancelAfter(TimeSpan.FromSeconds(4));
    byte[] buf = new byte[1500];
    try
    {
        await udp.SendToAsync(request, new IPEndPoint(ip, port), timeout.Token);
        while (true)
        {
            SocketReceiveFromResult got = await udp.ReceiveFromAsync(
                buf, new IPEndPoint(IPAddress.Any, 0), timeout.Token);
            if (Stun.TryParseBindingResponse(buf.AsSpan(0, got.ReceivedBytes), transactionId, out IPEndPoint? answer))
            {
                return answer;
            }
        }
    }
    catch (Exception ex) when (ex is OperationCanceledException or SocketException)
    {
        return null;
    }
}

// Hole punching stripped to its bones: one socket, one STUN query, and raw
// datagrams to the address the operator pastes in. Nothing from Tailcat.Net
// takes part, so if this works and a session still will not leave the relay,
// the fault is in the library rather than in the two networks.
static async Task<int> PunchAsync(string? peerAddress, CancellationToken ct)
{
    using Socket udp = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    udp.Bind(new IPEndPoint(IPAddress.Any, 0));

    // Windows reports the ICMP rejection of one of our own datagrams as a
    // reset on the next receive; without this the first unanswered probe ends
    // the test rather than the address it was aimed at.
    if (OperatingSystem.IsWindows())
    {
        udp.IOControl(-1744830452, [0, 0, 0, 0], null);
    }

    IPEndPoint? mine = await AskStunAsync(udp, "stun.cloudflare.com", 3478, ct);
    if (mine is null)
    {
        Console.Error.WriteLine("STUN nie odpowiedzial; bez adresu publicznego nie ma czego probowac");
        return 1;
    }

    Console.WriteLine();
    Console.WriteLine($"    MOJ ADRES:  {mine}");
    Console.WriteLine();
    string? typed = peerAddress;
    if (typed is null)
    {
        Console.Write("wklej adres drugiej maszyny (ip:port) i Enter: ");
        typed = await Console.In.ReadLineAsync(ct);
    }
    if (!IPEndPoint.TryParse(typed?.Trim() ?? "", out IPEndPoint? peer))
    {
        Console.Error.WriteLine("to nie jest adres ip:port");
        return 1;
    }

    Console.WriteLine($"sonduje {peer} co 250 ms; Ctrl+C konczy");
    int sent = 0;
    int received = 0;

    Task sender = Task.Run(async () =>
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await udp.SendToAsync("PUNCH?"u8.ToArray(), SocketFlags.None, peer, ct);
                sent++;
            }
            catch (SocketException ex)
            {
                Console.WriteLine($"  wysylka nie wyszla: {ex.SocketErrorCode}");
            }
            await Task.Delay(250, ct);
        }
    }, ct);

    Task receiver = Task.Run(async () =>
    {
        byte[] buf = new byte[1500];
        while (!ct.IsCancellationRequested)
        {
            try
            {
                SocketReceiveFromResult got = await udp.ReceiveFromAsync(
                    buf, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), ct);
                received++;
                string text = Encoding.ASCII.GetString(buf, 0, Math.Min(got.ReceivedBytes, 6));
                Console.WriteLine($"  <<< {got.ReceivedBytes} B \"{text}\" od {got.RemoteEndPoint}   (wyslano {sent}, odebrano {received})");
                if (text == "PUNCH?")
                {
                    await udp.SendToAsync("PUNCH!"u8.ToArray(), SocketFlags.None, got.RemoteEndPoint, ct);
                }
            }
            catch (SocketException ex)
            {
                Console.WriteLine($"  odbior nie wyszedl: {ex.SocketErrorCode}");
            }
        }
    }, ct);

    // Progress even when nothing arrives, so silence is distinguishable from
    // a stalled program.
    while (!ct.IsCancellationRequested)
    {
        await Task.Delay(5000, ct);
        Console.WriteLine(received == 0
            ? $"... wyslano {sent}, nic nie przyszlo"
            : $"... wyslano {sent}, odebrano {received}  <-- PRZEBICIE DZIALA");
    }

    await Task.WhenAll(sender, receiver);
    return 0;
}

// A Tailcat.Link host, written the way the README says to write one. It knows
// nothing about who will join: a .NET peer settles on QUIC, a browser on
// relay1, and neither shows up here.
static async Task<int> HostAsync(bool forget, CancellationToken ct)
{
    const string AppName = "tailcat-demo-host";

    if (forget)
    {
        // A host pins the first machine that uses a code and refuses everyone
        // after it, so a second client needs the pairing cleared first.
        await TailcatLink.ForgetAsync(AppName, cancellationToken: ct);
        Console.WriteLine("forgot the stored pairing");
    }

    await using ILink link = await TailcatLink.HostAsync(AppName, cancellationToken: ct);
    link.OnRequest(command =>
    {
        Console.WriteLine($"  <- {(command.Length > 48 ? command[..48] + "..." : command)}  ({command.Length} B)");
        return command.ToUpperInvariant();
    });

    Console.WriteLine();
    Console.WriteLine("Give this to the other end, once:");
    Console.WriteLine();
    Console.WriteLine($"    {link.InvitationCode.Value}");
    Console.WriteLine();
    Console.WriteLine("waiting for a peer ...");

    // Ask the peer something once it arrives: the direction nobody requested
    // is the one that breaks first when a transport only half works.
    _ = Task.Run(async () =>
    {
        try
        {
            await link.WaitUntilConnectedAsync(ct);
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
            Console.WriteLine($"  -> the peer answered: {await link.RequestAsync("what time is it there?", ct)}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.WriteLine($"  -> asking the peer failed: {ex.Message}");
        }
    }, ct);

    try
    {
        await Task.Delay(Timeout.Infinite, ct);
    }
    catch (OperationCanceledException)
    {
    }
    return 0;
}

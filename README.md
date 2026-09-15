# tailcat-link

Two machines that cannot see each other — different networks, behind routers,
no open port, no VPN, no account — pair once with a short code and stay in
touch for as long as they are switched on. Either one can ask the other
something, tell it something, or send it a file of any size.

```
dotnet add package Tailcat.Link
```

## What it is built on

This is a .NET 10 library that started as a port of
[tailscale/tailcat](https://github.com/tailscale/tailcat), a small experiment
from Tailscale that connects two computers using nothing but Tailscale's
public relay servers (called DERP). On top of that port it adds its own
networking layer and a link that looks after itself: it pairs, reconnects,
resumes and keeps working when a laptop changes Wi-Fi.

It does not use WireGuard and does not talk to the Go version of tailcat; the
connection itself is QUIC. It is not affiliated with Tailscale.

## How it works, roughly

```
   machine A ──outbound──►  relay server  ◄──outbound── machine B
        │                  (meeting point)                   │
        └──────────── direct path, when one can be made ─────┘
```

1. **Both machines dial out to a relay.** Tailscale runs relay servers around
   the world, reachable on port 443 like any website. Because both ends only
   connect *out*, neither needs an open port, a firewall rule or a public IP.
   The relay passes packets between them, addressed by public key.
2. **They introduce themselves through the relay.** The code from pairing
   tells one machine where to find the other and proves it was invited. The
   messages are sealed, so the relay can pass them on but cannot read or
   forge them.
3. **They try to go direct.** Both machines send probes at each other's
   addresses at the same time, which usually opens a way through both routers
   (hole punching). If it works, traffic moves onto the direct path and gets
   faster; if the networks will not allow it, everything keeps working over
   the relay, just slower.
4. **Everything is encrypted end to end.** The relay only ever sees bytes it
   cannot read.
5. **The link looks after itself.** A dropped connection, a new network, a
   reboot or a relay going away all end the same way: the link notices, builds
   a new connection, and carries on with whatever was in flight — a
   half-sent file continues from where it stopped.

A browser can be one end too (it cannot punch holes, so it always stays on the
relay) — see [clients/browser](clients/browser/README.md).

## Quick start

On the machine to be reached:

```csharp
await using ILink link = await TailcatLink.HostAsync("my-app");
Console.WriteLine(link.InvitationCode);         // show it once, as text or a QR code
link.OnRequest(command => Run(command));        // answer whatever the other machine asks
```

On the other machine, with that code:

```csharp
await using ILink link = await TailcatLink.JoinAsync("my-app", code);
string answer = await link.RequestAsync("status");
```

Every later start needs no code — the pairing is stored:

```csharp
await using ILink link = await TailcatLink.JoinAsync("my-app");
```

After pairing both ends are equal: either can ask, answer or notify.

## Examples

### Ask, tell

```csharp
string answer = await link.RequestAsync("disk usage");   // ask and wait for the answer
await link.NotifyAsync("backup finished");               // tell, without waiting
byte[] reply = await link.RequestAsync(new byte[] { 1, 2, 3 });
```

### Send content of any size, with a description

There is no size limit — a kilobyte of JSON and a 20 GB video go the same way.
`LinkContent` can carry a name, a content type and metadata of your own, which
the other side reads before the content itself.

```csharp
// sending
byte[] about = JsonSerializer.SerializeToUtf8Bytes(new Recording("kitchen", DateTimeOffset.Now));

await using IncomingTransfer answer = await link.RequestAsync(
    LinkContent.FromFile(@"D:\recordings\kitchen.mp4") with
    {
        ContentType = "video/mp4",
        Metadata = about,
    });
Console.WriteLine(await answer.ReadAllTextAsync());

// receiving
link.OnRequest(async (request, ct) =>
{
    Recording? recording = JsonSerializer.Deserialize<Recording>(request.Metadata.Span);
    string folder = Path.Combine(inbox, recording!.Camera);
    Directory.CreateDirectory(folder);

    await request.SaveToAsync(Path.Combine(folder, request.SuggestedFileName), null, ct);
    return LinkContent.FromString($"saved {request.BytesReceived} bytes");
});
```

### Files

```csharp
// receiving: every file into one folder
link.SaveTransfersTo(inbox);

// sending, with progress
await link.SendFileAsync(@"D:\photos\holiday.zip",
    progress: new Progress<TransferProgress>(p => Console.Write($"\r{p.Fraction:P0}")));
```

If the connection drops midway, the transfer continues from where it stopped,
and the receiving handler runs once.

### Several machines paired with one host

```csharp
await using ILinkHost host = await TailcatLink.HostManyAsync("my-app", new LinkOptions { MaxPeers = 4 });

host.SetRequestHandler((peer, request, ct) =>
    Task.FromResult<ReadOnlyMemory<byte>>(Encoding.UTF8.GetBytes($"hello {peer.Name}")));
host.PeerJoined += (_, e) => Console.WriteLine($"{e.Peer.Name} connected");
host.PeerLeft += (_, e) => Console.WriteLine($"{e.Peer.Name} left: {e.Reason}");

// one code per device
LinkInvitation invitation = await host.InviteAsync(new InvitationRequest
{
    Label = "kitchen phone",
    Lifetime = TimeSpan.FromMinutes(2),
    SingleUse = true,
});
Draw(invitation.Code, invitation.ExpiresAt);

// talk to one of them, or unpair it
foreach (ILinkPeer peer in host.Peers)
{
    await peer.NotifyAsync("the host is restarting"u8.ToArray());
}
await host.ForgetPeerAsync(host.Peers[0]);
```

The device joining can say who it is (the host cannot verify it — it is only a
label):

```csharp
await using ILink link = await TailcatLink.JoinAsync("my-app", code, new JoinRequest { DisplayName = "kitchen phone" });
```

### Channels, for live data

For audio, telemetry or input events: ordered, fast, and deliberately *not*
resumed — a channel ends with the connection.

```csharp
host.OnChannel("audio", async (peer, channel, ct) =>
{
    await foreach (ReadOnlyMemory<byte> received in channel.ReadAllAsync(ct))
    {
        Play(received);
    }
});

await using ILinkChannelWriter audio = await link.OpenChannelAsync("audio");
await audio.SendAsync(frame);
```

### Typed requests (`Tailcat.Link.Json`)

```csharp
link.SetRequestHandler(AppJson.Default.StatusQuery, AppJson.Default.Status,
    (query, ct) => Task.FromResult(new Status(query.Service, IsRunning: true)));

Status status = await link.RequestAsync(new StatusQuery("backup"), AppJson.Default.StatusQuery, AppJson.Default.Status);

record StatusQuery(string Service);
record Status(string Service, bool IsRunning);

[JsonSerializable(typeof(StatusQuery))]
[JsonSerializable(typeof(Status))]
partial class AppJson : JsonSerializerContext;
```

### In a hosted app (`Tailcat.Link.Extensions.DependencyInjection`)

```csharp
HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddTailcatLinkHost("my-app");
builder.Services.AddHostedService<Worker>();
builder.Build().Run();

sealed class Worker(ILinkHost host, ILogger<Worker> log) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        host.SetRequestHandler((peer, request, ct) => Task.FromResult<ReadOnlyMemory<byte>>("ok"u8.ToArray()));
        host.PeerJoined += (_, e) => log.LogInformation("{Peer} connected", e.Peer.Name);
        return Task.CompletedTask;
    }
}
```

### State and errors

```csharp
link.StateChanged += (_, e) => Console.WriteLine($"link is {e.State}");
link.SessionEnded += (_, e) =>
{
    if (e.Reason == LinkDisconnectReason.Refused)
    {
        Console.WriteLine("the other machine forgot this one: pair again with a new code");
    }
};

try
{
    await link.RequestAsync("restart the service");
}
catch (RemoteHandlerException ex)
{
    Console.WriteLine($"the other machine's handler failed: {ex.Message}");
}
catch (LinkTimeoutException)
{
    Console.WriteLine("nothing moved for too long: the other machine is probably off");
}
```

Sending while the link is down is not an error — it waits for the next
connection. It gives up only when nothing has moved for a while.

### Options

```csharp
await using ILink link = await TailcatLink.HostAsync("my-app", new LinkOptions
{
    Store = new FileLinkStore(@"D:\my-app\state"),     // where the identity and pairing are kept
    LoggerFactory = loggerFactory,                      // what the link is doing, and why
    PairingWindow = TimeSpan.FromMinutes(15),           // how long a code can be used
    HeartbeatInterval = TimeSpan.FromSeconds(15),       // how often the other machine is checked
    RequestDeadline = TimeSpan.FromMinutes(1),          // how long a request may go with nothing moving
    TransferStallTimeout = TimeSpan.FromMinutes(2),     // the same for files and LinkContent
});

InvitationCode fresh = await link.RenewInvitationAsync(); // a new code, without restarting
```

### Testing without a network (`Tailcat.TestSupport`)

```csharp
await using FakeDerpRelay relay = new();
var gateways = new FakeRelayGatewayFactory(relay);
LinkOptions Offline() => new() { Gateway = gateways, Store = new InMemoryLinkStore() };

await using ILink host = await TailcatLink.HostAsync("test", Offline());
host.OnRequest(text => text.ToUpperInvariant());

await using ILink client = await TailcatLink.JoinAsync("test", host.InvitationCode.Value, Offline());
Assert.Equal("PING", await client.RequestAsync("ping"));
```

### From a browser

```js
import { TailcatLink, LinkContent } from "@tailcat/link";

const link = await TailcatLink.join({ appName: "my-app", invitationCode: code });
link.onRequest((text) => `the browser says: ${text}`);

const answer = await link.request("status");
await link.send(LinkContent.fromBytes(file, { name: "photo.jpg" }));
```

The .NET host is written exactly as above and does not care which kind of
machine joined. More in [clients/browser](clients/browser/README.md).

## Packages

| Package | What for |
| --- | --- |
| `Tailcat.Link` | everything above; the one you need |
| `Tailcat.Link.Json` | typed requests over `System.Text.Json` |
| `Tailcat.Link.Extensions.DependencyInjection` | the host with your app's lifetime |
| `Tailcat.TestSupport` | an in-memory relay for tests |

## Good to know

- **Requirements:** .NET 10. The direct path needs QUIC — Windows 11 / Server
  2022+, macOS, or Linux with `libmsquic`. Without it (e.g. Windows 10) the
  link still works, only over the relay.
- **Some networks cannot be punched through.** Then everything stays on the
  relay: it works, just slower.
- **Relays are Tailscale's public servers**, shared and rate-limited; they see
  that two keys talk, never what they say.
- **The security design has not been reviewed** by anyone outside this
  project.

## More

- [docs/internals.md](docs/internals.md) — how every layer works, and what was
  learned the hard way
- [docs/exchanges.md](docs/exchanges.md), [docs/channels.md](docs/channels.md),
  [docs/relay1.md](docs/relay1.md) — the wire formats
- Tests: `dotnet test`; `TAILCAT_LIVE_TESTS=1 dotnet test` also runs tests over
  the public relays; `npm --prefix clients/browser test` for the browser client.

## Licence

BSD-3-Clause — see [LICENSE](LICENSE). The parts ported from
[tailscale/tailcat](https://github.com/tailscale/tailcat) carry Tailscale's
copyright. This project is not affiliated with or endorsed by Tailscale Inc.

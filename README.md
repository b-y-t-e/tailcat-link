# tailcat-link

A .NET 10 port of the parts of [tailscale/tailcat](https://github.com/tailscale/tailcat)
that tailcat implements itself: the `ConnBlob` wire format, the meow
handshake framing, DERP map fetching and caching, the SOCKS5 address
classifier, the web demo handler, and connection proxying.

On top of that port sit two things Go's tailcat does not have: `Tailcat.Net`,
which does the *job* of Tailscale's data plane — meet at a relay, punch a
direct path, carry reliable streams — over QUIC rather than WireGuard, and
`Tailcat.Link`, which turns that into a pairing two machines keep for as long
as they are switched on. A browser can hold one too; see
[clients/browser](clients/browser/README.md).

Tests are xUnit v3 and were written first, ported case for case from the Go
tests.

## The short way in: `Tailcat.Link`

Everything below this section is the transport. If what you want is two
machines that stay in touch — a laptop that moves between Wi-Fi networks, and
a machine somewhere you cannot reach except by pairing it once — use
`Tailcat.Link` and skip the rest.

Install it:

```
dotnet add package Tailcat.Link
```

On the machine to be reached, once:

```csharp
await using ILink link = await TailcatLink.HostAsync("my-app");
Console.WriteLine(link.InvitationCode);      // show this once, as text or a barcode
link.OnRequest(command => Run(command));     // answer whatever the operator asks
```

On the machine doing the reaching, with that code the first time and never
again:

```csharp
await using ILink link = await TailcatLink.JoinAsync("my-app", code);
string answer = await link.RequestAsync("status");
```

Both ends are equal after pairing: either can ask, either can answer, and
either can push a message the other did not ask for. There is nothing to call
when the link drops.

What it adds on top of `Tailcat.Net`, and why each part is needed:

- **A pairing that survives a restart.** The identity key is generated once
  and stored — DPAPI-encrypted to the user account on Windows, mode 0600
  inside a 0700 directory on Unix, written through a temporary file so a
  power cut cannot leave half of one. Without it a restart means a new key,
  a new address, and a code that no longer points anywhere.
- **An address that never changes.** A host records the region it first
  measured and pins it from then on. A node's address is its key *and* its
  region, so a host that re-measured after moving would quietly retire the
  code already in circulation — with nobody there to publish the new one.
- **A supervision loop.** `TailcatConnection` is not resurrected once it
  dies; `DurableLink` builds the next one. Relay outage, network change,
  either machine rebooting, and a peer that has been away for a day all look
  the same from here — a session that stopped answering — and all get the
  same answer.
- **A host that cannot be reached repairs itself.** The hosting end spends
  its life waiting to be connected to, and waiting looks exactly like a relay
  socket that died without saying so — what a laptop resumed from sleep behind
  another NAT leaves behind. Nobody is standing next to that machine, so
  silence longer than `ListenSilenceTimeout` counts as a failure and the node
  is rebuilt from the stored identity, with the same key and region: the
  published code still points at it.
- **Failure detection that works.** Writing into a dead session succeeds:
  the bytes reach a relay with nobody to hand them to. So a heartbeat asks
  the peer every 15 seconds, and every exchange is given up on once nothing
  has moved for `RequestTimeout`; silence, not an error, is what a machine
  that has gone away looks like. It bounds silence rather than the exchange
  on purpose — a payload of several megabytes through a shared relay takes
  longer than any sane deadline, and each retry would resend it from the
  start and run out in the same place.
- **Trust on first use, with something to trust.** A code is an address and
  a random pairing token. The address cannot be kept secret — it is what the
  host hands to every relay it connects to, and the operator of a public
  relay sees it — so the token is what a host actually checks: the first
  machine to arrive with it is pinned as the peer, and everyone after it is
  refused. Candidates are heard side by side, up to a bounded number, because
  one at a time means anyone who knows the address can hold the host away from
  its peer by connecting and saying nothing. The token is good for an hour (`PairingWindow`); after that, or
  after pairing, the code buys nothing, and a host started again past its
  window shows a new one. A host that outlived its window while running is
  not stuck showing a code it would refuse either: `InvitationExpiresAt` says
  when the code stops working and `RenewInvitationAsync` mints the next one,
  so an application that can publish a code — printing it, drawing a barcode
  — does not have to be restarted to do it again.
- **A request that waits.** Sending while the link happens to be down is not
  an error: it waits for the next session, up to `RequestDeadline`. Only a
  handler that threw on the other machine comes back as a failure
  (`RemoteHandlerException`), because retrying that would only replace a
  clear error with a timeout.
- **A transfer that is not a request.** A request is a message and is capped
  at sixteen megabytes, because both machines hold all of one at once.
  `SendAsync` takes a stream instead — a 20 GB file is an ordinary use of it
  — and cuts it into blocks that neither machine holds more than a few of.
  What that buys is not size but resumption: a session that dies mid-file is
  answered by asking the other machine where it got to and carrying on from
  exactly there, into the same handler, which never learns that anything
  happened. The receiving end sets the pace, so sending to a slow disk costs
  memory on neither machine, and `SendAsync` returns only once the receiving
  handler has finished with the content.
- **A retry that is not a second command.** A request carries an id that
  belongs to the request, not to the attempt, so the machine that already ran
  it answers the retry from memory instead of running it again. Without that,
  a session dying between the handler finishing and its answer arriving would
  restart the service, write the file, or take the payment twice. The one
  case outside the promise is the other machine's process ending mid-request:
  nothing here can know how far its handler got.

Sending a file is the whole of it on either side:

```csharp
// receiving
link.OnTransfer(async (transfer, ct) =>
    await transfer.SaveToAsync(Path.Combine(inbox, transfer.SuggestedFileName), null, ct));

// sending
await link.SendFileAsync(path,
    progress: new Progress<TransferProgress>(p => Console.Write($"{p.Fraction:P0}")));
```

`tests/Tailcat.Link.Tests` runs all of it offline against the in-memory relay:
pairing, the code being spent and renewed, a stranger who has the host's
address but not its token, a relay that drops both machines, a host that
reboots, a host nobody reaches at all, both machines losing their network
stack outright, and a transfer larger than a request could ever be that is
interrupted mid-file and resumes into the handler already reading it.

## Layout

| Project | Go counterpart |
| --- | --- |
| `src/Tailcat` | the root `tailcat` package (`tailcat.go`, `wire.go`, `disco.go`) |
| `src/Tailcat.WebDemo` | `webdemo/` |
| `src/Tailcat.Cli` | `cmd/tailcat/` (the pure logic, so far) |
| `src/Tailcat.Derp` | `tailscale.com/derp` — a DERP relay client |
| `src/Tailcat.Net` | the job `magicsock` + `wgengine` + netstack do in Go |
| `src/Tailcat.Demo` | `tailcat-demo`, a CLI for verifying a link between two machines |
| `src/Tailcat.Link` | (new) pair two machines once, stay linked — see above |
| `tests/Tailcat.Tests` | `tailcat_test.go`, `wire_test.go` |
| `tests/Tailcat.WebDemo.Tests` | `webdemo/webdemo_test.go` |
| `tests/Tailcat.Cli.Tests` | `cmd/tailcat/socks_test.go` |
| `tests/Tailcat.Derp.Tests` | (new) framing, handshake, and routing tests |
| `tests/Tailcat.Net.Tests` | (new) STUN, sealed messages, paths, and live session tests |
| `tests/Tailcat.Link.Tests` | (new) pairing, stored identities, and self-healing links |
| `clients/browser` | (new) the same link, in a page — `web/` is the nearest Go relative |

## What was ported

| Go | .NET |
| --- | --- |
| `ConnInfo`, `ConnInfo.ConnBlob` | `ConnInfo`, `ConnInfo.ToConnBlob` |
| `ParseConnBlob`, `ParseConnBlobRaw` | `ConnBlob.Parse`, `ConnBlob.ParseRaw` |
| `ConnBlob.Resolve` | `ConnBlob.ResolveAsync` |
| `ConnInfo.Expand` | `ConnInfo.ExpandAsync` |
| `wireConnInfo`, `wireRegion`, `wireNode` | `WireConnInfo`, `WireRegion`, `WireNode` |
| `cbor:"n,omitempty"` struct tags | `[CborProperty("n", order, OmitEmpty = true)]` |
| `FetchDERPMap`, `DERPMapCache` | `DerpMapFetcher.FetchAsync`, `IDerpMapCache` |
| variadic `opts ...any` | `ExpandOptions` |
| `IsMeowPacket`, `EncodeMeowPing`, … | `Disco.IsMeowPacket`, `Disco.EncodeMeowPing`, … |
| `tcAddrForKey` | `TcAddr.ForKey` |
| `ProxyConns` | `Proxy.ConnsAsync` |
| `webdemo.Handler` | `WebDemo.MapWebDemo` |
| `classifySOCKSAddr`, `socksTarget` | `SocksAddr.ClassifyAsync`, `SocksTarget` |
| `normalizeListenAddrPort` | `ListenAddr.Normalize` |
| `key.NodePrivate`/`NodePublic`/`Disco*` | `Tailcat.Keys.*` (X25519 via BouncyCastle) |
| `discoPrivateForNode` | `DiscoPrivate.ForNode` |
| `tailcfg.DERPMap`/`DERPRegion`/`DERPNode` | `Tailcat.Tailcfg.*` |

The wire format is byte-compatible: `ConnBlobTests` asserts the same base64
blobs the Go test pins (`tcoWFwWC…`, `tcomFwWC…`), which means the CBOR field
names, field order, and omitempty behaviour all match fxamacker/cbor's
output for the Go structs.

Two deliberate API departures, both because the Go idiom has no C# equivalent:

- Go's variadic `opts ...any` with a type switch became the `ExpandOptions`
  record. Same options, checked at compile time.
- `PickBestRegion` (netcheck/STUN latency probing) is an interface,
  `IRegionPicker`, defaulting to `NoRegionPicker`, which reports "no
  measurement" so callers fall back to a random region exactly as Go does
  when netcheck fails. Implementing real probing needs a STUN client.

`MemDerpMapCache` takes a `TimeProvider` so cache-freshness tests don't
sleep; the default is the system clock.

## Connecting hosts across networks that can't see each other

`Tailcat.Derp` is a client of Tailscale's DERP relays. Both hosts connect
**outbound** to a relay on port 443, so neither needs an inbound port, a
firewall rule, or a NAT mapping — which is what makes two mutually invisible
subnets reachable to each other. The relay routes by Curve25519 public key:
send to a key, and whoever holds it receives it.

Verified against a live public relay (`tc301a.ipn.dev`): the login handshake
completes and packets flow between two clients.

Two things about the relay shape everything built on top:

- **It sees the traffic.** Whatever rides on DERP must bring its own
  end-to-end encryption.
- **It does not guarantee delivery.** Packets can be dropped, so reliability
  is the caller's job too.

### Why not `SslStream`

A DERP relay appends a self-signed **Ed25519 "meta certificate"** to its TLS
chain (subject `CN=derpkey<hex>`), which is how it publishes its DERP public
key. Windows' Schannel rejects that chain outright — the handshake dies with
`SEC_E_INVALID_TOKEN` before any validation callback runs — so `SslStream`
cannot talk to a DERP relay at all on Windows. `DerpTlsConnector` therefore
uses BouncyCastle's TLS stack and validates the certificate itself: the meta
cert is set aside, and the rest of the chain is verified against the OS trust
store with the host name checked, as a browser would. The key from the meta
cert is then cross-checked against the key the relay greets us with, so a
rewritten connection is caught.

## Connecting two hosts: how it actually works

```
TailcatNode.ConnectAsync(peerKey)
        │
        ├─ 1. meet at the relay        both sides are already connected outbound
        ├─ 2. sealed Hello / HelloAck  exchange TLS fingerprints + candidate addresses
        ├─ 3. QUIC handshake           over the relay path, pinned to that fingerprint
        └─ 4. hole punch               probe candidates; move onto a direct path if one answers
```

**Verified live** against a public relay: two nodes that know only each
other's public keys open a session, exchange a stream, and end up on a direct
path. `dotnet test` runs those end-to-end tests when `TAILCAT_LIVE_TESTS=1` is set
(they need internet access and use the public rate-limited relays); otherwise
they skip. Their timeouts are generous on purpose: a shared relay under load
varies a lot, and the tests are there to show a session forms, not to measure
this machine.

```csharp
await using TailcatNode node = await TailcatNode.CreateAsync();
Console.WriteLine(node.Address);   // hand this to the other side

// One side listens:
await using TailcatConnection conn = await node.AcceptConnectionAsync();
await using Stream stream = await conn.AcceptStreamAsync();

// The other dials that address:
await using TailcatConnection conn = await node.ConnectAsync(address);
await using Stream stream = await conn.OpenStreamAsync();
Console.WriteLine(conn.CurrentPath);   // "relay (mtu 65024)" or "direct 203.0.113.9:51820 (23 ms, mtu 1400)"
```

### Why an address, not just a key

A node listens in the relay region closest to itself. Two nodes far apart
therefore pick *different* regions — and a node that only knew a peer public
key would send into its own region, where nobody is listening. The handshake
would simply time out, with everything appearing correct.

So a node address is its public key **and** its home region, in the same
compact `tc…` form the Go implementation uses:

```
tco2FwWCDRh1raQ5-4WVvjPImw4tL9B4-mZ-oadOTh4vSXitOjeWFrWCCcfd76PYE76USuQsZKyf8eCUzR5fEr-M07C5VfhMsRBGFpAQ
```

The address also carries the node's *disco* public key, derived from its node
key by an HMAC (`DiscoPrivate.ForNode`) so that only the node key is worth
persisting. The two are deliberately unlinkable: a disco key is shown in the
clear on a direct path, while the node key is the unguessable part of the
address. Tailcat originally reused the node key's bytes as its disco key and
[fixed that](https://github.com/tailscale/tailcat/commit/cb1e0d753) after
release; this port follows, and — unlike Go, whose whole data plane is
disco-based — still accepts an address written before the split, because it
meets peers over QUIC and never consults a peer's disco key.

To reach a peer, a node opens a connection into *that peer's* region and
sends there; the handshake carries each side's home region so the answer goes
back to the right place. Connections to other regions are pooled and the
least recently used is dropped past a limit — the home connection never is.
This is the arrangement Tailscale uses, and `MultiRegionTests` pins it down
by connecting two nodes deliberately placed in different regions.

### Why it needs no inbound port

Both sides dial *out* to the relay on 443, so neither network has to accept an
inbound connection — that is what makes two mutually invisible subnets
reachable. The direct path is then punched open by probing: each probe opens a
NAT mapping on its way out, so the peer's probes arriving from the other side
find a way in. If the NATs are hostile enough that no direct path exists, the
session stays on the relay and keeps working.

### What secures it

- **The relay is untrusted.** It routes by public key but holds neither
  private key. Every control message is sealed in a NaCl box between the two
  node keys, so a relay can neither read one nor forge one.
- **No certificate authority.** Each node's QUIC certificate is generated
  fresh and is meaningless by itself; what makes it trustworthy is that its
  fingerprint arrives inside a sealed message. Both sides pin that exact
  fingerprint, in both directions.
- **The traffic itself is QUIC**, so encryption, reliability, and stream
  multiplexing come from a TLS 1.3 stack rather than anything hand-rolled.

### Seeing what it did

A session that ends up relayed instead of direct is not an error, and it
looks identical from the outside to one that never tried — so a node reports
its steps to an `ITailcatObserver`:

```csharp
await TailcatNode.CreateAsync(new TailcatNodeOptions
{
    Observer = new TextTailcatObserver(Console.Error.WriteLine),
});
```

```
relay: connected to region 301
endpoints: 192.168.0.146:58061, [2a02:a312:44bf:9100::de82]:58061, ...
session: handshaking with f24953dc0b78 in region 301
path: f24953dc0b78 now on direct [2a02:a312:44bf:9100:...]:61765 (2 ms, mtu 1200)
session: f24953dc0b78 up in 3376 ms
```

Counters are published on the `Tailcat.Net` meter for anything collecting
`System.Diagnostics.Metrics`.

`TailcatNodeOptions.TimeProvider` sets the clock the node measures with, so
endpoint freshness can be tested without waiting.

### Keeping it up

- **The relay reconnects.** A DERP connection is one long-lived TCP
  connection, and those end. `DerpConnection` re-dials with backoff, keeping
  the node key, so peers can still find the node afterwards. Callers read
  from a channel that outlives any single connection.
- **The closest region is measured, not guessed.** `StunRegionPicker` times a
  STUN round trip to every region and takes the lowest — the port of Go's
  `PickBestRegion`. Every relayed byte crosses the relay twice, so the choice
  matters. If nothing answers, the node falls back to a region rather than
  failing.
- **Address changes are announced.** Moving between networks invalidates
  every address a peer knows. The node watches for it, re-runs discovery, and
  sends each live session an `EndpointUpdate` — sealed like every other
  control message, so it cannot be forged to redirect a session.
- **IPv4 and IPv6 direct paths.** The UDP socket is dual-stack and routable
  IPv6 addresses are offered as candidates, which are often the easiest
  direct path of all: no NAT to punch through. Addresses are normalized to
  one canonical form, or an IPv4 peer seen through a dual-stack socket would
  appear as two separate paths and neither would gather enough evidence to
  win.
- **MTU is discovered per path.** Paths start at QUIC's 1200-byte floor;
  once a direct path answers, a padded probe tests 1400. A datagram too large
  for the direct path goes over the relay (which carries far more) instead of
  being dropped, since losing only the large packets looks like a mysterious
  stall rather than a lost packet.
- **The chosen path is sticky.** A rival must be faster by 10 ms *and* by a
  fifth before traffic moves to it. Two paths to the same peer often measure
  within noise of each other — a LAN address and a tunnel address, say — and
  without a margin the link alternates between them on every probe, churning
  NAT mappings and burying real path changes in noise.
- **Candidates expire.** A peer that moves networks announces a fresh set of
  addresses each time, and an address nobody has heard from in a minute is
  forgotten rather than probed for the life of the session.
- **Leaving a path is reported too.** A path is abandoned by falling silent,
  not by announcing it, so the move back to the relay produces no answer to
  react to. The probe loop reports it, which is the case an observer most
  needs: traffic quietly back on the relay is exactly what "why is this
  suddenly slow?" looks like.

### Two implementation notes worth knowing

**QUIC over a custom transport.** The platform QUIC stack will only speak to a
UDP socket, so `UdpBridge` gives it one: a loopback socket standing in for the
peer. QUIC therefore sees a single stable peer address while the link
underneath moves between the relay and a direct path — which is why the switch
doesn't interrupt a stream. It's the trick magicsock plays on WireGuard.

**A received datagram is only borrowed.** The receive loop reads every packet
into one buffer and reuses it, so anything that outlives the call — notably
`UdpBridge`, whose send to the local QUIC endpoint is fire-and-forget — must
copy first. Without the copy the next packet overwrites the bytes mid-send,
which surfaces only once a direct path is in use and only under load, and
reads as random loss rather than corruption. `PeerLink.DatagramReceived`
states the contract; `UdpBridgeTests` holds it to it.

**A TLS write is best effort.** `DerpTlsStream.WriteAsync` encrypts and
queues; the pump sends afterwards, so a connection dying at that moment
surfaces on the reading side, not from the write. That suits a relay, whose
delivery was never guaranteed, but it means a successful write is not a
delivered write.

**BouncyCastle TLS must run non-blocking.** In stream mode it holds its lock
for the whole of a blocking read, so a parked receive loop blocks every send
behind it — measured here as a 63-second stall on a DERP connection, which is
exactly the shape a node's relay loop has. `DerpTlsStream` uses non-blocking
mode instead: the protocol object only transforms buffers under a short lock,
never while waiting on the network, and a single pump writes outbound records
so TLS never sees them reordered.

### Verifying it between two real machines

`tailcat-demo` exists to check the direct path across actual networks, which
is the one thing a single machine cannot prove.

```
# machine A
$ tailcat-demo listen
measuring relay regions and connecting ...
  [relay: connected to region 301]
node up in 5486 ms
  region:    301
  reachable: 192.168.0.146:65430, [2a02:a312:44bf:9100::de82]:65430

Run this on the other machine:

    tailcat-demo connect tcomFwWCDySVPcC3gLpPLOP5n40rrAJluCcfm1A0a9RvWb0af-DmFpGQEt

# machine B
$ tailcat-demo ping tcomFwWCDySVPcC3gLpPLOP5n40rrAJluCcfm1A0a9RvWb0af-DmFpGQEt
session up in 1476 ms over relay (mtu 65024)
#1     126.6 ms  relay (mtu 65024)
path changed -> direct 100.84.18.10:55862 (2 ms, mtu 1400)
#2       2.2 ms  direct 100.84.18.10:55862 (2 ms, mtu 1400)
```

That trace is the whole design in six lines: the session forms over the
relay, a direct path is punched open, traffic moves to it, and MTU discovery
then raises the packet size. Both sides only ever dial out.

The run above was two processes on one machine, so its direct path was a
certainty. The interesting case is two machines on different networks, and
that has now been measured: between a home connection and an LTE carrier
NAT, `77.236.30.248` to `83.238.131.70`, the session came up over a Frankfurt
relay at 69 ms and moved onto a direct path at 30 ms, with MTU discovery then
raising packets from 1200 to 1400 bytes.

Getting there took four fixes, each of which had made the outcome impossible
rather than unlikely, and each of which was invisible because a relayed
session works — it is only slow:

- **No node ever learned its public address.** The STUN servers a node asks
  default to the ones named in the DERP map, and none of the four relays in
  tailcat's map answer on 3478. So `PeerHello` advertised nothing but LAN
  addresses and a peer on another network had nothing to aim at.
  `StunFallbackHosts` now supplies public servers when the map's own answer
  nothing, and all of them are asked at once rather than one after another.
- **Every node in the world chose New York.** Same cause: with no STUN answer
  `StunRegionPicker` measured nothing, and the caller then fell back to the
  lowest-numbered region. Ranking now falls back to timing a TCP handshake to
  each relay, which is a worse clock but reaches a port the relay is certain
  to be listening on.
- **Punching was attempted for five seconds, once, ever.** After that window
  a dead candidate was skipped and after a minute forgotten, and nothing
  reopened it — so a pair whose first burst overlapped the tail of the relay
  handshake stayed relayed for the life of the session. It is now retried
  every 30 seconds for as long as nothing direct is working.
- **On Windows, one bounced probe broke the next receive.** Windows reports
  the ICMP rejection of a datagram we sent as a connection reset on the
  socket's *next* receive; for a socket whose job is probing addresses that
  may be dead, every probe to a candidate that had gone away aborted the
  receive a peer's answer was about to arrive on. `SIO_UDP_CONNRESET` is now
  turned off, and a candidate that fails to send no longer costs the rest of
  the sweep their turn.

The trace that found them is still there: `ITailcatObserver.DirectProbeSent`
and `DatagramArrived` report which candidates were probed and what arrived
from where. Without them the first three attempts at this were guesswork —
a failed punch otherwise looks exactly like a peer that is switched off.

What is still true: between two sufficiently hostile NATs there is no direct
path, and then the `path changed` line simply never appears and the session
keeps running on the relay. Note also that the address a peer advertises need
not be the one its packets arrive from — the carrier NAT above preserved the
port toward STUN servers and used a different one toward its peer, so the
punch succeeded on the address learned from an arriving probe rather than on
the one that was announced.

## When one end cannot have QUIC

A browser has no UDP socket. It cannot originate a QUIC connection to a peer
and cannot present a client certificate, so it cannot be one end of the
session described above. It can open a WebSocket, and that is enough to reach
a DERP relay.

So there is a second transport, `relay1`, for ends like that: the relay
carries the streams itself, encrypted end to end, and the session never
leaves it. A pair that can do QUIC still does — the transports are offered in
preference order inside the sealed hello, and QUIC is first because it
recovers from a lost packet and can punch its way onto a direct path.
`relay1` is the floor, not a replacement.

Writing it turned up a second audience nobody had asked about. **Windows 10
has no QUIC at all** — `QuicListener.IsSupported` is false, and until this
existed a node there refused to start. It now offers `relay1`, connects, and
works; measured between a Windows 11 machine and a Windows 10 one, the two
agreed on `relay1` with neither configured for it, and the session came up in
107 ms against QUIC's 3.3 seconds. There is no QUIC handshake to do.

The protocol is specified byte for byte in [docs/relay1.md](docs/relay1.md),
which is what let the JavaScript client be written against the document
rather than against the code. It landed on the formats first time — and then
immediately found a bug in them. Records had been sized against DERP's 64 KiB
packet limit, but a relay reached over a WebSocket closes a client that sends
more than 32 KiB: `code 1009, read limited at 32769 bytes`. Small requests
were fine. A 300 kB one was not. A second implementation is how you find
that before a user does.

```js
const link = await TailcatLink.join({ appName: "my-app", invitationCode: code });
link.onRequest((text) => `the browser says: ${text}`);
const answer = await link.request("status");
```

The machine being reached is written exactly as it always was —
`TailcatLink.HostAsync`, `OnRequest` — and never learns that a browser
arrived. See [clients/browser](clients/browser/README.md).

### Not done yet

- **Rekeying.** A session TLS certificate lives as long as the process.
- **QUIC session resumption.** The relay reconnects and paths fail over, but
  a QUIC connection that dies must be re-established by the caller.
- **Throughput measurement.** Nothing here says what the relay path or a
  direct path actually sustains.
- **A security review.** The primitives are libsodium and TLS 1.3, but the
  authentication design — pinning a certificate fingerprint announced inside
  a sealed box — has not been reviewed by anyone else.

## What was not ported, and why

tailcat is a thin layer over Tailscale's whole data plane. These parts have
no .NET equivalent to build on:

- `Server`, `Client`, `locoBackend` — magicsock, wgengine, netstack/gVisor,
  disco, and the WireGuard packet filter. `Tailcat.Net` does the same *job* —
  meet at a relay, punch a direct path, carry reliable streams — but over
  QUIC rather than WireGuard, so it does not interoperate with the Go
  implementation.
- `tailcat_ssh.go`, `cmd/tailcat/ssh.go` — the auth-free SSH server on the
  tunnel.
- `web/`, `internal/wasmbuild`, `cmd/tailcat-webdist` — the Go js/wasm build
  and its toolchain. `Tailcat.WebDemo` serves a dist directory but does not
  produce one. The browser reaches the network a different way here: not Go
  compiled to wasm, but a JavaScript client speaking the same protocol —
  `clients/browser`, below.
- `PickBestRegion` — see `IRegionPicker` above. `Tailcat.Derp` does not yet
  do STUN latency probing either.

The tests that exercise those layers therefore have no counterpart:
`TestTailcat`, `TestHalfClose`, `TestSSHSuite`, `TestPipeMode`,
`TestBrowserReceives`, and `TestBrowserSends`. `ProxyTests` covers the
half-close behaviour of `TestHalfClose` directly over loopback TCP, which is
the part that doesn't need the tunnel.

## Running the tests

```
dotnet test                      # unit tests only
TAILCAT_LIVE_TESTS=1 dotnet test # also the end-to-end tests over a public relay

npm --prefix clients/browser ci
npm --prefix clients/browser test   # the browser client, offline
```

CI runs both on every push (`.github/workflows/ci.yml`): the .NET tests on
Linux, macOS and Windows, and the browser client's on Node. The live tests
stay opt-in there, so the build depends on no public service and adds no load
to it.

The two implementations of `relay1` are held to one file of record vectors —
`clients/browser/test/vectors/relay1-records.json`, read by `Relay1VectorTests`
here and by the client's own tests. That is what catches a wire format
drifting apart without a relay in the loop. Checking them against each other
for real needs both ends:

```
dotnet run --project src/Tailcat.Demo -- host --forget   # prints a code
npm --prefix clients/browser run interop -- <code>
```

## What is published

One package, `Tailcat.Link`. The layers below it — `Tailcat`, `Tailcat.Derp`
and `Tailcat.Net` — are named after someone else's project and exist to serve
it, so their assemblies ship *inside* that package rather than beside it on
nuget.org. `Tailcat.Cli`, `Tailcat.WebDemo` and `tailcat-demo` are here as
source and are not published at all.

That is a deliberate trade: a consumer of `Tailcat.Link` gets everything with
one reference, and nobody else's project name gets claimed on a public feed.
Anyone who wants the transport on its own can reference `src/Tailcat.Net`
from a checkout.

`clients/browser` is not published either. It carries a `package.json` and is
importable from a checkout; putting it on npm is a separate decision.

## Licence

BSD-3-Clause — see [LICENSE](LICENSE).

The parts ported from [tailscale/tailcat](https://github.com/tailscale/tailcat)
(`src/Tailcat`, `src/Tailcat.Cli`, `src/Tailcat.WebDemo` and their tests) carry
Tailscale's copyright alongside this repository's; everything else is original
work released under the same licence. Each file says which it is in its header.

This project is not affiliated with or endorsed by Tailscale Inc.

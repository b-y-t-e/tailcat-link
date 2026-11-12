# The `relay1` transport

A second way to carry a tailcat session's streams, for ends that cannot open
a UDP socket — which in practice means a web browser.

Status: **implemented twice**. In .NET, `src/Tailcat.Net/Relay1`, covered by
`Relay1SessionTests` plus one test that runs the whole of `Tailcat.Link` over
it; and in JavaScript, `clients/browser`, which is the browser client this was
designed for. The two have been run against each other over a live relay.

## Why

There is a second audience beyond browsers, found while testing: **Windows 10
has no QUIC at all**. Whatever `relay1` was built for, it is also what those
machines now get instead of a node that refuses to start.

`Tailcat.Net` carries every session over QUIC, including the ones that never
leave the relay: `UdpBridge` hands the platform QUIC stack a loopback UDP
socket and forwards its datagrams over `PeerLink`. A browser has no UDP
socket, cannot originate a QUIC connection to a peer, and cannot present a
client certificate — so it cannot be one end of that. It can open a
WebSocket, which is enough to reach a DERP relay, and that is the whole of
what `relay1` assumes.

The goal is that the machine being reached does not care.
`TailcatLink.HostAsync`, `ILink`, the invitation code, `LinkFrame`,
`PairingHandshake` and `ExchangeLedger` are all unchanged and shared; the
choice of transport is made below them, per session, and never surfaces.

## What it is not

- Not a direct path of its own. A `relay1` session starts on the relay and,
  between two .NET machines or with one, stays there for its whole life.
  Between two JavaScript ends it may move its records to a WebRTC data
  channel once it is up — *A direct path*, below — which is WebRTC finding
  the path, not this transport.
- Not a replacement for QUIC. Two nodes that can both speak QUIC always do —
  see *Cost*, below.
- Not lossless. It inherits exactly what a DERP relay gives and no more.

## Negotiation

The transport is chosen in the sealed hello, so a relay cannot influence it.

`PeerHello` ends with a count byte and that many transport bytes
(`PeerTransport`). An empty list — which is what a node built before the
negotiation existed sends — reads as `[Quic]`, the only transport such a node
has. `relay1` is **`1`**.

- The dialling node lists **every** transport it can speak, best first.
  Sending the whole set rather than one wish is what lets a pair settle on
  the best they share in a single round trip.
- The answering node picks the first entry it also has and names that one,
  alone, in its `HelloAck`. Sharing none, it answers with its own set instead
  and creates no session; the dialler then fails with a message naming both
  sides' sets rather than waiting out its handshake timeout.

What a node offers follows from the platform: `Quic` when
`QuicListener.IsSupported`, then `relay1`, which always works.
`TailcatNodeOptions.Transports` overrides that — how a test exercises the
relayed path, and how an operator holds a node to it.

When the agreed transport is `Relay1`, the hello carries **32 more bytes**
after the transport list: the sender's ephemeral X25519 public key (below). The
`CertificateFingerprint` field has no meaning without TLS; it is sent as 32
zero bytes and ignored.

## Keys

The two nodes already authenticate each other with their static keys: every
hello is a NaCl box between them, which is what makes the relay untrusted.
That authentication is reused, and an ephemeral exchange is layered on it so
that a static key stolen tomorrow does not open a session recorded today.

```
dialler                                   host
e_d = X25519 keypair
Hello{..., transport=1, e_d.pub}  ---->   (sealed to host's static key)
                                          e_h = X25519 keypair
                                 <----    HelloAck{..., transport=1, e_h.pub}
                                          (sealed to dialler's static key)

ikm    = X25519(e_d.priv, e_h.pub)        == X25519(e_h.priv, e_d.pub)
salt   = sessionId (8B, BE) || dialler_static_pub (32B) || host_static_pub (32B)
prk    = HKDF-Extract(SHA-256, salt, ikm)
k_d2h  = HKDF-Expand(prk, "tailcat relay1 v1 d2h", 32)
k_h2d  = HKDF-Expand(prk, "tailcat relay1 v1 h2d", 32)
```

Both ephemeral private keys are destroyed when the session ends.

Notes on the choices, each of which is a place to push back during review:

- **The boxes are the authentication.** An ephemeral key only counts because
  it arrived inside a box that only the peer's static key could have sealed.
  Anything that weakens the box weakens this.
- **The static-static DH is not mixed in.** It is the same value the box
  already used; mixing it would tie the session key to a secret that lives
  forever, which is the property being removed.
- **AES-256-GCM, not ChaCha20-Poly1305.** Browsers expose AES-GCM through
  WebCrypto and ChaCha20-Poly1305 not at all, so ChaCha would mean shipping a
  cipher in WASM and running every transferred byte through it. .NET has
  `AesGcm` natively. The JS side is then left needing only X25519 and HKDF
  (both WebCrypto) plus a small XSalsa20-Poly1305 for the handshake boxes.
- **This is Noise IK by hand.** It has the shape of a reviewed protocol
  without being one. `Tailcat.Link`'s README already says the authentication
  design has had no outside review; this enlarges what that sentence covers.

## Records

A `relay1` record travels as its own peer message type, not as
`PeerMessageType.Data`: `Data` means "an opaque datagram that is already
encrypted", which `PeerLink` forwards into the UDP bridge. A distinct type
keeps that dispatch honest.

```
PeerMessageType.Relay1Record = 0x07

"TCN1" | 0x07 | counter (8B, BE) | AES-256-GCM ciphertext + tag
```

- **Key**: `k_d2h` for records the dialler sends, `k_h2d` for the host's. One
  key per direction, so the two sides can never collide on a nonce.
- **Nonce**: `0x00000000 || counter`, 12 bytes. The counter starts at 0 and
  increments by one per record sent. Reaching `2^64 - 1` ends the session; it
  is not reachable in practice and must not silently wrap.
- **Associated data**: none.
- **The counter must arrive strictly in sequence.** A gap means the relay
  dropped a record, and there is no way to recover the stream it belonged to,
  so the session is closed. See *Cost*. The one exception is an end that is
  setting a direct path up: there a record ahead of its turn is held until
  the ones before it arrive, within bounds — see *A direct path*.
- **A record that does not open is ignored**, not treated as the end of the
  session. The usual one belongs to the session before: a sender whose relay
  connection died resends what it sent just before, and when that session has
  since been replaced, its records reach the new one under keys it does not
  have. The tag still keeps a forged record out; a genuine one corrupted in
  flight leaves a gap that the next record shows. They are still reported
  (`ITailcatObserver.Relay1RecordsIgnored`, as a count at most every ten
  seconds because a cut can bring thousands; `onignored` in `relay1.js`): a
  steady stream of them is two ends whose keys disagree, not a silent peer.
- **A counter, once sealed, is sent.** A sender may give up on a record only
  before it takes a counter for it. One cancelled after that is a gap the
  receiver closes the session on, with nothing dropped by the relay.
- **A counter already seen is ignored**, not treated as a gap. A sender whose
  relay connection dies resends what it sent shortly before the connection's
  last sign of life, on the new connection and ahead of anything new, because
  it cannot know which of those records arrived. Records sent while no
  connection is up are held and go out with them. Ignoring the copies is what
  lets that repair a cut instead of ending the session. The sender holds a
  bounded amount (16 MiB in the .NET implementation); past it the oldest are
  dropped, which the receiver sees as a gap.
- **Size**: at most 32256 bytes of plaintext. Not sized against DERP's 64 KiB
  packet limit, which is the obvious mistake and was made here first: a relay
  reached over a WebSocket closes a client that sends a message larger than
  32768 bytes — `code 1009, read limited at 32769 bytes` — and a WebSocket is
  the only way a browser reaches one. The limit is the same on both
  transports because either end may be a browser. The remainder covers the
  DERP frame header, the destination key, this record's header and counter,
  and the tag.

## Streams

QUIC's multiplexing goes away with QUIC, so the record payload carries a
minimal one. Exactly one frame per record — no second length field, because
the record already ends where the frame does.

```
stream_id: uvarint | flags: u8 | payload: the rest of the record
```

`flags`:

| bit | name     | meaning                                              |
|-----|----------|------------------------------------------------------|
| 0   | `FIN`    | no more data on this stream from this sender         |
| 1   | `RESET`  | the sender abandoned the stream; payload is a reason |
| 2   | `WINDOW` | payload is a `u32` of additional credit, not data    |

A stream is opened implicitly by the first frame naming it. Ids opened by the
dialler are odd, by the host even, starting at 1 and 2 — so neither side has
to ask before opening one, and they cannot collide.

Flow control is per stream and credit-based: 256 KiB initially, and a
receiver sends `WINDOW` as it consumes. It is not decoration — a message
frame has no size limit, and without credit a sender would push all of one at a
relay which drops what it cannot deliver, ending the session (above). A sender
with no credit stops; it does not buffer past the window.

An end that lets go of a stream before the peer's `FIN` sends `RESET` after
its own `FIN`, as QUIC does for a stream disposed while the peer still writes:
a writer waiting on credit that will never come would otherwise wait for the
session to end. A `RESET` after a `FIN` fails only the receiver's writes;
everything before the `FIN` is still read.

## Lifetime

A `relay1` session lives and dies with the relay connection under it. There
is no migration and no resumption: a relay that goes away, a network change,
and a peer that rebooted all end the session.

This is deliberate, and it is what keeps the transport small. `DurableLink`
already treats a dead session as the normal case — it reconnects, and
`ExchangeLedger` makes sure a request that was already answered is answered
from memory rather than run twice. So `relay1` needs no retransmission, no
sequence recovery and no session resumption of its own; it needs only to fail
loudly and quickly.

## A direct path

Two JavaScript ends can take a session off the relay. A page has no UDP
socket, so it cannot do what `PeerLink` does; what it has is WebRTC, which
finds a path with ICE and hands back a reliable, ordered data channel. That
channel is used as **a second way of carrying the same records**, not as a
transport of its own: the hello, the keys, the counter, the frames and
everything above them are unchanged. `direct-path.js` is all of it;
`clients/browser` offers and `clients/browser-host` answers. The .NET side
has no half yet, and ignores the offer.

It is on unless either end turns it off (`directPath: false`), and a session that
never gets one behaves exactly as it did before there was such a thing.

### The control stream

Stream id 0 is no stream: neither end can open it, and both implementations
have always dropped a frame that names it. A frame on it now carries what one
end says to the other about the session itself:

```
frame on stream 0, no flags:   kind (1B) | text (UTF-8)

0  Decline     nothing follows: "this end takes no direct path"
1  Offer       the dialler's session description (SDP)
2  Answer      the host's
3  Candidate   one ICE candidate, the JSON of an RTCIceCandidateInit
4  Received    8 bytes, big-endian: the counter this end expects next
5  Lost        nothing follows: "my end of the path is gone, I am on the relay"
```

An unknown kind is ignored. Being frames in records, these are sealed under
the session's keys, arrive in order, and can come from nobody but the other
end of the session — which is the whole authentication of the channel: its
DTLS fingerprint is in the SDP, so only the holder of the session's keys can
be at the far end of it.

### Who may ask, and when

- **Only the dialler offers**, once per session, and **only after the host
  has answered the pairing handshake.** The host **answers nothing on the
  control stream until that handshake has let the machine in**
  (`HostedConnection.trust`). An offer and its answer name the machines'
  addresses on their own networks, and a session can be opened by anyone who
  knows a host's address. This is why the gate is the link's and not the
  transport's: `relay1` does not know what "paired" means.
- A host that knows the offer and will not take it says `Decline`; one that
  does not know it says nothing, and the dialler gives up after 15 s. Either
  way the session carries on by the relay.
- Two machines on different networks need a STUN server to find each
  other, and it learns both machines' public addresses. With no `iceServers`
  given, Google's (`stun.l.google.com:19302`) is asked; `iceServers: []`
  tries only the machines' own addresses and asks nobody. A STUN server that
  does not answer costs the direct path, not the session.

### Moving, and what it costs

Each end moves its *sending* to the channel when the channel opens there, and
back to the relay only when the path gives out. Two consequences, and they
are the whole of what the record layer had to learn:

- **Records overtake each other once.** What an end sent by the relay just
  before it moved is still crossing — the relay is the slow path — when its
  first record on the channel arrives. So an end that has begun setting a
  path up (`expectTwoPaths`) holds a record that arrives ahead of its turn
  instead of ending the session on it: at most 4 MiB of them, and for at most
  10 s from the moment a gap opens, not from the newest record behind it.
  Past either bound the missing record is taken for lost and the session
  ends, as it always did. Held only after it has opened, so nothing forged
  takes up the room. A record seen before is still ignored, whichever path
  brought it. An attempt that comes to nothing takes the allowance back.
- **A path that dies sends the session back to the relay.** What the channel
  accepted and did not deliver cannot be known from the channel, so each end
  says how far it has read what came by it: `Received`, at most 200 ms after
  records arrive there (and after 32 acknowledgements alone, so the two ends
  do not answer each other's for ever). An end keeps every record it sent on
  the channel until it is covered, up to 8 MiB. Once ICE has connected, a
  channel that closes, fails, or stops answering for 3 s makes an end send
  by the relay again, starting with what it kept — the far end skips what it
  already has by counter — and say `Lost` there, because a path can die one
  way, or before anything crosses it, and a real ICE took more than twenty
  seconds to notice at the other end, which sent into the dead channel all
  that time. Measured on real stacks (Edge on Windows 10 against the
  JavaScript host under Node, UDP blocked for 12 s): back on the relay about
  10 s after the cut at both ends, no request failed, and direct again 5 s
  later. The session ends instead, as it used to, when the far end has
  never said `Received` (an end from before it, or a path that died within
  its first 200 ms), when what it has not read was let go of past the 8 MiB,
  or when the relay connection is gone too. Before ICE has connected neither
  end has sent anything on the channel, and an attempt that fails then costs
  the session nothing.

**An end that closes on purpose says so first.** It sends the text message
`leaving` on the channel — the only text the channel carries; everything else
is records — closes the channel, and closes the peer connection a quarter of
a second later. The other end then reports the session as ended by the other
end, not as a path lost, and does not count it against the path. Telling the
two apart by ICE's state was right on the test double and wrong on a real
stack, where the far end closing its peer connection takes ICE down before the
channel says anything; and the channel is closed at once, not with the peer
connection, because a page closing its link on its way out may not live for
the delay. A machine that vanishes says nothing, and is noticed when ICE or
the heartbeat gives up on it.

So that a path which forms and dies does not take every session in turn, the
dialler waits before offering again: 5 s, doubling to 5 minutes, and back to
none after a path that carried a session for a minute
(`DirectPathBackoff`). It outlives sessions, which is the point. Two details
that were each wrong at first:

- **The wait is sat out inside a session**, and the offer made when it ends.
  The session that follows a lost path is up within half a second, well
  inside the wait; asked only as a session starts, it stayed relayed for as
  long as it lived.
- **Only a path that gave out is counted.** A session that ended for reasons
  of its own — its relay connection was cut, the link was closed — says
  nothing about the path. A dialler cannot tell a host that closed the
  session from a path that died, though: both close the channel, and both
  count.

**A session that has gone direct outlives its relay connection.** Its records
no longer cross the relay, and ending it with the relay ended working
sessions every time a relay connection was cut. A session that has not gone
direct has nothing else, and ends with its relay as it always did. A host
keeps such a session listed, so a machine that dials again still replaces it.
If the path goes later, there is no relay to fall back to: the session ends
then and the link dials again, over a new relay connection.

## Cost

Being honest about what is given up against QUIC:

- **A dropped record kills the session.** DERP drops packets for a receiver
  that has fallen behind, and there is no retransmission here to cover it.
  QUIC rode over the same relay but recovered on its own. In practice this
  turns into a reconnect and a retried request, which is survivable, but it
  makes `relay1` measurably more brittle under load.
- **Head-of-line blocking.** Every stream shares one relay connection.
- **No direct path of its own.** Every byte crosses the relay twice, unless
  both ends are JavaScript and have asked for WebRTC to carry the records.

That is the argument for keeping QUIC as the default and `relay1` as what a
browser asks for: nothing that can avoid these should pay for them.

## The browser side

What a pure-JS client has to implement, in the order it needs them. This is
`clients/browser`, and each item names the module it landed in:

1. **The DERP map**, fetched with `fetch` (`derp.js`).
   `https://controlplane.tailscale.com/derpmap/default` answers
   `Access-Control-Allow-Origin: *`, so an SPA may read it directly; serving
   a copy from the app's own origin, as the upstream js/wasm demo does
   (`web/app.js` in `tailscale/tailcat`), remains the option that does not
   depend on that header staying.
2. **The relay connection** (`derp.js`), `wss://<hostname>/derp` with the `derp`
   subprotocol, then DERP's own login: the server's key from its greeting,
   the client info sealed to it, and the frame loop from `DerpProtocol.cs`.
   No region measuring: a dialler publishes no address, so it connects
   straight to the region named in the invitation code.
3. **The peer handshake** (`peer.js`, `nacl.js`): `PeerMessage` framing, the NaCl box
   (X25519 + HSalsa20 + XSalsa20-Poly1305 — `tweetnacl` is about 8 KB and
   only ever runs over handshake-sized messages), and `PeerHello` with
   `transport = 1`.
4. **`relay1`** as specified above (`relay1.js`): HKDF and AES-GCM through
   WebCrypto.
5. **The link layer**, ported as-is (`link.js`, `link-session.js`,
   `session-source.js`, `pairing-handshake.js`, `link-frame.js`,
   `address.js`, `exchange-ledger.js`): `LinkFrame`, `PairingHandshake`,
   `ExchangeLedger`, `InvitationCode` (CBOR for the `ConnBlob`).
6. **Storage** (`store.js`): the node key in IndexedDB. WebCrypto can hold the
   X25519 private key as non-extractable and still do `deriveBits`, so the key
   never has to exist as bytes in JS — better than the file on disk that
   `FileLinkStore` writes.

The shape it took, mirroring `JoinAsync`:

```js
const link = await TailcatLink.join({
  appName: "my-app",
  invitationCode: code,        // first time only
  derpMap: "/derpmap.json",
});
const answer = await link.request("status");
```

### What the browser cannot be told

Anything in the page can use the link: an XSS is a shell on the paired
machine, and no amount of care inside the library changes that. It belongs in
the README of whatever ships this.

## Testing

- **Wire format**, both sides: a hello with and without the transport byte,
  record counter gaps, stream framing, window accounting. Done — `dotnet test`
  for the .NET side, `npm --prefix clients/browser test` for the JavaScript
  one, neither needing a network.
- **The same bytes on both sides**, offline:
  `clients/browser/test/vectors/relay1-records.json` holds frames, sealed
  records, the key schedule and encoded hellos, and the JavaScript unit tests
  and `Relay1VectorTests` both check against it. Everything a session is
  agreed on before a byte of application data moves is in there: without the
  schedule case an HKDF label edited on one side, and without the hello cases
  a field added to it, would pass both test runs and fail only in somebody's
  manual interop run. The schedule is checked from a fixed shared secret
  rather than a real exchange — the ephemeral keys are whatever the two ends
  chose — which is why both sides expose it as a function of its own.
- **Interop**, over a live relay against `tailcat-demo host`: `npm --prefix
  clients/browser run interop`. Not in CI, because it needs a relay and a
  host. Node is a *test* dependency only — the library itself must not need
  it.
- **A .NET dialler must still choose QUIC**, and a `relay1` peer must still
  be refused with a reason by a build that does not have it. Both are cheap,
  and both are failures that would otherwise be silent.

- **The link itself, offline**: the JavaScript client dials through a seam
  (`dial`) that a test replaces with a session pair carried by a function
  call, and a host in memory answers it —
  `clients/browser/test/unit/link.test.mjs` against `loopback.mjs`, which is
  what `Tailcat.TestSupport` is for the .NET side. Pairing, a refusal, the
  heartbeat, the reconnect loop, a request re-sent across a session boundary
  and the drain in `close()` are all in there.

Still missing: an offline run that includes the relay itself —
`tests/Tailcat.TestSupport`'s in-memory relay behind a WebSocket endpoint,
which is the one layer the seam above steps over — and **one browser test**
under Playwright, so the WebCrypto and WebSocket paths are exercised where
they will actually run. Until those exist, everything that crosses a real
socket is verified by hand.

## What has been verified

Against the live public relays on 2026-09-03, from Node 24 using only APIs a
browser also has (`WebSocket`, `fetch`) plus `tweetnacl` for the box:

- **The WebSocket upgrade is accepted.** `wss://derp22b.tailscale.com/derp`
  and `wss://derp4f.tailscale.com/derp` both answer `101 Switching
  Protocols` and echo `Sec-WebSocket-Protocol: derp`, with a cross-origin
  `Origin` header present.
- **The relay speaks DERP over it.** The `ServerKey` greeting arrives as
  binary frames with the `DERP🔑` magic and parses with the framing in
  `DerpProtocol.cs`.
- **Login completes.** A sealed `ClientInfo` is accepted and the relay
  answers with a `ServerInfo` box that opens: `{"version":2}`.
- **Packets cross between a JavaScript client and a .NET one.** A
  `Tailcat.Derp.DerpClient` (BouncyCastle TLS, ordinary TCP) and a
  browser-shaped WebSocket client, both logged in to the Warsaw relay,
  exchanged a packet and its echo in both directions.

So the transport this specification assumes exists, and the two runtimes meet
on it. Everything above the relay — the ephemeral handshake, the record layer
and the mux — was then written on top of it in `clients/browser/src`, and the
whole of it verified end to end: `npm --prefix clients/browser run interop`
pairs with `tailcat-demo host`, exchanges requests in both directions, and
carries 300 kB through the record layer and past the credit window.

That last case is what the second implementation earned: records had been
sized against DERP's 64 KiB packet limit, and a relay reached over a
WebSocket closes a client that sends more than 32 KiB. See *Size*, above.

Scripts used before the client existed, kept out of the repository:
`derp-ws.mjs`, `derp-login.mjs`, `derp-send.mjs` and the `derpecho` console
peer.

## Open questions

- Should a `relay1` session be allowed to migrate to a new relay connection
  rather than dying with it? It would need the record counters to survive,
  which is most of a real transport. Not until something demands it.
- The .NET half of the direct path: a WebRTC stack is a large dependency for
  what a .NET machine already gets from QUIC, so it buys something only for
  a .NET host with pages joining it.
- Is per-stream credit enough, or does the connection need a window too?
- Who reviews the key schedule.

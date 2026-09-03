# The `relay1` transport

A second way to carry a tailcat session's streams, for ends that cannot open
a UDP socket — which in practice means a web browser.

Status: **specification only.** Nothing here is implemented. The negotiation
that will select it exists (`PeerTransport` in `PeerMessage.cs`); the
transport itself does not.

## Why

There is a second audience beyond browsers, found while testing: **Windows 10
has no QUIC at all**, so a node there does not start. Whatever `relay1` is
built for, it is also the only way those machines get a session.

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

- Not a direct path. A `relay1` session is relayed for its whole life. There
  is no STUN, no hole punching, and no path switching.
- Not a replacement for QUIC. Two nodes that can both speak QUIC always do —
  see *Cost*, below.
- Not lossless. It inherits exactly what a DERP relay gives and no more.

## Negotiation

The transport is chosen in the sealed hello, so a relay cannot influence it.

`PeerHello` ends with one byte naming the transport (`PeerTransport`).
Absent, it reads as `Quic = 0`, which is what a node built before the byte
existed means. `relay1` is **`1`**.

- The dialling node puts the transport it wants in its `Hello`.
- The answering node puts the transport it will actually use in its
  `HelloAck`. When it cannot do what was asked, it names what it does have
  and creates no session; the dialler sees the mismatch and fails with a
  reason instead of waiting out its handshake timeout.

When `Transport = Relay1`, the hello carries **32 more bytes** after the
transport byte: the sender's ephemeral X25519 public key (below). The
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
  so the session is closed. See *Cost*.
- **Size**: at most 64512 bytes of plaintext, which leaves room for the tag,
  the counter and the peer-message header inside DERP's 64 KiB packet limit.

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
receiver sends `WINDOW` as it consumes. It is not decoration —
`LinkFrame.MaxPayloadBytes` is 16 MB, and without credit a sender would push
that at a relay which drops what it cannot deliver, ending the session
(above). A sender with no credit stops; it does not buffer past the window.

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

## Cost

Being honest about what is given up against QUIC:

- **A dropped record kills the session.** DERP drops packets for a receiver
  that has fallen behind, and there is no retransmission here to cover it.
  QUIC rode over the same relay but recovered on its own. In practice this
  turns into a reconnect and a retried request, which is survivable, but it
  makes `relay1` measurably more brittle under load.
- **Head-of-line blocking.** Every stream shares one relay connection.
- **No direct path, ever.** Every byte crosses the relay twice.

That is the argument for keeping QUIC as the default and `relay1` as what a
browser asks for: nothing that can avoid these should pay for them.

## The browser side

What a pure-JS client has to implement, in the order it needs them:

1. **The DERP map**, fetched with `fetch`.
   `https://controlplane.tailscale.com/derpmap/default` answers
   `Access-Control-Allow-Origin: *`, so an SPA may read it directly; serving
   a copy from the app's own origin, as the upstream js/wasm demo does
   (`web/app.js` in `tailscale/tailcat`), remains the option that does not
   depend on that header staying.
2. **The relay connection**, `wss://<hostname>/derp` with the `derp`
   subprotocol, then DERP's own login: the server's key from its greeting,
   the client info sealed to it, and the frame loop from `DerpProtocol.cs`.
   No region measuring: a dialler publishes no address, so it connects
   straight to the region named in the invitation code.
3. **The peer handshake**: `PeerMessage` framing, the NaCl box
   (X25519 + HSalsa20 + XSalsa20-Poly1305 — `tweetnacl` is about 8 KB and
   only ever runs over handshake-sized messages), and `PeerHello` with
   `transport = 1`.
4. **`relay1`** as specified above: HKDF and AES-GCM through WebCrypto.
5. **The link layer**, ported as-is: `LinkFrame`, `PairingHandshake`,
   `ExchangeLedger`, `InvitationCode` (CBOR for the `ConnBlob`).
6. **Storage**: the node key in IndexedDB. WebCrypto can hold the X25519
   private key as non-extractable and still do `deriveBits`, so the key never
   has to exist as bytes in JS — better than the file on disk that
   `FileLinkStore` writes.

Intended shape, mirroring `JoinAsync`:

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
  record counter gaps, stream framing, window accounting.
- **Interop**, in CI and offline: `tests/Tailcat.TestSupport`'s in-memory
  relay behind a WebSocket endpoint, with the JS client driven from Node.
  Node is a *test* dependency only — the library itself must not need it.
- **One browser test** under Playwright, so the WebCrypto and WebSocket paths
  are exercised where they will actually run.
- **A .NET dialler must still choose QUIC**, and a `relay1` peer must still
  be refused with a reason by a build that does not have it. Both are cheap,
  and both are failures that would otherwise be silent.

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
on it. What remains unbuilt is everything above the relay: the ephemeral
handshake, the record layer and the mux.

Scripts used, kept out of the repository: `derp-ws.mjs`, `derp-login.mjs`,
`derp-send.mjs` and the `derpecho` console peer, in this session's scratchpad.

## Open questions

- Should a `relay1` session be allowed to migrate to a new relay connection
  rather than dying with it? It would need the record counters to survive,
  which is most of a real transport. Not until something demands it.
- Is per-stream credit enough, or does the connection need a window too?
- Who reviews the key schedule.

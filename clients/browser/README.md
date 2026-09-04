# tailcat-link for the browser

The other half of [`relay1`](../../docs/relay1.md): a page pairs with a
machine running `TailcatLink.HostAsync` and talks to it, with no server of
its own in between.

The host is not configured for this and does not know a browser arrived. It
offers QUIC first and settles on `relay1` because that is all the other end
can do — the choice is made below `Tailcat.Link` and never surfaces.

## Pair once

```js
import { TailcatLink } from "@tailcat/link";

const link = await TailcatLink.join({
  appName: "my-app",
  invitationCode: code,   // the first time only; after that it is remembered
});

link.onRequest((text) => `the browser says: ${text}`);

const answer = await link.request("status");
```

Every later start needs nothing from anybody:

```js
const link = await TailcatLink.join({ appName: "my-app" });
```

Deliberately the same shape as `TailcatLink.JoinAsync` in .NET, because it is
the same protocol and the same promises.

## What it handles for you

- **A pairing that survives a reload.** The identity key is generated once
  and kept in IndexedDB, so a refresh is a reconnection rather than a new
  machine asking to be paired again.
- **Reconnection.** A relay outage, a tab that was backgrounded, a host that
  rebooted — all the same from here, and all get the same answer. Nothing
  above is told.
- **Requests that survive a reconnection.** An exchange keeps its id across
  retries, so a request re-sent on a new session is answered from the host's
  memory rather than run a second time.
- **Detection that works.** Writing into a dead relayed session succeeds, so
  silence is what a host that has gone away looks like: a heartbeat and a
  per-request timeout are what notice.
- **Both directions.** The host can ask the page things too. `close()` waits
  for the answers already being written, *and* for the socket to send them,
  before it tears the session down — a handler returns long before its answer
  is on the wire, `send()` returns before the bytes leave, and a lost answer
  reaches the host as silence and a full timeout. `link.events` carries
  `answered`, once the answer has been written, and `answer-failed` when it
  could not be.
- **A refusal that stops.** A host that will not have this browser answers the
  same way however often it is asked, so a refused pairing ends the link
  rather than being retried on the backoff schedule: the stored code is
  dropped, `link.events` fires `failed`, and every pending and later
  `request()` rejects with `PairingRefusedError` instead of a deadline.

## Options

| option | default | |
| --- | --- | --- |
| `appName` | — | names the stored identity, so one origin can hold several pairings |
| `invitationCode` | `null` | needed the first time only |
| `derpMap` | `"/derpmap.json"` | where to read the relay list; must be same-origin (below) |
| `relayHost` | — | dial this relay and skip the map entirely |
| `store` | IndexedDB | `memoryStore()` for a page that would rather persist nothing |
| `requestTimeout` | 30 s | how long one attempt waits before the session is suspect |
| `requestDeadline` | 90 s | how long a request keeps being retried across sessions |
| `heartbeatInterval` | 20 s | how often silence is tested for |
| `minReconnectDelay` / `maxReconnectDelay` | 0.5 s / 30 s | backoff bounds |

## The DERP map has to be same-origin

`tailcat.dev` serves its map without the CORS header a cross-origin `fetch`
needs, so a page cannot read it directly. Ship a copy or proxy it — the
example does the latter, which is what a real deployment does. This is not a
detail of this client: the upstream js/wasm build ships its own copy for the
same reason.

## Layout

Each module mirrors its .NET counterpart, so a change on one side has an
obvious place to land on the other.

| | mirrors | |
| --- | --- | --- |
| `src/link.js` | `DurableLink.cs` | the reconnect loop, and a request re-sent across it |
| `src/link-session.js` | `LinkSession.cs` | one session: serving, one attempt, the heartbeat |
| `src/session-source.js` | `SessionSources.cs` | where the next session comes from |
| `src/pairing-handshake.js` | `PairingHandshake.cs` | the token, offered on every session |
| `src/link-frame.js` | `LinkFrame.cs` | one request per stream, length-prefixed |
| `src/idle-timeout.js` | `IdleTimeout.cs` | silence, not duration, is what times a request |
| `src/exchange-ledger.js` | `ExchangeLedger.cs` | a retried request answered again, never run again |
| `src/relay1.js` | `Relay1*.cs` | key schedule, records, stream multiplexing |
| `src/peer.js` | `PeerMessage.cs` | sealed hellos and the transport negotiation |
| `src/derp.js` | `DerpClient.cs` | the relay, over a WebSocket |
| `src/address.js` | `ConnBlob.cs`, `InvitationCode.cs` | invitation codes and the CBOR in an address |
| `src/store.js` | `ILinkStore.cs` | the identity, in IndexedDB |
| `src/bytes.js` | — | byte handling, so nothing else counts offsets twice |
| `src/nacl.js` | — | the one primitive WebCrypto does not have |

No build step: the modules load as they are.

## Dependencies

One: `tweetnacl`, for the NaCl boxes that seal a hello and log in to a relay.
WebCrypto has no XSalsa20-Poly1305, and boxes only ever run over
handshake-sized messages. Everything else — X25519 for the ephemeral
exchange, HKDF, AES-GCM — is WebCrypto.

An application that bundles installs it from npm and the bare import
resolves. A page with no build step loads its UMD build in a `<script>` tag
first, which is what the example does; `src/nacl.js` accepts either.

## Running the example

```
dotnet run --project src/Tailcat.Demo -- host      # prints an invitation code
npm --prefix clients/browser run example           # http://127.0.0.1:8777/
```

Paste the code into the page and press connect. The buttons cover a request,
a notification, and 300 kB in one request — that last one is past a record
and past the initial credit window, which is where the interesting failures
live.

## Tests

```
npm --prefix clients/browser test
```

Offline, and everything that can be checked without a socket: the varints,
frames and sealed records of `relay1`, a whole session between two ends with
the relay replaced by a function call, the link frames, and the CBOR of an
invitation code. The link itself is in there too, through the `dial` seam:
`test/unit/loopback.mjs` stands a host up in memory the way
`Tailcat.TestSupport` does for .NET, so pairing, a refusal, the heartbeat, the
reconnect loop, a request re-sent across a session boundary and the drain in
`close()` all fail a test run rather than a manual one.

The vectors in `test/vectors/relay1-records.json` — frames,
sealed records, the key schedule and encoded hellos — are read by
`Relay1VectorTests` on the .NET side too — one file, two implementations, so a
disagreement about a varint, a nonce, an HKDF label or a hello field fails a
build rather than waiting for the next manual run.

## Checking it against .NET

```
dotnet run --project src/Tailcat.Demo -- host --forget
npm --prefix clients/browser run interop -- <invitation-code>
```

Everything the unit tests cannot reach: a real relay, a real host, and the
whole link from pairing to close. Not run by CI: it needs a host to talk to
and a relay to meet it at. That is
what makes it worth having — the failures it has caught were disagreements
between two implementations that each looked correct on its own. It runs
under Node, which supplies WebSocket, WebCrypto and fetch, so the same
modules run unchanged; what differs is the TLS stack and IndexedDB.

## What it does not do

- **Host.** A browser can only join. A host pins the relay region it first
  measured and publishes an address that has to keep pointing at it, and a
  page cannot promise that.
- **Leave the relay.** There is no direct path from a browser, ever. Every
  byte crosses the relay twice, and a session is as fast as the relay is.
- **Survive a dropped record.** A gap in the counter ends the session and the
  link reconnects. That is the trade `relay1` makes for being small; the
  reasoning is in [docs/relay1.md](../../docs/relay1.md).

## Two things to know before shipping this

**An XSS is a shell on the paired machine.** The identity and the pairing
live in the page's origin, so anything running there can use the link. No
amount of care inside this client changes that, and it belongs in the README
of whatever ships it.

**The key schedule has not been reviewed.** It has the shape of Noise IK
without being it. `docs/relay1.md` says so at more length, and
`src/Tailcat.Link/README.md` has said as much about the authentication design
from the start.

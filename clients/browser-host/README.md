# tailcat-link-host

The hosting end of a Tailcat link, in JavaScript: a page or a Node process
publishes an invitation code, and the machines that join with it — a browser
running [`@tailcat/link`](../browser), or anything built on `Tailcat.Link` in
.NET — are paired with it and stay paired.

It is `TailcatLink.HostManyAsync` from .NET, and built on `@tailcat/link`
rather than beside it: every paired machine is held by the same link a page
gets from `TailcatLink.join`, with its sessions handed in instead of dialled.
Requests that wait through reconnections, exchanges resumed from where each end
got to, the heartbeat, channels and streams are the client's own code, so the
two ends cannot drift apart.

## Host

```js
import { TailcatHost } from "@tailcat/link-host";

const host = await TailcatHost.start({ appName: "my-app", maxPeers: 4 });
showQrCode(host.invitationCode); // give it to the other machine, once

host.onRequest((peer, text) => `${peer.name} asked: ${text}`);

const peer = await host.waitForPeer();
console.log(await peer.request("what time is it there?"));
```

Every later start is the same machine at the same address: the identity, the
relay region and the pairings are stored under `appName`, and a paired machine
reconnects by itself with no code.

Under Node there is no IndexedDB, so say where to keep them:

```js
import { fileStore } from "@tailcat/link-host/file-store";

const host = await TailcatHost.start({ appName: "my-app", store: fileStore("./state") });
```

The file holds this machine's private key — its name and its password at once —
and is written readable by its owner only.

## What it handles for you

- **Who gets in.** A machine is admitted with a live invitation's token and
  room to spare under `maxPeers`, and by its key ever after. Everyone else —
  including whoever learned the address by watching the relay — hears the same
  refusal, so nothing helps anybody search for the right token. The rules are
  `PairingRecord.cs`'s, ported rather than reinvented.
- **Invitations.** `host.invitationCode` is the one to publish;
  `invite({ singleUse, lifetime, label })` mints more, `revokeInvitation(id)`
  withdraws one, and `renewInvitation()` mints a new code only once the last
  has run out, so a timer never invalidates a code somebody is reading off the
  screen. A full host refuses to invite rather than hand out a code that could
  pair nothing.
- **Unpairing.** `forgetPeer(peer)` drops the machine at once and withdraws the
  invitation it came in by, because it still holds that code and reconnects on
  its own. Lowering `maxPeers` unpairs the machines seen longest ago, before
  anything is let in: the bound is a security one.
- **An address that does not move.** The region is measured once — by timing
  each relay's probe endpoint, since a page cannot send STUN — and pinned, or
  given as `regionId` the first time. The address is the key *and* the region,
  and every code already published names both.
- **Machines in other regions.** A .NET machine far from this one listens in
  another region, and is answered there.
- **Every handler is told who asked:** `onRequest((peer, text, bytes, content))`,
  `onNotify`, `onTransfer((peer, content))`, `onChannel(name, (peer, channel))`
  and `onStream(name, (peer, stream))`. A peer is a `LinkPeer` with the
  joining end's whole surface in the other direction: `request`, `notify`,
  `send`, `openChannel`, `openStream`.
- **Events.** `host.events` fires `peer-joined` and `peer-left` with
  `{ peer, error? }` on every session; `refused`, `invitation-expired` and
  `handshake-failed` naming the machine by key; and `relay-connected` and
  `relay-lost`. `invitation-expired` is told apart because it is the one
  refusal with a cure — invite again — and only this end can apply it.

## What it does not do

- **Leave the relay.** A host here offers `relay1` only: there is no UDP in a
  page, so no QUIC and no direct path. A .NET machine that could use QUIC
  agrees to relay1 because it offers it second. Every byte crosses the relay.
- **Survive its own relay connection.** When it drops, every session goes with
  it — relay1 has no resend here — and the machines at the other end dial
  again; exchanges carry on from where each end got to.
- **Stay up in a tab nobody looks at.** A page that is closed, reloaded or put
  to sleep by the browser is a host that is gone until it comes back. Under
  Node it is a host like any other.

## Two things to know before shipping this

**Every admitted machine reaches your handlers.** That is what `maxPeers`
bounds, and why `peer.name` — sent by the other machine and authenticated by
nothing — must never be what anything is keyed on. Key on `peer.key`.

**In a page, an XSS is the host.** The identity sits in IndexedDB beside
everything else the origin can read; whoever runs script there can answer as
this machine and read what its peers send.

## Running it

```
npm --prefix clients/browser ci        # the client this is built on
npm --prefix clients/browser-host ci
npm --prefix clients/browser-host test
npm --prefix clients/browser-host run example    # a page that hosts, on :8778
npm --prefix clients/browser-host run live -- --self   # hosts and joins itself over a real relay
npm --prefix clients/browser-host run live       # hosts; then, from .NET:
dotnet run --project src/Tailcat.Demo -- join <invitation-code>
```

The unit tests stand a host and the pages that join it up against a relay in
memory, with nothing faked above it. The live run is not in CI: it uses
Tailscale's public relays.

Without a bundler a page resolves `@tailcat/link` itself, with an import map —
the example shows how. The two packages move in lockstep: this one reaches into
the client's modules, so it names the exact client version it was built
against.

# Tailcat.Link

Two machines that cannot see each other — different networks, no port
forwarding, no VPN, no account — pair once with a short code and stay in
touch for as long as they are switched on.

The link survives what a long-lived connection actually meets: a Wi-Fi
network changing under either end, a relay going away, and either machine
rebooting. Nothing has to be re-entered, and there is nothing to call when it
drops.

## Pair once

On the machine to be reached:

```csharp
await using ILink link = await TailcatLink.HostAsync("my-app");
Console.WriteLine(link.InvitationCode);      // show once, as text or a barcode
link.OnRequest(command => Run(command));     // answer whatever is asked
```

On the machine doing the reaching, with that code the first time only:

```csharp
await using ILink link = await TailcatLink.JoinAsync("my-app", code);
string answer = await link.RequestAsync("status");
```

Every later start needs nothing from anybody:

```csharp
await using ILink link = await TailcatLink.JoinAsync("my-app");
```

Both ends are equal once paired: each can ask, each can answer, and each can
send a message the other did not ask for (`NotifyAsync`).

## What it handles for you

- **A pairing that survives a restart.** The identity key is generated once
  and stored — encrypted to the user account with DPAPI on Windows, mode
  0600 inside a 0700 directory on Unix, written through a temporary file so
  a power cut cannot leave half of one.
- **An address that stays valid.** A host pins the relay region it first
  measured, so the code published once keeps pointing at it however far the
  machine moves.
- **Reconnection.** A relay outage, a network change, a peer that rebooted,
  and a peer that has been away for a day all look the same from here — a
  session that stopped answering — and all get the same answer.
- **Detection that works.** Writing into a dead session succeeds, so silence
  is what a machine that has gone away looks like: a heartbeat and a
  per-request timeout are what notice.
- **Requests that survive a reconnection.** A request is re-sent across a
  reconnection but not re-run: it carries an id, and a peer that already
  answered replies from memory.
- **Pairing that cannot be stolen.** The code carries a secret that expires,
  and the first machine to use it is pinned — everyone after it is refused.

## Where it sits

`Tailcat.Link` is the friendly layer on top of `Tailcat.Net`, which meets a
peer at one of Tailscale's DERP relays, authenticates it with sealed
messages, punches a direct UDP path when it can, and carries QUIC over
whichever path is better. The relay only ever sees QUIC packets it cannot
read.

Requires .NET 10. QUIC — which is what carries a session onto a direct path —
needs Windows 11 or Server 2022 and later, macOS, or Linux with `libmsquic`
installed; .NET does not carry its own copy. Where it is missing, including
on Windows 10, the link still works: the two ends negotiate `relay1` instead
and the session stays on the relay, slower but no different to use. See
`docs/relay1.md`.

A browser can hold one of these links too, over the same `relay1`: the host
is written exactly as above and never learns which arrived. The JavaScript
client lives in the repository under `clients/browser` and is not published
on npm.

`Tailcat.Net` and the two layers under it are not published separately; their
assemblies ship inside this package, so one reference is the whole thing.

## Known limits

Hole punching between two different NATs is verified: a home connection and
an LTE carrier NAT moved off the relay onto a direct path, 69 ms down to
30 ms. Between two sufficiently hostile NATs there is no direct path at all,
and then the session stays on the relay — which works, but is slower and
carries every byte past somebody else's server.

The authentication design has not been reviewed by anyone outside this
project.

## Licence

BSD-3-Clause. Parts are ported from
[tailscale/tailcat](https://github.com/tailscale/tailcat) and carry
Tailscale's copyright; see the LICENSE file in the package. Not affiliated
with or endorsed by Tailscale Inc.

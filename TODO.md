# TODO

## Discovery on the LAN without a relay

Today a direct LAN path already forms: `PeerHello` carries every local
interface address (`TailcatNode.LocalAddresses`), `PeerLink` probes them and
picks the lowest RTT. But the first contact — hello and the exchange of
addresses — always goes through a DERP relay, so two machines on one LAN with
no internet never find each other.

Idea: announce the node on the LAN (multicast / mDNS) with its node key, and
treat that as a second signalling channel beside the relay, so hellos can
reach a peer whose announcement was heard locally. Open questions:

- what the announcement reveals: a node key on the LAN is visible to everyone
  there, the same concern as `LinkProtocol.HelloFrameBytes` on the relay;
- how it fits "a node's address is key + home region" and the pinned region
  in an invitation code;
- `clients/browser` cannot do it (no UDP), so it stays .NET-only.

## From the field test (tools/field-test/RESULTS.md)

- ~~A .NET request over a direct QUIC path costs two round trips~~ — fixed:
  the request no longer waits for its stream to close, and small frames go as
  one write. 85 ms → 42 ms on a 41 ms path.
- ~~A .NET client connects in ~10 s from cold~~ — now 2.5–4.3 s. The joining
  end no longer measures every region (it listens in the host's, as a page
  does: ~5 s), and STUN asks the fallback servers beside the map's relays,
  which answer no STUN, instead of after their timeout (~2 s at each end of
  every handshake).
- **The QUIC handshake over the relay still takes 1.5–3 s**, against ~70 ms
  for the hello it follows. Find out what it waits for — a PTO on a lost
  Initial, msquic's initial RTT guess, the server certificate — with the
  observer on both ends of a real relay.
- ~~"The direct path was lost: the direct path closed" for a client that
  finished~~ — a channel the other end closes while ICE is up is reported as
  that, and not counted against the path.
- **The LAN path from a browser to a Node host runs through the router**, as
  Edge's address is an mDNS name `node-datachannel` does not resolve. Works;
  costs a few milliseconds. Not ours to fix: resolving `.local` needs mDNS in
  the host's WebRTC stack, and a host in a page has it.

## QUIC on Windows 10: done, CI could hold it

Verified on a real Windows 10 (build 19045): the fallback gives a
framework-dependent application QUIC and a direct path. A CI job could pack
and consume the package framework-dependent and check that the OpenSSL
`msquic.dll` lands beside it, as `single-file-quic` checks the runtime's;
there is no Windows 10 runner to do more.

## A direct path on a carrier gave up once in 30 s

On the phone, on a carrier, ICE reported "disconnected" for longer than the
3 s grace once in a run and the session ended; the link was back, relayed, in
1.8 s and direct again shortly after. A longer grace on a carrier, or a
liveness check of the path's own, would ride such a gap out instead.

## The direct path between JavaScript ends: what is left

`directPath` (WebRTC carrying relay1 records, `docs/relay1.md`, *A direct
path*) is in and off by default. Still open, in the order they would pay:

- **Try it in real browsers, across real networks.** The tests run on a fake
  WebRTC and one run by hand on `node-datachannel`. Two browsers on one LAN
  hide their addresses behind mDNS names, and whether that resolves depends
  on the operating systems. Turn it on by default only after that.
- **Fall back to the relay when the path dies**, instead of ending the
  session. Needs to know what the channel delivered.
- **The .NET half**, for a .NET host with pages joining it. A WebRTC stack is
  a large dependency.
- **A liveness check of its own on the channel**, if ICE's 3 s of
  "disconnected" proves too slow or too jumpy in the field.

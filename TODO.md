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

## The direct path between JavaScript ends: what is left

`directPath` (WebRTC carrying relay1 records, `docs/relay1.md`, *A direct
path*) is in and off by default. Still open, in the order they would pay:

- **Try it in real browsers, across real networks.** The tests run on a fake
  WebRTC and one run by hand on `node-datachannel`. Two browsers on one LAN
  hide their addresses behind mDNS names, and whether that resolves depends
  on the operating systems. Turn it on by default only after that.
- **Let a direct session outlive its relay connection.** Today it still ends
  with it, so a relay that is cut every few seconds ends sessions that no
  longer need it.
- **Fall back to the relay when the path dies**, instead of ending the
  session. Needs to know what the channel delivered.
- **The .NET half**, for a .NET host with pages joining it. A WebRTC stack is
  a large dependency.
- **A liveness check of its own on the channel**, if ICE's 3 s of
  "disconnected" proves too slow or too jumpy in the field.

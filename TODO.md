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

- **A .NET request over a direct QUIC path costs two round trips**: 85 ms on
  a path that probes at 41 ms, where a browser on the same networks took
  48 ms. Find out where the second one goes (stream opening, an
  acknowledgement before the answer, QUIC's ACK delay) before anything
  interactive leans on .NET-to-.NET.
- **A .NET client connects in ~10 s from cold** (6 s building the node and
  measuring regions, 4 s of QUIC handshake over the relay); a page in under
  one. The region measurement could run beside the dial rather than before it.
- **"The direct path was lost: the direct path closed"** is what a host logs
  for a client that simply finished. Tell a channel closed by the other end
  apart from one that failed.
- **The LAN path from a browser to a Node host runs through the router**, as
  Edge's address is an mDNS name `node-datachannel` does not resolve. Works;
  costs a little latency.

## QUIC on Windows 10: try it on one

`Microsoft.Native.Quic.MsQuic.OpenSSL` referenced by the application gives
Windows 10 QUIC (`Tailcat.Link.targets`, the Tailcat.Link README). Loaded and
tested under Windows 11 only. On a Windows 10 machine:
`dotnet run --project src/Tailcat.Demo -p:OpenSslQuic=true -- listen`, connect
to it from another machine, and see the route go direct. If it does, consider
a CI job that publishes a consumer with the reference, as `single-file-quic`
does without it.

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

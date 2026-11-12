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
- ~~The QUIC handshake over the relay takes 1.5–3 s~~ — msquic's socket let
  the first datagram from a new address go, at both ends, and QUIC waited a
  1 s probe timeout each time; `UdpBridge` now sends one byte first. A session
  over the relay is up in 0.2–0.45 s (Windows 10 to Windows 11, 90 ms relay).
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

## ~~A direct path on a carrier gave up once in 30 s~~

Done differently: a path that stops answering now sends the session back to
the relay instead of ending it (acknowledgements and a resend, *A direct
path* in `docs/relay1.md`), so a gap on a carrier costs a few seconds on the
relay, not a session. Verified on real stacks (Edge on Windows 10 against the
Node host, UDP blocked for 12 s): no request failed, direct again 5 s after.
Still worth trying on the phone itself, on a carrier.

## The direct path between JavaScript ends: what is left

`directPath` (WebRTC carrying relay1 records, `docs/relay1.md`, *A direct
path*) is in and on by default, through Google's STUN server. Still open:

- **The .NET half**, for a .NET host with pages joining it. A WebRTC stack is
  a large dependency.
- ~~A liveness check of its own on the channel~~ — the `alive` pulse: a dead
  path is left in 1.6–2 s rather than ~10 s (Edge on Windows 10, UDP blocked).

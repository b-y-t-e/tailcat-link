# Field test: last full run

2026-11-12, library at 0.5.6 (`c90ea36`) plus the field-test tool itself.
Medians of a request answered once a second for 30 s (60 s across a network
change); "relayed" is the first request or two, before a direct path formed,
unless the route stayed relayed.

## Where

| | network | public address | client |
| --- | --- | --- | --- |
| **host** | home, Wi-Fi | 77.236.17.96 | both hosts ran here (Windows 11) |
| **LAN** | same home network, Wi-Fi | 77.236.17.96 | Windows 11, Edge 157, `tailcat-demo` |
| **WAN** | another network, Ethernet behind NAT | 188.252.20.110 | Windows 11, Edge 157, `tailcat-demo` |
| **phone** | Wi-Fi at home, then a carrier (CGNAT, 100.116.x) | 77.236.17.96 / 46.112.196.129 | Galaxy S24 Ultra, Android 17, Chrome 157 |

Every end used `tailcat.dev`'s DERP map and region 303. Tailscale was stopped
on the LAN and WAN machines and its addresses filtered out of ICE; the runs
before that are not counted (see README, "What will fool you").

## Results

| host | client | route | relayed | direct | how the direct path ran |
| --- | --- | --- | --- | --- | --- |
| JavaScript | browser, LAN | **direct** | 182 ms | 28–34 ms | srflx ↔ srflx, both 77.236.17.96 (through the router) |
| JavaScript | browser, WAN | **direct** | 140 ms | 48 ms | srflx 188.252.20.110 ↔ srflx 77.236.17.96 |
| JavaScript | phone, Wi-Fi | **direct** | 151 ms | 19 ms | host 192.168.0.21 ↔ host 192.168.0.146 |
| JavaScript | phone, carrier | **direct** | 219 ms | 79 ms | srflx 46.112.196.129 ↔ srflx 77.236.17.96 |
| JavaScript | phone, carrier → Wi-Fi mid-session | **direct** again | 221 ms | 45 ms | reconnected 1.5 s after the switch, direct 0.35 s later, no request lost |
| JavaScript | .NET, LAN / WAN | relayed | 94 / 76 ms | — | relay1, as expected |
| JavaScript | .NET `--relay-only`, WAN | relayed | 74 ms | — | |
| .NET | .NET, LAN | **direct** | — | 39 ms | QUIC |
| .NET | .NET, WAN | **direct** | — | 85 ms | QUIC, 188.252.20.110 (path RTT 41 ms) |
| .NET | .NET on OpenSSL msquic, LAN | **direct** | — | 20 ms | QUIC, the Windows 10 build against the Schannel one |
| .NET | .NET `--relay-only`, LAN / WAN | relayed | 95 / 72 ms | — | relay1 |
| .NET | browser, LAN / WAN | relayed | 94 / 74 ms | — | relay1 |
| .NET | phone, Wi-Fi / carrier | relayed | 107 / 134 ms | — | relay1 |

Up to five clients ran against one host at once (series A–E), browsers, .NET
and the phone together; none affected another. No request failed in any
counted run.

## What it found

- **Between two JavaScript ends the direct path works on every network
  tried**, the carrier included, and cuts the round trip to a third or less.
  It also survives a network change the way it was designed to: the session
  ends, the link is back in a second and a half, direct again at once.
- **A .NET request over a direct QUIC path takes about two path round trips**:
  85 ms on a path whose own probes measured 41 ms, where the browser on the
  same pair of networks took 48 ms. Not investigated yet; it matters for
  anything interactive on .NET-to-.NET.
- **A .NET client takes ~10 s to connect from cold** (about 6 s building the
  node and measuring regions, about 4 s for the QUIC handshake over the
  relay); on OpenSSL msquic 12–17 s. A page connects in under a second.
- **A browser on the LAN reached the Node host through the router**, not
  across the LAN: Edge hides its LAN address behind an mDNS name, which
  `node-datachannel` does not resolve. Still direct, and the phone, whose
  Chrome gave its real address, went across the LAN at 19 ms.
- **A host logs a client that finished normally as "the direct path was
  lost: the direct path closed"**, because the channel closing is the first
  thing it hears. Misleading in an operator's log; the session did not fail.

### Fixed since (measured on the same WAN pair, 2026-11-12)

| | before | after |
| --- | --- | --- |
| .NET request over a direct QUIC path | 85 ms (path 41 ms) | 42 ms |
| .NET client, start to connected | ~10 s | 2.5–4.3 s |
| host's log for a page that finished | "the direct path was lost" | "the other end closed the direct path" |

Earlier runs that did not count, each for a reason now in the README: a WAN
path through Tailscale's tunnel (twice: once via `100.x`, once via its IPv6
range, which the first filter missed), a .NET client against a host on a
different DERP map, two "carrier" runs on which the phone had turned Wi-Fi
back on, and a .NET host whose invitation had expired after its default
window.

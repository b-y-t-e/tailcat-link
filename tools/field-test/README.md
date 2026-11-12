# Field test

Every pairing of the .NET and JavaScript ends, on real machines and real
networks: what the unit tests, the in-memory relay and `fake-rtc.mjs` cannot
reach. Run it before turning a transport or a default on for real users, and
after anything that touches how two machines find each other.

It is not in CI and cannot be: it needs several machines on different
networks, a phone, and Tailscale's public relays.

## What is in it

| | |
| --- | --- |
| `host.mjs` | the JavaScript host under Node, with real WebRTC (`node-datachannel`) |
| `tailcat-demo field-host` | the .NET host (`src/Tailcat.Demo/FieldTest.cs`) |
| `web/field.html` | the browser client; opened by Playwright on a PC, by `phone.mjs` on a phone |
| `tailcat-demo field-join <code>` | the .NET client |
| `web/serve.mjs` | serves the page on `localhost:8777` and proxies the DERP map |
| `client.mjs` | runs the page in a real Chrome or Edge, without a window |
| `phone.mjs` | opens and closes the page in Chrome on an Android phone, through adb |
| `summary.mjs` | one line per report |

Every client pings its host once a second for `seconds` (30 by default) and
then sends one report over the link itself: the route it saw over time, the
median round trip on each route, failures, the time to connect, and — for a
browser — the ICE candidate pair that carried the direct path and the
client's public address. The host prints each as a `REPORT` line, adding the
route it saw itself. Both hosts speak the same, so any client pairs with
either.

## Setting up

On the machine that hosts:

```bash
npm --prefix clients/browser ci
npm --prefix clients/browser-host ci
npm --prefix tools/field-test ci
dotnet build src/Tailcat.Demo -c Release
```

On each machine that runs a client: Node 22+, Chrome or Edge, this repository
(or `clients/browser/src`, `clients/browser/node_modules/tweetnacl` and
`tools/field-test` in the same layout), `npm --prefix tools/field-test ci`,
and `dotnet publish src/Tailcat.Demo -c Release -r win-x64 --self-contained`
copied over — self-contained, because a test machine need not have .NET 10.

A phone: Android, USB debugging on, plugged in by cable to the machine that
serves the page. The cable is what lets the test keep driving it after it
leaves Wi-Fi.

## Running it

```bash
# the hosts (each prints CODE <invitation>)
node tools/field-test/host.mjs --reports js.jsonl
dotnet src/Tailcat.Demo/bin/Release/net10.0/tailcat-demo.dll field-host --reports dotnet.jsonl

# on every client machine
node tools/field-test/web/serve.mjs
node tools/field-test/client.mjs msedge "http://localhost:8777/field.html?code=<CODE>&name=<label>&seconds=30"
tailcat-demo field-join <CODE> --name <label> --seconds 30 [--relay-only]

# the phone
adb reverse tcp:8777 tcp:8777
adb forward tcp:9222 localabstract:chrome_devtools_remote
node tools/field-test/phone.mjs open "http://localhost:8777/field.html?code=<CODE>&name=<label>"
node tools/field-test/phone.mjs close

node tools/field-test/summary.mjs js.jsonl
```

`--relay-only` on the .NET client is a machine without QUIC — Windows 10, a
Linux without `libmsquic` — on one that has it. `-p:OpenSslQuic=true` when
building the demo is a Windows 10 machine given QUIC by the OpenSSL build.
`direct=0` on the page turns `directPath` off; `stun=0` leaves only the
machines' own networks.

## The matrix

| host | client | expected |
| --- | --- | --- |
| JavaScript | browser, same network | direct |
| JavaScript | browser, other network | direct (needs STUN) |
| JavaScript | phone, Wi-Fi / carrier | direct, or relayed where the carrier's NAT will not punch |
| JavaScript | .NET | relayed (relay1; .NET has no WebRTC) |
| .NET | .NET, same / other network | direct (QUIC) |
| .NET | .NET `--relay-only` | relayed (relay1) |
| .NET | .NET on OpenSSL msquic | direct (QUIC) |
| .NET | browser or phone | relayed (relay1) |
| either | several clients at once | each as above, none affecting another |

`RESULTS.md` is the last full run.

## What will fool you

Each of these produced a wrong result before it was understood.

- **Tailscale on the test machines.** Its addresses (`100.64.0.0/10`,
  `fd7a:115c:a1e0::/48`) are reachable from every machine on the tailnet, so a
  "direct path between two networks" goes through Tailscale's tunnel instead.
  `web/filter.js` keeps them out of ICE on both ends; .NET has no such filter,
  so stop Tailscale (`tailscale down`) on the machines of a .NET-to-.NET run.
  The tell is a path on `100.x` or `fd7a:…`.
- **Two DERP maps.** Every end must use the same one. `tailcat.dev`'s and
  Tailscale's number their regions differently; a .NET client given a host in
  a region of the other map says "the DERP map has no region …", a page just
  does not connect.
- **A phone that turns Wi-Fi back on.** Samsung's "Auto Wi-Fi" re-enables it
  within seconds of `cmd wifi set-wifi-enabled disabled`, and the "carrier"
  run is a Wi-Fi run. Check the report's `ip`: on a carrier it is not the
  home address. Turn Wi-Fi off by hand, with the automatic switch off.
- **A tab in the background.** Chrome throttles its timers to one a minute;
  a second test tab on the phone reports a third of the samples. One at a time.
- **A server already on the port.** A second `serve.mjs` fails to bind and the
  first, older one keeps answering — with whatever it was started with.
- **`localhost` only.** WebCrypto and WebRTC need a secure context: the page
  must be opened as `http://localhost`, never by the machine's LAN address.

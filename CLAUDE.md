# CLAUDE.md

Guidance for Claude Code when working in this repository.

## What this is

A .NET 10 port of the parts of [tailscale/tailcat](https://github.com/tailscale/tailcat)
that tailcat implements itself, plus a networking layer (`Tailcat.Net`) that
does the *job* of Tailscale's data plane — meet at a relay, punch a direct
path, carry reliable streams — over QUIC rather than WireGuard.

The Go source it was ported from is cloned at `D:\work\sources\tailcat`.
Read it before changing anything that claims to mirror Go behaviour.

## Commands

```bash
dotnet build
dotnet test                       # unit tests only
TAILCAT_LIVE_TESTS=1 dotnet test  # also end-to-end tests over public DERP relays

# The two-machine demo (see README). "connect" runs the interactive side,
# "ping" the one that only measures, "host" runs a Tailcat.Link pairing —
# which is what the browser client joins:
dotnet run --project src/Tailcat.Demo -- listen [--relay-only]
dotnet run --project src/Tailcat.Demo -- connect <address>
dotnet run --project src/Tailcat.Demo -- ping <address>
dotnet run --project src/Tailcat.Demo -- host [--forget]

# Diagnosing a direct path that will not form. natcheck classifies this
# machine's NAT; punch is bare UDP with none of this library involved, which
# is what separates "the networks cannot" from "the code does not".
dotnet run --project src/Tailcat.Demo -- natcheck
dotnet run --project src/Tailcat.Demo -- punch [ip:port]

# The browser client (clients/browser), offline and then against a host:
npm --prefix clients/browser ci
npm --prefix clients/browser test
npm --prefix clients/browser run interop -- <invitation-code>

dotnet pack -c Release -o artifacts   # builds the one published package
python Deploy/publish.py --dry-run    # everything a release does but the push
```

The build must stay at **zero warnings**. Analyzers are on via
`Directory.Build.props`; when one fires, fix it or suppress it *locally* with
a written justification — never blanket-disable a rule. CI builds with
`-warnaserror`, so a warning is a failed build, not a note in the log.

**Check Release, not just Debug.** Some analyzers only run there — CA2022, an
unchecked `Stream.ReadAsync` result, passed every Debug build and stopped a
release. `dotnet build -c Release -warnaserror` is what CI and
`Deploy/publish.py` do.

Package versions are central (`Directory.Packages.props`): a `PackageReference`
here carries no `Version`. Restore is pinned to nuget.org by `NuGet.config`,
which central package management requires and which keeps the build from
depending on whatever feeds a machine happens to have.

## Layers

```
Tailcat        wire format (ConnBlob/CBOR), keys, DERP map fetching, proxying
Tailcat.Derp   DERP relay client: framing, TLS, reconnection, region pool
Tailcat.Net    sessions: STUN, sealed control messages, path selection, QUIC
Tailcat.Link   the durable, paired link on top: stored identity, reconnection
Tailcat.Cli    the pure logic from cmd/tailcat
Tailcat.WebDemo  the webdemo package
Tailcat.Demo   tailcat-demo, for verifying a link between two machines

clients/browser  the JavaScript client: the same link, in a page
```

`clients/browser` has no place in the solution — it is JavaScript — and CI
runs its own tests in a job of their own. Its modules mirror the .NET ones one for one
(`link.js` ↔ `DurableLink.cs`, `relay1.js` ↔ `Relay1*.cs`, and so on), which
is the only thing keeping two implementations of one protocol in step: a
change on one side has an obvious place to land on the other. `npm --prefix
clients/browser test` runs its own unit tests offline; `npm --prefix
clients/browser run interop` checks the two against each other over a real
relay, against `tailcat-demo host`. The record vectors in
`clients/browser/test/vectors/relay1-records.json` are read by both sides —
`Relay1VectorTests` is the .NET half — so the wire formats cannot drift apart
without a build failing. Add to that file when you change one.

## Publishing

**Only `Tailcat.Link` is a package.** Everything else has `IsPackable=false`
(the default, set in `Directory.Build.props`) and the assemblies of `Tailcat`,
`Tailcat.Derp` and `Tailcat.Net` ship *inside* the `Tailcat.Link` package,
bundled by the `IncludeReferencedProjectsInPackage` target in its csproj. Two
consequences worth remembering:

- The `ProjectReference` to `Tailcat.Net` is `PrivateAssets="all"`, so it does
  not become a nuspec dependency. That also stops it flowing transitively, so
  a test project that needs `Tailcat.Net` must reference it by name.
- Anything those projects pull from NuGet must be declared again as a
  `PackageReference` in `Tailcat.Link`, or a consumer restores a package whose
  bundled assemblies cannot load. CI packs and then actually consumes the
  package on every push, which is what catches this.

Copyright headers are not decoration: files ported from Go (`src/Tailcat`,
`src/Tailcat.Cli`, `src/Tailcat.WebDemo` and their tests) carry Tailscale's
line, new files do not. `LICENSE` explains the split and ships in the package.

## Things that will bite you

These were each found the hard way; the README explains them at length.

- **`SslStream` cannot talk to a DERP relay.** The relay appends a self-signed
  Ed25519 "meta certificate" to its chain and Schannel rejects the whole chain
  with `SEC_E_INVALID_TOKEN` before any callback runs. TLS goes through
  BouncyCastle, and certificate validation is ours to do.
- **BouncyCastle TLS must run in non-blocking mode.** Stream mode holds its
  lock across a blocking read, so a parked receive loop blocks every send
  behind it — measured as a 63-second stall.
- **A received datagram is borrowed, not owned.** One buffer is reused for
  every packet; anything outliving the call must copy.
- **QUIC opens streams lazily.** Until the opener writes, the peer never sees
  the stream. Tests where the listener speaks first will hang.
- **A node's address is key + home region**, not a bare key. Two nodes far
  apart choose different regions, and a bare key sends into the wrong one.
  This is why `Tailcat.Link` pins a host's region once and never re-measures:
  the invitation code already published must keep pointing at the machine.
- **A session is not always QUIC.** `relay1` carries one on the relay when an
  end cannot have QUIC — a browser has no UDP socket, and Windows 10 has no
  QUIC at all and used to refuse to start. It is chosen below `Tailcat.Link`,
  from a preference list in `PeerHello`, so nothing above ever sees it. Two
  consequences: a record is capped at 32256 bytes rather than DERP's 64 KiB,
  because a relay reached over a WebSocket closes a client that sends more
  than 32 KiB; and a dropped record ends the session outright, since there is
  no retransmission there. `docs/relay1.md` is the specification.
- **Writing into a dead session succeeds.** The bytes go to a relay with
  nobody to hand them to, so a broken link is silent rather than faulty.
  Anything that must notice needs a heartbeat and a per-request timeout.

## Conventions

- **Comments explain *why*, never *what*.** The bar: would a competent reader
  wonder why this is here? Most of the comments in this repo record a
  constraint, a failure mode, or a decision — that is what they are for.
- **Tests are xUnit v3 + `TestContext.Current.CancellationToken`.** Name them
  after the behaviour, not the method. Tests ported from Go keep the Go test's
  intent and say so in the summary.
- **Live tests are opt-in** behind `TAILCAT_LIVE_TESTS=1`: they use
  Tailscale's public, rate-limited relays, so CI must not depend on them.
  Give them generous timeouts — a shared relay under load varies a lot.
  Because CI never runs them, a live test is *extra* coverage, never the only
  coverage: `tests/Tailcat.TestSupport` holds an in-memory DERP relay and a
  manually advanced clock, and `TailcatNodeOptions.ConnectRelay` is the seam
  that stands a whole node up against them offline.
- **Every `IAsyncDisposable` here is idempotent** (`_disposed` guard) and has
  a `DisposingTwiceIsHarmless` test. Keep that true for new ones.
- **Anything measuring time takes a `TimeProvider`**, so tests never sleep.

## What is deliberately not here

Interop with the Go implementation (this uses QUIC, not WireGuard), the SSH
server, Go's js/wasm build (a browser gets `clients/browser` instead), and a
userspace TCP/IP stack. The README's "What
was not ported, and why" is the authority; keep it honest when scope changes.

**Hole punching between two different NATs is verified** — a home connection
to an LTE carrier NAT, 69 ms relayed down to 30 ms direct, using
`tailcat-demo`. Four defects had made it impossible rather than unlikely, all
of them silent because a relayed session still works: the DERP map's relays
answer no STUN so no node learned its public address; region ranking
therefore measured nothing and every node chose New York; punching ran for
five seconds once and was never retried; and on Windows `SIO_UDP_CONNRESET`
let one bounced probe abort the receive a peer's answer was arriving on. The
README tells the whole story. `ITailcatObserver.DirectProbeSent` and
`DatagramArrived` are what made it diagnosable — reach for them first when a
path will not form, because a failed punch looks exactly like a peer that is
switched off.

**QUIC is narrower than "Windows, Linux, macOS"**: Windows 10 has none and
Linux needs `libmsquic` from the distro. That is no longer fatal — such a
node offers only `relay1` and works, relayed. A pair that can do QUIC still
does, because the transport list is in preference order.

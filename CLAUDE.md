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
# "ping" the one that only measures:
dotnet run --project src/Tailcat.Demo -- listen
dotnet run --project src/Tailcat.Demo -- connect <address>
dotnet run --project src/Tailcat.Demo -- ping <address>

dotnet pack -c Release -o artifacts   # builds the one published package
```

The build must stay at **zero warnings**. Analyzers are on via
`Directory.Build.props`; when one fires, fix it or suppress it *locally* with
a written justification — never blanket-disable a rule. CI builds with
`-warnaserror`, so a warning is a failed build, not a note in the log.

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
```

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
server, the js/wasm build, and a userspace TCP/IP stack. The README's "What
was not ported, and why" is the authority; keep it honest when scope changes.

A **browser client** — the reason `relay1` exists. The .NET half is built
(`src/Tailcat.Net/Relay1`, `docs/relay1.md`); the JavaScript half is not.

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

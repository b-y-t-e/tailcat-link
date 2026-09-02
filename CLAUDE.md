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

# The two-machine demo (see README):
dotnet run --project src/Tailcat.Demo -- listen
dotnet run --project src/Tailcat.Demo -- ping <address>
```

The build must stay at **zero warnings**. Analyzers are on via
`Directory.Build.props`; when one fires, fix it or suppress it *locally* with
a written justification — never blanket-disable a rule. CI builds with
`-warnaserror`, so a warning is a failed build, not a note in the log.

## Layers

```
Tailcat        wire format (ConnBlob/CBOR), keys, DERP map fetching, proxying
Tailcat.Derp   DERP relay client: framing, TLS, reconnection, region pool
Tailcat.Net    sessions: STUN, sealed control messages, path selection, QUIC
Tailcat.Cli    the pure logic from cmd/tailcat
Tailcat.WebDemo  the webdemo package
Tailcat.Demo   tailcat-demo, for verifying a link between two machines
```

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

The one thing never verified: **hole punching between two different NATs**.
Every direct-path test so far ran between processes on one machine, where
success was a foregone conclusion. `tailcat-demo` exists to close that gap.

# Contributing

## Before you start

Read `CLAUDE.md`. It is short, and it records the things in this codebase that
have already bitten someone: why TLS to a relay does not go through
`SslStream`, why a received datagram must be copied, why a node's address is a
key *and* a region. A change that ignores one of them will look correct and
fail in the field.

Anything that claims to mirror the Go implementation should be checked against
[tailscale/tailcat](https://github.com/tailscale/tailcat) first.

## The rules the build enforces

```bash
dotnet build -warnaserror     # must be clean; CI builds this way
dotnet test                   # unit tests; must pass
```

Zero warnings is a rule, not an aspiration. When an analyzer fires, fix it or
suppress it *locally* with a written justification — never blanket-disable a
rule.

## Conventions

- **Comments explain *why*, never *what*.** The bar: would a competent reader
  wonder why this is here?
- **Tests are xUnit v3** and use `TestContext.Current.CancellationToken`. Name
  them after the behaviour, not the method.
- **Anything measuring time takes a `TimeProvider`**, so tests need not sleep.
- **Every `IAsyncDisposable` is idempotent** and has a `DisposingTwiceIsHarmless`
  test. Keep that true for new ones.
- **Live tests are opt-in** behind `TAILCAT_LIVE_TESTS=1`. They use Tailscale's
  public, rate-limited relays, so CI never runs them — which makes a live test
  *extra* coverage, never the only coverage. `tests/Tailcat.TestSupport` has an
  in-memory relay and a manually advanced clock for the offline equivalent.

## Copyright headers

Every `.cs` file carries one. Files ported from Go (`src/Tailcat`,
`src/Tailcat.Cli`, `src/Tailcat.WebDemo` and their tests) keep Tailscale's
copyright line; new files do not. Copy the header from a neighbouring file in
the same project.

## What is published

Only `Tailcat.Link` becomes a NuGet package; everything else ships inside it or
not at all. If you add a project, it is not packable unless it says so — see
`Directory.Build.props`.

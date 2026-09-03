# Security

## What this project claims

`Tailcat.Link` pairs two machines over a public DERP relay and carries QUIC
between them. The relay sees who is talking to whom and how much, and cannot
read anything either side sends.

Concretely:

- The identity key is generated on the machine and never leaves it. It is
  stored encrypted to the user account (DPAPI) on Windows, and mode 0600
  inside a 0700 directory on Unix.
- An invitation code is an address plus a random pairing token. The address is
  not a secret — the host hands it to every relay it connects to — so the
  token is what a host actually checks. It is compared in constant time, it is
  good for one pairing, and it expires.
- A session is authenticated by pinning a TLS certificate fingerprint
  announced inside a sealed box, and carried over TLS 1.3 via QUIC.

## What it does not claim

- **The authentication design has not been reviewed by anyone outside this
  project.** The primitives are libsodium and TLS 1.3, but the way they are
  put together is this repository's own and has had no external scrutiny.
  Treat it accordingly.
- **No rekeying.** A session certificate lives as long as the process.
- **No protection against a malicious relay denying service.** A relay cannot
  read or forge traffic, but it can drop it.
- **Hole punching between two different NATs is unverified**, so the direct
  path is a performance optimisation, never a security boundary.

## Reporting a vulnerability

Report privately through GitHub's
[security advisories](https://github.com/b-y-t-e/tailcat-link/security/advisories/new)
rather than as a public issue. This is a spare-time project; expect an
acknowledgement within a week, not within a day.

Vulnerabilities in the DERP relays themselves belong to Tailscale, not here —
see [tailscale.com/security](https://tailscale.com/security).

# Transfers

A transfer is an exchange with the `Transfer` flag: `ILink.SendAsync` on one
machine, `ILink.OnTransfer` on the other. [exchanges.md](exchanges.md) is the
specification.

The shape transfers had before exchanges — `LinkFrame` tag 5, an offer, and
blocks — is no longer sent or served. It existed for 0.4, and no 0.4 machine is
in use; a frame tagged 5 is answered as an unknown message type.

The browser client under `clients/browser` does not speak exchanges, and a .NET
sender refuses a transfer to it at once, with that reason, rather than hanging.

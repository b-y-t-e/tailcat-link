# Transfers

A transfer is an exchange with the `Transfer` flag: `ILink.SendAsync` on one
machine, `ILink.OnTransfer` on the other. [exchanges.md](exchanges.md) is the
specification.

The shape transfers had before exchanges — `LinkFrame` tag 5, an offer, and
blocks — is no longer sent or served. It existed for 0.4, and no 0.4 machine is
in use; a frame tagged 5 is answered as an unknown message type.

The browser client under `clients/browser` speaks exchanges exactly as a .NET
machine does: it announces `Exchanges`, takes a transfer in `link.onTransfer`,
sends one with `link.send`, and resumes either direction after a session dies.
Only a machine that announces `LargeFrames` alone has a transfer refused at
once, with that reason, rather than hanging.

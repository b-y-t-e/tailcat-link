# Transfers

How content too large to be a message crosses a link: `ILink.SendAsync` on one
machine, `ILink.OnTransfer` on the other, and between them a wire format that
resumes mid-file when a session dies.

This is the specification, for anyone implementing the other side of it — the
browser client under `clients/browser` does not speak it yet, and a .NET
sender is answered by such a peer with a plain refusal rather than a hang.

## Why not a request

`RequestAsync` is a message: it travels as one `LinkFrame`, both machines hold
all of it at once, and it is capped at 16 MiB for exactly that reason. Raising
the cap would not help — a two-gigabyte message is two gigabytes of memory on
each end, and a session dying at 99% of it means sending all of it again.

A transfer is a stream. Neither machine holds more than a few megabytes of it,
the receiving end sets the pace, and the unit of loss when a session dies is
one block rather than the whole content.

## The exchange

Every transfer has an id (a `Guid`) that belongs to the transfer and not to
the attempt: it is the same across every session it takes to deliver, which is
what tells the receiver that a transfer arriving again is the one it is
already halfway through rather than a second one. It travels in the exchange
field of every frame below.

One attempt opens one stream and, on it:

| # | Direction | What |
| --- | --- | --- |
| 1 | sender → receiver | `LinkFrame` tagged `Transfer` (5), payload = the encoded offer |
| 2 | receiver → sender | `LinkFrame` tagged `Ok` (0), payload = the offset to start at, or `Failed` (1) with a reason |
| 3 | sender → receiver | the content, as blocks, ending with a block of length zero |
| 4 | receiver → sender | `LinkFrame` tagged `Ok` with the byte count, or `Failed` with a reason |

Step 2 is what makes a transfer resumable, and why the receiver decides the
offset rather than the sender: the receiver is the end that knows which blocks
made it out of a session that then died.

Step 4 comes only after the receiving application's handler has returned, so a
sender whose `SendAsync` completed knows the other machine has finished with
the content — not merely that the bytes arrived.

## The offer

```
[u8  version = 1]
[i64 length]              // -1 when the sender does not know
[u16 nameLen][name utf8]  // at most 1024 bytes
[u16 typeLen][type utf8]  // at most 1024 bytes
[u32 metaLen][metadata]   // at most 65536 bytes, opaque to the library
```

The name is what the sender calls the content, and is not a path: it is as
trustworthy as anything else off a network, and `../../etc/passwd` is a name a
peer may send. `IncomingTransfer.SuggestedFileName` is the sanitised form, and
the only one that should ever reach a file system.

The length is advisory and used for progress — but a transfer that ends short
of one it announced is refused rather than delivered, so a half file is never
handed over as a whole one.

## The offset

Eight bytes, big-endian: the byte of the content the sender should start from.
Zero for a transfer nobody has heard of, and whatever the receiver already
has for one it is resuming. A sender whose content cannot seek back to it
fails the transfer rather than delivering something with a hole in it.

## The blocks

```
[i32 length][length bytes] ... [i32 0]
```

Big-endian, at most 262144 bytes each — one whole `relay1` stream window, so a
block never waits for a window update in the middle of itself. A block of zero
length ends the content: a stream that simply stops is a truncation and reads
as one.

Each block is also a sign of life. A transfer that takes an hour is told from a
peer that has gone away by whether blocks are still arriving, which is why
nothing here has a total deadline — only `LinkOptions.TransferStallTimeout`,
which bounds silence.

## What each end remembers

The receiver holds a half-delivered transfer, and the handler reading it, for
`LinkProtocol.TransferRetention` — ten minutes. It holds the answer to a
finished one for the same window, so a sender whose session died between being
handed the answer and reading it does not run the handler a second time.

That window is a protocol constant rather than an option for the same reason
`ExchangeRetention` is: the sending machine decides when to come back, and
nothing on the wire tells the receiver how patient that sender was configured
to be. `TransferStallTimeout` is bounded by it.

Neither end remembers a transfer across a process restart. Resuming is for a
link that broke, not for a machine that was switched off.

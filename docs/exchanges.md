# Exchanges

How application data of any size crosses a link from 0.5 on: a kilobyte of
JSON and twenty gigabytes of video go the same way, resume where they stopped
when a session dies, and reach the other machine's handler once.

`ILink.RequestAsync(LinkContent)`, `NotifyAsync(LinkContent)` and `SendAsync`
are all this one exchange with different flags, and so are the older
`RequestAsync(bytes)` and `NotifyAsync(bytes)`, which are wrappers over it.
Channels are not: they are not durable, and `channels.md` describes them.

This is the specification, for anyone implementing the other side of it.
`ExchangeFrame.cs` is the .NET implementation; the vectors in
`clients/browser/test/vectors/link-frames.json` (`exchangeHeaders`,
`answerHeaders`, `capabilities`) are the bytes both sides must agree on.

## Who can take what

A machine says what it can take in the answer to a ping (`LinkFrame` tag 3).
Every version answers a ping and ignores what the answer carries, which is why
this is there and not in the hello: an older host reads an unknown hello as a
bare pairing token and refuses to pair.

The answer is one byte of flags:

| bit | name | meaning |
| --- | --- | --- |
| 0 | `LargeFrames` | takes a `LinkFrame` and a channel frame of any length |
| 1 | `Exchanges` | understands the `Exchange` frame (7) |

A machine built before capabilities answers with nothing, which reads as none.
Bits a machine does not know are dropped rather than refused. A session asks
once, lazily, the first time something needs to know, and remembers the answer
for as long as the session lasts.

| sender → receiver | what is sent |
| --- | --- |
| 0.5 → 0.5 | exchanges, no size limit, resumable |
| 0.5 → browser (`LargeFrames`) | single `Request` and `Notify` frames, no size limit, not resumable; a transfer is refused |

Compatibility with 0.4 is not a goal: no 0.4 machine is in use. A machine that
answers with nothing is sent nothing at all — every exchange to it is refused at
once, with the reason that it takes neither exchanges nor frames. A machine
without `Exchanges` takes a message as one array, so a message larger than an
array holds is refused before anything is sent, with that reason.

## One attempt

An exchange has an id (a `Guid`) that belongs to the exchange, not to the
attempt: it is the same on every session it takes. One attempt opens one
stream and, on it:

| # | direction | what |
| --- | --- | --- |
| 1 | sender → receiver | `LinkFrame` tagged `Exchange` (7), payload = the header |
| 2 | sender → receiver | the content as blocks, from byte zero — only with `Pipelined` |
| 3 | receiver → sender | `Ok` (0) with an `i64` offset into the content, or `Failed` (1) with a reason |
| 4 | sender → receiver | the content as blocks, from that offset — only without `Pipelined` |
| 5 | receiver → sender | the outcome, below |
| 6 | sender → receiver | `Ok` with no payload, once the outcome is here — for a request, once its answer is whole |

Step 5 depends on the flags:

- a request (`Answer`): `Ok` with the answer header, then the answer as blocks
  starting at the header's answer offset; or `Failed` if the handler threw.
- a notification with `AckOnDelivery`: `Ok` once the content has arrived,
  without waiting for the handler.
- anything else, a transfer included: `Ok` once the handler has finished, or
  `Failed` with its reason.

Step 6 lets the receiver forget the exchange at once rather than at the end of
its retention — which matters most for notifications, since an application
sending a few a second would otherwise have the other machine holding thousands.
It is never retried: a lost acknowledgement costs memory on the other machine
for a while, and nothing else.

A `Failed` in step 3 or 5 is an answer about the exchange, not a broken
session, and is not retried.

## The header

```
[u8  version = 1]
[u8  flags]            bit0 Answer, bit1 Transfer, bit2 Pipelined, bit3 AckOnDelivery
[i64 length]           -1 when the sender does not know
[i64 answer offset]    how much of the answer the sender already has; 0 the first time
[u32 len][name utf8]
[u32 len][type utf8]
[u32 len][metadata]
```

Big-endian throughout. Every field is prefixed with 32 bits, where the transfer
offer used 16: a narrower prefix is a limit on what an application can say
about its content, which is not the protocol's business.

`Transfer` chooses the handler: the transfer handler (`OnTransfer`) with it,
the request handler without.

The name is what the sender calls the content and is not a path;
`IncomingTransfer.SuggestedFileName` is the only form of it that should reach a
file system.

## The answer header

```
[u8  version = 1]
[i64 length]           -1 when the handler did not know
[u32 len][type utf8]
[u32 len][metadata]
```

## Blocks

```
[i32 length][length bytes] ... [i32 0]
```

Big-endian, at most 262144 bytes each — one whole `relay1` stream window, so a
block never waits for a window update in the middle of itself — and a block of zero length ends
the content. Content that ends short of a length it announced is a failure, not
a delivery.

**A block is taken by its position, not on trust.** The receiver knows where
each block starts — at the offset it asked for, or at zero after `Pipelined` —
and drops whatever overlaps what it already has. That one rule covers resuming,
a repeated attempt, and pipelined content arriving again.

## Pipelined

A sender that has not attempted the exchange before, and whose content has a
known length that fits one block, may set `Pipelined` and write the content
straight after the header. The offset in step 3 then changes nothing, and a
small request costs one round trip, as a message frame always did.

## Once, and the newest attempt wins

What the receiver knows about an exchange lives on the peer, not the session:
the content received so far, the running handler, its answer. An exchange that
arrives again is joined to that state rather than started again, so its
handler runs once and its answer is sent again rather than made again.

The sender runs one attempt at a time, so a newer attempt means the older one
was abandoned: the receiver cancels the older one, waits for it to let go, and
serves the newer one.

The receiver forgets an exchange on the acknowledgement in step 6, or
`LinkProtocol.TransferRetention` (ten minutes) after its last attempt ended.
Neither end remembers one across a process restart.

## Time

Nothing has a deadline on the whole: any total limit would be a limit on size.
Every exchange has one patience — how long nothing may move — and waiting for
a session counts against it, so an exchange to a machine that is gone for good
ends in bounded time.

| API | patience | waiting for the handler |
| --- | --- | --- |
| `RequestAsync(bytes)`, `NotifyAsync(bytes)` | `RequestDeadline` | within the patience |
| `RequestAsync(LinkContent)` | `TransferStallTimeout` | within the patience |
| `NotifyAsync(LinkContent)` | `TransferStallTimeout` | not waited for |
| `SendAsync` | `TransferStallTimeout` | until the handler finishes, while the session lives |

Movement is any block in either direction. An application reading its answer
slowly is not the network being silent: only reads from the network are timed.

**Bytes moving are as good as a heartbeat.** On a link saturated by a large
exchange, the answer to a ping queues behind the very bytes that prove the other
machine is there. A session whose ping went unanswered for its window, but on
which bytes moved within that window, is kept; so is a session on which one
exchange fell silent while others moved. Only silence on the whole session ends
it. Writes count as movement as well as reads, which is safe only because of
flow control: a write into a session whose peer has gone stops completing once
the transport's window is spent.

## Resuming needs content that can be read again

Carrying on after a session dies means reading from the middle: bytes, a file,
a seekable stream — on the sender for the content, and on the receiver for the
answer its handler returned. Content that cannot works until the first session
dies under it, and then fails with a readable error rather than arriving with a
hole in it.

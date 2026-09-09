# Channels

The third shape a link carries, between a message and a file.

`ILink.RequestAsync` is a **message**: one frame, held whole at both ends, a
round trip, an entry in the exchange ledger, capped at 16 MiB.
`ILink.SendAsync` is a **file**: seekable content, cut into blocks, resumed
across as many sessions as it takes, paced by the receiver.

Neither fits a microphone. Measured against 16 kHz mono 16-bit PCM — about
32 kB/s in 3 kB frames, which is an ordinary microphone — a request is a round
trip and a ledger entry per frame, and a transfer promises a durability that
would be actively wrong: a frame of audio from ten seconds ago is worth
nothing, and resuming one would be worse than dropping it.

A channel is what is left:

```csharp
// receiving, on either end
host.OnChannel("audio", async (peer, channel, ct) =>
{
    await foreach (ReadOnlyMemory<byte> frame in channel.ReadAllAsync(ct))
        Consume(peer, frame.Span);
});

// sending
await using ILinkChannelWriter audio = await peer.OpenChannelAsync("audio", ct);
await audio.SendAsync(frame, ct);
```

## The contract

- **Ordered within the channel.** Frames arrive in the order they were sent.
  Nothing here sequences them: a channel is one transport stream, and the
  transport already does. Ordering *between* channels, or between a channel
  and a request, is not promised and never was.
- **Not durable.** A channel ends with the session that carries it and is not
  resumed on the next one. `ILinkChannel.Closed` says which happened —
  `PeerClosed`, `LocalClosed` or `SessionEnded` — because those are the same
  silence and want different answers: a peer that hung up, against a link that
  is in the middle of repairing itself and will be back.
- **Paced by the reader.** The bytes go no faster than the receiving handler
  consumes them, so a sender faster than the network waits in `SendAsync`
  rather than filling memory.
- **One direction.** `OpenChannelAsync` returns an `ILinkChannelWriter`; the
  handler is given an `ILinkChannelReader`. Two directions is two channels,
  which is one fewer thing to explain than a duplex object whose halves close
  at different times.

## The wire format

A channel opens with an ordinary `LinkFrame`, tag `0x06`, whose payload is the
channel's name in UTF-8 (1 to 256 bytes). It is answered on the same stream
with a `LinkFrame` status: `Ok` when a handler took it, `Failed` with a reason
when nothing did — refused outright rather than swallowing frames into a
machine that will never read them.

After the answer, the stream carries frames:

```
+--------+------------------+
| length | payload          |
| uint32 | length bytes     |
+--------+------------------+
```

`length` is big-endian and at most 262144 (256 KiB), the same block size a
transfer uses. A zero length ends the channel: it is what tells the reading
end that the frames stopped on purpose rather than with the session, so a
stream that simply stops is `SessionEnded` and not `PeerClosed`.

Nothing is retried, acknowledged or remembered. That is the whole of it.

## What it is not

Not a substitute for `NotifyAsync`, which is still the right thing for an
occasional message nobody answers: a channel costs a stream and a handler.
Not a substitute for a transfer, which is what survives a reconnection.

`clients/browser` speaks channels — `link.openChannel(name)` and
`link.onChannel(name, handler)`, in `link-channel.js` — so a browser peer is
one more end of the same thing. A name nothing is listening for is refused
with "no such channel", whichever port the peer is.

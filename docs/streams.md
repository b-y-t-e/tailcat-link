# Streams

The fourth shape a link carries: an ordinary two-way stream of bytes.

A request is content that must arrive, resumed across sessions and handled
once ([exchanges.md](exchanges.md)). A channel is frames of the moment in one
direction, gone with the session ([channels.md](channels.md)). Neither fits what
already speaks in streams — a TCP connection to tunnel, SSH, a database
protocol — which wants bytes both ways, each direction ended on its own, and an
honest answer to "did that end, or did it break?". Before this, an application
built one out of two channels, an id to match them and an abort marker of its
own: some 370 lines where a dozen should do.

```csharp
// the end that takes it
host.OnStream("tunnel", async (peer, stream, ct) =>
{
    using TcpClient target = new();
    await target.ConnectAsync("localhost", 5432, ct);
    await using NetworkStream socket = target.GetStream();
    await Task.WhenAll(stream.CopyToAsync(socket, ct), socket.CopyToAsync(stream, ct));
});

// the end that opens it
await using LinkStream tunnel = await link.OpenStreamAsync("tunnel", ct);
await tunnel.WriteAsync(request, ct);
await tunnel.CompleteWritesAsync(ct);  // the half-close: still reading
await tunnel.CopyToAsync(output, ct);   // to a clean end
```

## The contract

- **An ordinary `Stream`.** `LinkStream` derives from it, so `CopyToAsync`, a
  `StreamReader` or a protocol library take it as it is. One reader and one
  writer may use it at once, as with a socket.
- **Ordered and paced by the reader.** Bytes arrive in the order they were
  written, and a writer faster than the far end reads waits in `WriteAsync`.
- **Not durable.** Like a channel, a stream needs a session, waits for one
  rather than being buffered, and ends with it. What must survive a reconnection
  is a request or a transfer.
- **A read of 0 is a clean end, and nothing else is.** It means the other end
  called `CompleteWritesAsync` or disposed its stream, and that everything it
  wrote has been read. Every other ending is a `LinkStreamException` — an
  `IOException`, which is what code written against `Stream` catches — whose
  `Ending` says what happened:

  | `Ending` | what happened |
  | --- | --- |
  | `PeerClosed` | the other end closed the stream while this end was still writing; what it wrote before closing all arrived |
  | `PeerAborted` | the other end abandoned it — `AbortAsync`, a handler that threw, or bytes that stopped part-way; what was read may be incomplete |
  | `Aborted` | this end aborted it |
  | `SessionEnded` | the session carrying it died |

  For a tunnel that is the difference between a download that finished and
  one that was cut off, which is the one thing a tunnel must not confuse.
- **Disposing closes; aborting abandons.** `DisposeAsync` ends this end's
  writes cleanly and stops reading, as closing a socket does. `AbortAsync` ends
  it so the other end is told it did not finish.
- **A handler's stream ends with the handler.** When the handler returns the
  stream is disposed; when it throws the stream is aborted, and the other end
  hears `PeerAborted` rather than a clean end.

`clients/browser` speaks streams too: `link.openStream(name)` and
`link.onStream(name, handler)`, where `read()` resolves with an empty array at a
clean end, `finish()` is the half-close, and a `LinkStreamError` carries the
same endings as strings (`"peer-closed"`, `"peer-aborted"`, `"aborted"`,
`"session-ended"`).

## The wire format

A stream opens with an ordinary `LinkFrame`, tag `0x08`, whose payload is the
stream's name in UTF-8 (1 to 256 bytes). It is answered on the same transport
stream with a `LinkFrame` status: `Ok` when a handler took it, `Failed` with a
reason when nothing did — "the other machine has no "tunnel" stream".

After the answer, both directions carry the same thing, independently:

```
+------------+------------------+
| length     | bytes            |
| uint32, BE | length bytes     |
+------------+------------------+
```

- A length from 1 up is data. A writer sends at most 256 KiB per piece (the
  transfer block size); a reader takes whatever length arrives, in parts, and
  never allocates what a length announces.
- **`0x00000000`** ends this direction: the writer will send nothing more.
- **`0xFFFFFFFF`** abandons the stream, both ways.

That is a channel's framing (`channels.md`) plus one marker, and the vectors in
`clients/browser/test/vectors/link-frames.json` (`streamMarkers`) hold both
implementations to the bytes.

### What each end does with the transport stream

The markers alone cannot say everything, because the transport stream under
them ends too. Two rules make the endings unambiguous:

1. **After writing `0`, an end keeps the transport stream open for as long as it
   still reads.** So when the transport ends *after* an end marker, the other
   end has let go of the stream: reads have already ended cleanly, and a write
   still in progress fails with `PeerClosed`. When the transport ends *before*
   one — at a piece boundary or inside a piece — the other end abandoned it:
   `PeerAborted`.
2. **After writing `0xFFFFFFFF`, an end keeps the transport stream open until the
   other end lets go of it**, discarding whatever still arrives, and at most for
   its request timeout. The end that reads the marker lets go at once. Closed
   straight away instead, the close would race the marker, and on QUIC a writer
   at the other end hears a closed stream before its reader reaches the marker —
   `PeerClosed` where the truth is `PeerAborted`.

A write cut part-way through a piece leaves no room for a marker; the end that
cut it just lets go of the transport, which the other end reads as a piece that
stopped part-way — an abort.

Each end reads ahead by a piece, on a task of its own. That is what lets it
notice the other end letting go while the application is only writing: without
it, a writer into a machine that stopped reading waits on flow control until the
session dies. It holds a piece or two at most, so reading still paces the writer.

A machine that predates streams answers the opening frame the way it answers any
tag it does not know — `Failed`, "unknown message type 0x08" — so opening one
fails with `RemoteHandlerException` rather than hanging.

## What it is not

Not a replacement for a channel, which is lighter for frames that have their own
boundaries, and not a replacement for a request or a transfer, which survive a
reconnection. A stream is for what already expects a socket.

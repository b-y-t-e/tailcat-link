// The host's half of the hello: read what the machine that dialled presents,
// and say whether it may stay. `PairingHandshake.AcceptAsync` is the .NET
// half, and `pairing-handshake.js` in `@tailcat/link` the end that offers.

import { utf8, withTimeout } from "@tailcat/link/internal/bytes.js";
import { decodeLinkHello } from "@tailcat/link/internal/link-hello.js";
import { FrameKind, FrameStatus, HELLO_FRAME_BYTES, readFrame, writeFrame } from "@tailcat/link/internal/link-frame.js";

// Deliberately says nothing about which part was wrong: an expired
// invitation, a wrong token and a host that is already full are one answer,
// so that nothing here helps anybody search for the right one.
const REFUSAL = "this machine is not open to you";

/// Reads the hello on `connection` and answers it.
///
/// @param admit `(hello) => { admitted, lapsed }`, the pairing rules.
/// @returns `{ admitted, lapsed }`; `admitted` is null for a machine refused.
export async function acceptPairing(connection, admit, timeoutMs) {
  return withTimeout(answer(connection, admit), timeoutMs, "the machine that dialled");
}

async function answer(connection, admit) {
  const stream = await connection.accepted.next();
  try {
    // The one read with a limit on what a peer may send, and the reason is
    // who the peer might be: nothing is known about it yet, and anyone on
    // the relay can see this host's address.
    const { tag, exchange, payload } = await readFrame(stream, undefined, { limit: HELLO_FRAME_BYTES });
    if (tag !== FrameKind.Hello) {
      await writeFrame(stream, FrameStatus.Failed, exchange, utf8("say hello first"));
      return { admitted: null, lapsed: false };
    }

    const verdict = await admit(decodeLinkHello(payload));
    if (verdict.admitted) connection.trust();
    await writeFrame(
      stream,
      verdict.admitted ? FrameStatus.Ok : FrameStatus.Failed,
      exchange,
      verdict.admitted ? new Uint8Array(0) : utf8(REFUSAL),
    );
    return verdict;
  } finally {
    await stream.close().catch(() => {});
  }
}

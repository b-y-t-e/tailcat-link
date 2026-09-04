// The token, offered on every session before anything else is asked.
//
// It is presented again on each reconnection because this end cannot tell
// whether the machine it reached still remembers it: a host that was reset is
// indistinguishable from one that never paired. `PairingHandshake.cs` is the
// .NET half, and answers the same way.

import { str, utf8 } from "./bytes.js";
import { FrameKind, FrameStatus, newExchange, readFrame, writeFrame } from "./link-frame.js";

/// Presents `pairingToken` on `connection` and waits to be let in.
///
/// @throws {PairingRefusedError} if the host will not have this browser.
export async function offerPairing(connection, pairingToken) {
  const stream = connection.openStream();
  try {
    await writeFrame(stream, FrameKind.Hello, newExchange(), utf8(pairingToken));
    const answer = await readFrame(stream);
    if (answer.tag !== FrameStatus.Ok) {
      throw new PairingRefusedError(str(answer.payload));
    }
  } finally {
    await stream.close().catch(() => {});
  }
}

/// The host will not have this browser. Deliberately says nothing about which
/// part was wrong — an expired invitation and a wrong token are one answer.
export class PairingRefusedError extends Error {
  constructor(message) {
    super(message);
    this.name = "PairingRefusedError";
  }
}

// A relay1 session with no relay under it, and a host to point it at.
//
// The .NET side stands a whole node up offline against `Tailcat.TestSupport`'s
// in-memory relay; this is the same idea for the JavaScript one, plugged in
// through the `dial` seam. Everything above the socket is the real thing —
// real records, real counters, real streams, real link frames — so a test can
// cut a session, watch the link reconnect, and see what the far end actually
// received.

import { Deferred, hex, str, utf8 } from "../../src/bytes.js";
import { FrameKind, FrameStatus, newExchange, readFrame, writeFrame } from "../../src/link-frame.js";
import { Relay1Session, deriveKeys } from "../../src/relay1.js";
import { nacl } from "../../src/nacl.js";

// There is no relay to route by key, so the destination on a record is unused.
const UNROUTED = new Uint8Array(32);

/// One end of a session, with the surface the link layer asks a connection
/// for: streams, the ones the peer opened, and a way to give up on the lot.
class LoopbackConnection {
  #session;
  #peer = null;
  #inbound = Promise.resolve();

  constructor(keys, isDialer) {
    this.#session = new Relay1Session({
      derp: { sendPacket: (_destination, record) => this.#peer?.deliver(record) },
      peerPublic: UNROUTED,
      keys,
      isDialer,
    });
  }

  pointAt(peer) {
    this.#peer = peer;
  }

  /// Takes one record. Serialised, because `handleRecord` checks a counter
  /// and two of them running at once would report a gap the relay never made.
  deliver(record) {
    this.#inbound = this.#inbound.then(() => this.#session.handleRecord(record)).catch(() => {});
  }

  get accepted() {
    return this.#session.accepted;
  }

  get closed() {
    return this.#session.closed;
  }

  openStream() {
    return this.#session.openStream();
  }

  async flush() {
    return true;
  }

  close(error = new Error("the session was closed")) {
    this.#peer = null; // A record written after this goes nowhere, as it would.
    this.#session.close(error);
  }

  /// The relay went away. Both ends learn at once, which is what happens when
  /// the one socket carrying them does.
  cut(error = new Error("the relay went away")) {
    const peer = this.#peer;
    this.close(error);
    peer?.close(error);
  }
}

/// Two ends of one session. Real traffic keys, because the record layer is
/// what carries everything the test is about.
export async function sessionPair() {
  const dialerEphemeral = nacl.box.keyPair();
  const hostEphemeral = nacl.box.keyPair();
  const identities = { sessionId: 1, dialerPublic: nacl.randomBytes(32), hostPublic: nacl.randomBytes(32) };

  const dialerKeys = await deriveKeys({
    ephemeralPrivate: dialerEphemeral.secretKey,
    peerEphemeral: hostEphemeral.publicKey,
    ...identities,
  });
  const hostKeys = await deriveKeys({
    ephemeralPrivate: hostEphemeral.secretKey,
    peerEphemeral: dialerEphemeral.publicKey,
    ...identities,
  });

  const browser = new LoopbackConnection(dialerKeys, true);
  const host = new LoopbackConnection(hostKeys, false);
  browser.pointAt(host);
  host.pointAt(browser);
  return { browser, host };
}

/// The other machine: what `tailcat-demo host` does, in memory.
///
/// It records what it was asked so a test can say what crossed the wire —
/// which exchange ids arrived, and how many times each did.
export class LoopbackHost {
  #connection;
  #pairingToken;
  #answer;
  #answersPings;

  /// @param answer what to reply to a request; may hang, throw, or take its
  ///   time, which is how the awkward cases are staged.
  /// @param answersPings a host that stops answering these is the one thing a
  ///   relayed session cannot tell from a healthy one by writing to it.
  constructor(
    connection,
    { pairingToken = "token", answer = (text) => text.toUpperCase(), answersPings = true } = {},
  ) {
    this.#connection = connection;
    this.#pairingToken = pairingToken;
    this.#answer = answer;
    this.#answersPings = answersPings;
    this.requests = [];
    this.pings = 0;
    this.paired = new Deferred();
    this.#serve().catch(() => {});
  }

  /// How many times `exchange` arrived. More than once is a retry, which is
  /// the whole point of keeping the id across sessions.
  arrivalsOf(exchange) {
    return this.requests.filter((seen) => seen.exchange === exchange).length;
  }

  /// Asks the browser something, the direction nobody remembers to test.
  async request(text) {
    const stream = this.#connection.openStream();
    try {
      await writeFrame(stream, FrameKind.Request, newExchange(), utf8(text));
      const answer = await readFrame(stream);
      if (answer.tag === FrameStatus.Failed) throw new Error(str(answer.payload));
      return str(answer.payload);
    } finally {
      await stream.close().catch(() => {});
    }
  }

  /// Asks under an id of the test's choosing, so a retry across a session
  /// boundary can be staged the way a real one arrives.
  async requestAs(exchange, text) {
    const stream = this.#connection.openStream();
    try {
      await writeFrame(stream, FrameKind.Request, exchange, utf8(text));
      return str((await readFrame(stream)).payload);
    } finally {
      await stream.close().catch(() => {});
    }
  }

  async #serve() {
    for (;;) {
      const stream = await this.#connection.accepted.next();
      this.#handle(stream).catch(() => {});
    }
  }

  async #handle(stream) {
    // A ping the host swallows leaves its stream open: closing it would send a
    // FIN, and a FIN is an answer of sorts. Silence is what is being staged.
    let leaveOpen = false;
    try {
      const { tag, exchange, payload } = await readFrame(stream);
      switch (tag) {
        case FrameKind.Hello: {
          const accepted = str(payload) === this.#pairingToken;
          await writeFrame(
            stream,
            accepted ? FrameStatus.Ok : FrameStatus.Failed,
            exchange,
            utf8(accepted ? "" : "this host is paired with another machine"),
          );
          if (accepted && !this.paired.settled) this.paired.resolve();
          break;
        }
        case FrameKind.Ping:
          this.pings++;
          leaveOpen = !this.#answersPings;
          if (this.#answersPings) await writeFrame(stream, FrameStatus.Ok, exchange, new Uint8Array(0));
          break;
        default: {
          this.requests.push({ exchange: hex(exchange), text: str(payload) });
          const answer = await this.#answer(str(payload));
          await writeFrame(stream, FrameStatus.Ok, exchange, utf8(answer ?? ""));
        }
      }
    } finally {
      if (!leaveOpen) await stream.close().catch(() => {});
    }
  }
}

/// A `dial` for TailcatLink.join. Every reconnection asks `hostFor` for the
/// machine that attempt meets, so a test stages a host that refuses, one that
/// never answers, and one that does, in that order.
export function loopbackDialer(hostFor) {
  let attempt = 0;
  return async () => {
    const { browser, host } = await sessionPair();
    hostFor(host, attempt++);
    return browser;
  };
}

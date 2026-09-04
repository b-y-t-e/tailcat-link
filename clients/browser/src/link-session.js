// One session, for as long as it lasts.
//
// Everything here is about a session that is up: serving what the host opens,
// one attempt at an exchange, and the heartbeat that decides the session has
// quietly died. Nothing here reconnects — that is `link.js`, and keeping the
// two apart is what makes either of them understandable. `LinkSession.cs` is
// the .NET half.

import { delay, str, utf8 } from "./bytes.js";
import { FrameKind, FrameStatus, newExchange, readFrame, writeFrame } from "./link-frame.js";
import { IdleTimeout } from "./idle-timeout.js";

export class LinkSession {
  #connection;
  #handler;
  #notifyHandler;
  #ledger;
  #options;
  #emit;
  #answering = new Set();
  #beating = false;

  /// @param handler read on every inbound request rather than captured, so an
  ///   application that sets its handler after connecting still answers.
  /// @param ledger shared with every other session of the same link, because
  ///   that is where a request retried after this session dies will arrive.
  constructor({ connection, handler, notifyHandler, ledger, options, emit }) {
    this.#connection = connection;
    this.#handler = handler;
    this.#notifyHandler = notifyHandler;
    this.#ledger = ledger;
    this.#options = options;
    this.#emit = emit;
  }

  get closed() {
    return this.#connection.closed;
  }

  /// Answers what the host opens, and keeps a heartbeat going so that a
  /// session that has quietly died is noticed. Writing into a dead relayed
  /// session succeeds, so silence is the only symptom there is. Resolves when
  /// the session ends, which is what the link above waits on to reconnect.
  async serve() {
    this.#beating = true;
    this.#startHeartbeat();
    try {
      for (;;) {
        this.#answer(await this.#connection.accepted.next());
      }
    } finally {
      this.#beating = false;
    }
  }

  /// One attempt at an exchange, on this session. The exchange id comes from
  /// the caller because it outlives any one session: a retry carries the id of
  /// the original, which is how the far end recognises it.
  ///
  /// Failing does not necessarily end the session — see the silence below — so
  /// a caller that wants to know must ask `closed` rather than assume.
  async exchange(kind, exchange, payload, { expectAnswer = true } = {}) {
    // Silence rather than duration, so that a payload too large to move in
    // one window is not confused with a peer that has gone away. The .NET
    // end measures the same thing the same way; see IdleTimeout.cs.
    const idle = new IdleTimeout(this.#options.requestTimeout);
    // openStream() belongs inside the try: the session can close between the
    // caller asking for it and here, and that must be reported like any other
    // broken session rather than thrown at the application.
    let stream;
    try {
      stream = this.#connection.openStream();
      idle.restart();
      await writeFrame(stream, kind, exchange, payload, idle);
      // A notification is not answered — the far end runs the handler and
      // closes the stream — so waiting for one would look exactly like a peer
      // that has gone away.
      if (!expectAnswer) return new Uint8Array(0);

      const answer = await readFrame(stream, idle);
      if (answer.tag === FrameStatus.Failed) {
        // The handler on the far end failed. That is an answer, not a broken
        // session: retrying would only run it again.
        throw new RemoteHandlerError(str(answer.payload));
      }
      return answer.payload;
    } catch (error) {
      if (error instanceof RemoteHandlerError) throw error;

      // Silence during a request says nothing about the session: a handler on
      // the far end is allowed to take longer than one request window, and
      // condemning the session for it would break every other exchange
      // sharing it and cost a whole reconnect. Deciding that the peer is gone
      // is the heartbeat's job — its ping is answered by the frame loop, so
      // its silence really does mean silence. The caller asks again on this
      // same session and spends another whole window waiting, so this is
      // patience rather than a spin.
      if (idle.expired) throw new Error(`the host sent nothing for ${idle.limitMs} ms`);

      this.close(error);
      throw error;
    } finally {
      idle.stop();
      await stream?.close().catch(() => {});
    }
  }

  /// Lets the answers already produced leave before the session is taken
  /// down. A handler returns its answer well before the answer is on the wire
  /// — sealing a record is asynchronous, and the socket sends what it was
  /// given later still — and closing in that window turns a delivered answer
  /// into silence the host can only resolve by timing the request out.
  ///
  /// Bounded, because a handler of the page's own is allowed to hang and
  /// closing must still happen.
  async drain(timeout) {
    if (this.#answering.size) {
      await Promise.race([Promise.allSettled([...this.#answering]), delay(timeout)]);
    }
    // Written is not sent: waiting only for the handlers leaves the sealed
    // records sitting in the socket's buffer, which closing then throws away.
    await this.#connection.flush(timeout);
  }

  close(error = new Error("the session was closed")) {
    this.#beating = false;
    this.#connection.close(error);
  }

  #answer(stream) {
    // Held so that drain() can wait for it.
    const answering = (async () => {
      try {
        const { tag, exchange, payload } = await readFrame(stream);
        switch (tag) {
          case FrameKind.Ping:
            await writeFrame(stream, FrameStatus.Ok, exchange, new Uint8Array(0));
            break;
          case FrameKind.Notify: {
            // No answer, for the same reason this end does not wait for one.
            const handler = this.#notifyHandler() ?? this.#handler();
            await handler?.(str(payload), payload);
            break;
          }
          case FrameKind.Request: {
            // Through the ledger, so that a request the host is retrying after
            // a session died is answered from what its first arrival produced
            // rather than run a second time.
            const answer = await this.#ledger.answer(exchange, () => this.#runHandler(payload));
            await writeFrame(stream, answer.status, exchange, answer.payload);
            // Announced here rather than where the handler returned, so that
            // it says what it appears to say: the answer left this end. A
            // write that throws reaches "answer-failed" below instead, which
            // it could not if the handler had already claimed success.
            this.#emit("answered", { request: str(payload), length: answer.payload.length });
            break;
          }
          default:
            await writeFrame(stream, FrameStatus.Failed, exchange, utf8(`unexpected frame ${tag}`));
        }
      } catch (error) {
        // The stream went away mid-exchange, which the session notices. The
        // far end does not: an answer that never left looks to it like a peer
        // that said nothing, so this is reported rather than only dropped.
        this.#emit("answer-failed", error);
      } finally {
        await stream?.close().catch(() => {});
      }
    })();
    this.#answering.add(answering);
    answering.finally(() => this.#answering.delete(answering)).catch(() => {});
  }

  /// Runs the application's handler and turns whatever it does into an answer.
  /// Everything it can produce, a refusal included, is an answer the ledger
  /// remembers: a retry is told the same thing rather than running again.
  async #runHandler(payload) {
    try {
      const handler = this.#handler();
      if (!handler) throw new Error("this browser answers no requests");
      const answer = await handler(str(payload), payload);
      const bytes = answer instanceof Uint8Array ? answer : utf8(answer ?? "");
      return { status: FrameStatus.Ok, payload: bytes };
    } catch (error) {
      // Application code threw. The host is waiting and deserves to be told
      // why rather than left to time out, and this link must survive its own
      // handler's bugs.
      return { status: FrameStatus.Failed, payload: utf8(error.message) };
    }
  }

  #startHeartbeat() {
    (async () => {
      while (this.#beating && !this.closed) {
        await delay(this.#options.heartbeatInterval);
        if (!this.#beating || this.closed) return;

        const idle = new IdleTimeout(this.#options.requestTimeout);
        let stream;
        try {
          stream = this.#connection.openStream();
          await writeFrame(stream, FrameKind.Ping, newExchange(), new Uint8Array(0), idle);
          await readFrame(stream, idle);
        } catch (error) {
          // The one exchange whose silence does condemn the session: a ping is
          // answered by the peer's frame loop rather than by application code,
          // so nothing legitimate can make it slow.
          this.close(error);
        } finally {
          idle.stop();
          await stream?.close().catch(() => {});
        }
      }
    })();
  }
}

/// The far end's handler failed. Not a broken session: retrying it would only
/// run it again.
export class RemoteHandlerError extends Error {
  constructor(message) {
    super(message);
    this.name = "RemoteHandlerError";
  }
}

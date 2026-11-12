// The direct path: a relay1 session's records, between the two machines
// themselves.
//
// A page has no UDP socket, so it cannot do what `PeerLink` does for a .NET
// node. What it has is WebRTC, which finds a path with ICE and gives back a
// reliable, ordered data channel. That channel is used as a second way of
// carrying the records the session already seals — not as a transport of its
// own. The session starts on the relay exactly as it always has, the two ends
// arrange the channel through the session's control stream, and each end
// moves its sending over when the channel opens. Nothing above the session
// can tell, and an end that knows none of this ignores the offer and stays
// where it was.
//
// The channel is trusted with nothing. Its DTLS fingerprint crosses inside the
// session, so only the machine holding the session's keys can be at the other
// end of it; and every record on it is still sealed under those keys and
// still counted, so whatever arrives there is opened or ignored exactly as it
// would be from the relay.

import { Deferred, str, utf8, concat } from "./bytes.js";

/// What is said on the control stream: one byte of kind, then text.
export const DirectSignal = {
  /// "Not here": the answer of an end that knows the offer and will not take
  /// it, so the one that asked need not wait out its patience.
  Decline: 0,
  /// The dialler's session description.
  Offer: 1,
  /// The host's.
  Answer: 2,
  /// One ICE candidate, as the JSON of an `RTCIceCandidateInit`.
  Candidate: 3,
  /// How far an end has read what came by the channel: `RECEIVED_SIGNAL` in
  /// relay1.js, which handles it — it is the session's, not the path's.
  Received: 4,
  /// "My end of the path is gone, I am on the relay": sent by the relay by an
  /// end that fell back. The path may be dead only one way, or dead before
  /// the goodbye could cross it, and the other end's ICE took twenty seconds
  /// and more to notice — sending into it all the while.
  Lost: 5,
};

export const encodeSignal = (kind, text = "") => concat(new Uint8Array([kind]), utf8(text));

export const decodeSignal = (payload) =>
  payload.length ? { kind: payload[0], text: str(payload.subarray(1)) } : null;

const CHANNEL_LABEL = "tailcat-relay1";

/// What an end sends on the channel, as text, just before closing it on
/// purpose. Text is for the path itself: everything else is records.
export const LEAVING = "leaving";

/// What an end sends on the channel, as text, when it has sent nothing else
/// there for `ALIVE_MS`. An end that has heard it once takes `SILENT_MS` with
/// nothing at all arriving for the path being gone, and goes back to the
/// relay: ICE's own "disconnected" came 6–7 s after a real path died, plus its
/// grace, and a game stood still for all of it. Only once heard, so an end
/// from before it, which never says it, is not taken for dead when idle.
export const ALIVE = "alive";
const ALIVE_MS = 500;
const SILENT_MS = 2_500;

// How long the goodbye gets to leave before the peer connection is closed.
const LEAVE_MS = 250;

// How long ICE gets. Two machines that can reach each other do it in a second
// or two; the rest is for a slow relay carrying the candidates.
const ESTABLISH_MS = 15_000;

// "disconnected" is ICE saying its checks have stopped being answered, which
// a moment of packet loss also does. Past this it is taken for the path being
// gone — sooner than ICE's own half a minute, during which everything sent
// queues behind a channel that will never deliver it. Short, because taking a
// path for gone costs only a move back to the relay.
const DISCONNECTED_GRACE_MS = 3_000;

// The channel takes whatever it is given and buffers it; past its limit a
// browser closes it. Sending waits between these two instead.
const HIGH_WATER = 1024 * 1024;
const LOW_WATER = 256 * 1024;

// The first wait before a path that failed is offered again; it doubles.
const RETRY_MS = 5_000;

// Asked when an application names none. Without a STUN server only the
// machines' own addresses are tried, which joins two machines on one LAN and
// no others; and a STUN server that is down costs only the direct path — the
// session stays on the relay. It sees both machines' public addresses, which
// is why `iceServers` can name another, or `[]` for none.
export const DEFAULT_ICE_SERVERS = Object.freeze([{ urls: "stun:stun.l.google.com:19302" }]);

/// What the `directPath` option asks for, or null for "relay only".
///
/// @param option `false`, `true`, or `{ iceServers, RTCPeerConnection }`.
///   `iceServers` is WebRTC's own list, Google's STUN server when not given;
///   with `[]`, only addresses on the machines' own networks are tried, and
///   nothing outside either is asked.
///   `RTCPeerConnection` is for where there is no global one — Node.
export function directPathConfig(option) {
  if (!option) return null;
  const given = option === true ? {} : option;
  const RTCPeerConnection = given.RTCPeerConnection ?? globalThis.RTCPeerConnection;
  // Asked for where there is no WebRTC: the link works relayed, which is
  // what it would do against a peer without it.
  if (!RTCPeerConnection) return null;
  return {
    RTCPeerConnection,
    iceServers: given.iceServers ?? DEFAULT_ICE_SERVERS,
    establishMs: given._establishMs ?? ESTABLISH_MS,
    aliveMs: given._aliveMs ?? ALIVE_MS,
    silentMs: given._silentMs ?? SILENT_MS,
    retryMs: given._retryMs ?? RETRY_MS,
  };
}

/// When the dialling end may next offer a direct path. It outlives sessions,
/// which is the point: a path that forms and dies takes the session with it,
/// and offered again on every reconnection it would take each one in turn.
export class DirectPathBackoff {
  static #MAX_MS = 5 * 60_000;
  static #HELD_MS = 60_000;

  #firstMs;
  #failures = 0;
  #notBefore = 0;

  constructor({ firstMs = RETRY_MS } = {}) {
    this.#firstMs = firstMs;
  }

  /// How long until a path may be offered; 0 when it may be now.
  get dueInMs() {
    return Math.max(0, this.#notBefore - Date.now());
  }

  /// @param heldMs how long the path carried the session, or null if it
  ///   never formed.
  /// @param failed whether it was the path that gave out. A session that
  ///   ended for reasons of its own — its relay connection was cut, the link
  ///   was closed — says nothing about the path, and counted as a failure it
  ///   kept a link relayed after every cut of a relay it no longer needed.
  ended({ heldMs, failed }) {
    if (heldMs !== null && heldMs >= DirectPathBackoff.#HELD_MS) {
      this.#failures = 0;
      this.#notBefore = 0;
      return;
    }
    if (!failed) return;
    const wait = Math.min(DirectPathBackoff.#MAX_MS, this.#firstMs * 2 ** Math.min(this.#failures, 8));
    this.#failures++;
    this.#notBefore = Date.now() + wait;
  }
}

/// One attempt at a direct path for one session, from either end.
export class DirectPath {
  #session;
  #config;
  #pc = null;
  #channel = null;
  #remoteSet = false;
  #pendingCandidates = [];
  #deadline = null;
  #grace = null;
  #drained = null;
  // Whether ICE ever connected. From then on the far end may have seen the
  // channel open and moved its sending there, so giving up quietly is no
  // longer possible: its records would arrive nowhere, and only the far end
  // knowing to send them again by the relay keeps the session.
  #connected = false;
  // Whether the other end said it was leaving: see LEAVING.
  #peerLeaving = false;
  #openedAt = null;
  #backOnRelay = false;
  #pulse = null;
  #lastHeard = 0;
  #lastSent = 0;
  #peerPulses = false;
  #state = "idle";

  /// @param onopen called when records start leaving on the channel.
  /// @param onend called once with `{ heldMs, failed, backOnRelay }`: how
  ///   long the path carried the session in milliseconds, or null if it never
  ///   did; whether the path itself gave out rather than the session under
  ///   it; and whether the session carries on by the relay after it had moved.
  constructor({ session, config, onopen = () => {}, onend = () => {} }) {
    this.#session = session;
    this.#config = config;
    this.onopen = onopen;
    this.onend = onend;
    session.onClosed(() => this.#finish());
  }

  get open() {
    return this.#state === "open";
  }

  /// Whether this attempt is over, however it went.
  get ended() {
    return this.#state === "closed";
  }

  /// The dialling end's half: makes the channel and offers it.
  async offer() {
    if (this.#state !== "idle" || this.#session.closed) return;
    try {
      const pc = this.#begin();
      this.#adopt(pc.createDataChannel(CHANNEL_LABEL, { ordered: true }));
      await pc.setLocalDescription(await pc.createOffer());
      await this.#signal(DirectSignal.Offer, pc.localDescription.sdp);
    } catch (error) {
      this.#fail(error);
    }
  }

  /// What the far end said on the control stream. The host is given the
  /// offer here; the dialler the answer; both the candidates.
  async handle(signal) {
    try {
      switch (signal.kind) {
        case DirectSignal.Offer:
          await this.#answer(signal.text);
          return;
        case DirectSignal.Answer:
          // Only to an offer this end made, and only once.
          if (this.#state !== "connecting" || this.#remoteSet || !this.#pc?.localDescription) return;
          await this.#pc.setRemoteDescription({ type: "answer", sdp: signal.text });
          await this.#remoteDescribed();
          return;
        case DirectSignal.Candidate:
          if (this.#state !== "connecting" && this.#state !== "open") return;
          if (this.#remoteSet) await this.#pc.addIceCandidate(JSON.parse(signal.text));
          // The answer and the candidates after it are sent in that order,
          // but each is applied after an await; one that overtakes waits.
          else this.#pendingCandidates.push(signal.text);
          return;
        case DirectSignal.Decline:
          if (this.#state === "connecting") this.#fail(new Error("the other end takes no direct path"));
          return;
        case DirectSignal.Lost:
          // Only for a path that was up: one still being set up is a later
          // attempt, which the signal, sent before it was offered, is not about.
          if (this.#state === "open") this.#fail(new Error("the other end lost the direct path"));
          return;
        default:
          // A kind from a version after this one.
          return;
      }
    } catch (error) {
      this.#fail(error);
    }
  }

  /// Resolves once what was sent on the channel has left it, as
  /// `DerpConnection.flush` does for the relay.
  async flush(timeoutMs) {
    const deadline = Date.now() + timeoutMs;
    while (this.#state === "open" && this.#channel.bufferedAmount > 0) {
      if (Date.now() >= deadline) return false;
      await new Promise((resolve) => setTimeout(resolve, 5));
    }
    return this.#state === "open";
  }

  /// Lets go of the attempt without a word about the session, which is the
  /// caller's to end.
  close() {
    this.#finish();
  }

  async #answer(sdp) {
    if (this.#state !== "idle" || this.#session.closed) return;
    const pc = this.#begin();
    pc.ondatachannel = (event) => {
      // One channel, the one this protocol names; anything else the far end
      // opens is not something this end agreed to read.
      if (event.channel.label === CHANNEL_LABEL && !this.#channel) this.#adopt(event.channel);
      else event.channel.close();
    };
    await pc.setRemoteDescription({ type: "offer", sdp });
    await this.#remoteDescribed();
    await pc.setLocalDescription(await pc.createAnswer());
    await this.#signal(DirectSignal.Answer, pc.localDescription.sdp);
  }

  #begin() {
    this.#state = "connecting";
    // Before anything is offered: the far end may move its sending to the
    // channel the moment it opens there, ahead of what it sent by the relay.
    this.#session.expectTwoPaths();
    const pc = new this.#config.RTCPeerConnection({ iceServers: this.#config.iceServers });
    this.#pc = pc;
    pc.onicecandidate = (event) => {
      if (!event.candidate) return;
      const candidate = event.candidate.toJSON?.() ?? event.candidate;
      this.#signal(DirectSignal.Candidate, JSON.stringify(candidate)).catch(() => {});
    };
    const watch = () => this.#onConnectionState(pc.connectionState ?? pc.iceConnectionState);
    pc.onconnectionstatechange = watch;
    pc.oniceconnectionstatechange = watch;
    this.#deadline = setTimeout(
      () => this.#fail(new Error("no direct path formed in time")),
      this.#config.establishMs,
    );
    this.#deadline.unref?.();
    return pc;
  }

  async #remoteDescribed() {
    this.#remoteSet = true;
    for (const candidate of this.#pendingCandidates.splice(0)) {
      await this.#pc.addIceCandidate(JSON.parse(candidate));
    }
  }

  #adopt(channel) {
    this.#channel = channel;
    channel.binaryType = "arraybuffer";
    channel.bufferedAmountLowThreshold = LOW_WATER;
    channel.onopen = () => this.#opened();
    channel.onmessage = (event) => {
      if (this.#state === "closed") return;
      this.#lastHeard = Date.now();
      // Text is about the path. Any other text is from a version after this
      // one, and means nothing here.
      if (typeof event.data === "string") {
        if (event.data === LEAVING) this.#peerLeaving = true;
        else if (event.data === ALIVE) this.#peerPulses = true;
        return;
      }
      // Not awaited: the session takes records one at a time by itself, and
      // one that will not open is its business to ignore.
      this.#session.handleRecord(new Uint8Array(event.data), { direct: true }).catch(() => {});
    };
    channel.onclose = () => this.#fail(new Error("the direct path closed"));
    channel.onerror = (event) => this.#fail(event?.error ?? new Error("the direct path failed"));
    channel.onbufferedamountlow = () => {
      const drained = this.#drained;
      this.#drained = null;
      drained?.resolve();
    };
    if (channel.readyState === "open") this.#opened();
  }

  #opened() {
    if (this.#state !== "connecting") return;
    clearTimeout(this.#deadline);
    this.#state = "open";
    this.#connected = true;
    this.#openedAt = Date.now();
    this.#lastHeard = this.#lastSent = this.#openedAt;
    this.#pulse = setInterval(() => this.#checkPulse(), this.#config.aliveMs);
    this.#pulse.unref?.();
    this.#session.sendOn({ send: (record) => this.#send(record) });
    this.onopen();
  }

  #checkPulse() {
    if (this.#state !== "open") return;
    const now = Date.now();
    if (this.#peerPulses && now - this.#lastHeard > this.#config.silentMs) {
      this.#fail(new Error("the direct path went silent"));
      return;
    }
    if (now - this.#lastSent < this.#config.aliveMs) return;
    try {
      this.#channel.send(ALIVE);
      this.#lastSent = now;
    } catch {
      // The channel is going; it says so itself.
    }
  }

  async #send(record) {
    while (this.#state === "open" && this.#channel.bufferedAmount > HIGH_WATER) {
      this.#drained ??= new Deferred();
      await this.#drained.promise;
    }
    if (this.#state !== "open") throw new Error("the direct path was lost");
    this.#channel.send(record);
    this.#lastSent = Date.now();
  }

  #onConnectionState(state) {
    if (this.#state === "closed") return;
    clearTimeout(this.#grace);
    this.#grace = null;
    if (state === "connected" || state === "completed") {
      this.#connected = true;
    } else if (state === "failed" || state === "closed") {
      // Before the channel says so, on a real stack: the other end closing
      // its peer connection takes ICE down with it.
      this.#fail(new Error(`the direct path ${state}`));
    } else if (state === "disconnected") {
      this.#grace = setTimeout(
        () => this.#fail(new Error("the direct path stopped answering")),
        DISCONNECTED_GRACE_MS,
      );
      this.#grace.unref?.();
    }
  }

  #signal(kind, text) {
    return this.#session.sendControl(encodeSignal(kind, text));
  }

  // The attempt is over. Before ICE connected nothing was ever sent on the
  // channel by either end, and the session carries on by the relay as if
  // nothing had been tried. After, it carries on there only by sending again
  // what the far end has not owned up to (`leaveDirect`); the far end, seeing
  // the channel go, does the same. Records keep arriving from either path
  // until both have, so the session still expects two.
  //
  // `pathFailed` is false for a channel the other end closed while the path
  // was up: it is closing the session, which ends here too, but is not
  // reported as a path lost — every client that finished normally used to
  // reach a host's log as one — and not counted against the path in the
  // dialler's backoff.
  #fail(error, { pathFailed = !this.#peerLeaving } = {}) {
    if (this.#state === "closed") return;
    if (!pathFailed) error = new Error("the other end closed the direct path");
    const connected = this.#connected;
    // Before the attempt ends, so a send waiting on the channel finds the
    // session already back on the relay.
    const carriesOn = connected && pathFailed && this.#session.leaveDirect();
    this.#backOnRelay = carriesOn;
    if (carriesOn) this.#signal(DirectSignal.Lost).catch(() => {});
    this.#finish(pathFailed);
    if (!connected) {
      // One path again, and the rule for one: a gap is a record lost.
      this.#session.expectTwoPaths(false);
    } else if (!carriesOn) {
      const message = error?.message ?? "closed";
      this.#session.close(new Error(pathFailed ? `the direct path was lost: ${message}` : message));
    }
  }

  #finish(failed = false) {
    if (this.#state === "closed") return;
    const wasBegun = this.#state !== "idle";
    this.#state = "closed";
    clearTimeout(this.#deadline);
    clearInterval(this.#pulse);
    clearTimeout(this.#grace);
    const drained = this.#drained;
    this.#drained = null;
    drained?.resolve();
    const channel = this.#channel;
    // Said before closing, so the other end can tell an end it chose from a
    // path that broke: ordered, it arrives after every record. Telling them
    // apart by ICE's state was right on a test double and wrong on a real
    // stack, where closing the peer connection takes ICE down first.
    let leaving = false;
    if (!failed && channel?.readyState === "open") {
      try {
        channel.send(LEAVING);
        leaving = true;
      } catch {
        // Closing anyway.
      }
    }
    if (channel) {
      channel.onopen = channel.onmessage = channel.onclose = channel.onerror = null;
      // The channel at once, which goes out behind the goodbye: a page closing
      // its link on its way out may not live for the delay below, and the
      // other end should hear now, not when ICE gives up half a minute later.
      if (leaving) {
        try {
          channel.close();
        } catch {
          // Closing anyway.
        }
      }
    }
    const pc = this.#pc;
    if (pc) {
      pc.onicecandidate = pc.onconnectionstatechange = pc.oniceconnectionstatechange = pc.ondatachannel = null;
      const close = () => {
        try {
          pc.close();
        } catch {
          // Already closed, which is what was wanted.
        }
      };
      // A moment for the goodbye to leave: closing at once raced it, and the
      // other end heard its ICE go down before the message that explained it.
      if (leaving) setTimeout(close, LEAVE_MS).unref?.();
      else close();
    }
    if (wasBegun) {
      const heldMs = this.#openedAt === null ? null : Date.now() - this.#openedAt;
      this.onend({ heldMs, failed, backOnRelay: this.#backOnRelay });
    }
  }
}

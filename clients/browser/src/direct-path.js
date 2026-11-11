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
};

export const encodeSignal = (kind, text = "") => concat(new Uint8Array([kind]), utf8(text));

export const decodeSignal = (payload) =>
  payload.length ? { kind: payload[0], text: str(payload.subarray(1)) } : null;

const CHANNEL_LABEL = "tailcat-relay1";

// How long ICE gets. Two machines that can reach each other do it in a second
// or two; the rest is for a slow relay carrying the candidates.
const ESTABLISH_MS = 15_000;

// "disconnected" is ICE saying its checks have stopped being answered, which
// a moment of packet loss also does. Past this it is taken for the path being
// gone — sooner than ICE's own half a minute, during which everything sent
// queues behind a channel that will never deliver it.
const DISCONNECTED_GRACE_MS = 3_000;

// The channel takes whatever it is given and buffers it; past its limit a
// browser closes it. Sending waits between these two instead.
const HIGH_WATER = 1024 * 1024;
const LOW_WATER = 256 * 1024;

// The first wait before a path that failed is offered again; it doubles.
const RETRY_MS = 5_000;

/// What the `directPath` option asks for, or null for "relay only".
///
/// @param option `false`, `true`, or `{ iceServers, RTCPeerConnection }`.
///   `iceServers` is WebRTC's own list; with none, only addresses on the
///   machines' own networks are tried, and nothing outside either is asked.
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
    iceServers: given.iceServers ?? [],
    establishMs: given._establishMs ?? ESTABLISH_MS,
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
  // longer possible: its records would arrive nowhere.
  #connected = false;
  #openedAt = null;
  #state = "idle";

  /// @param onopen called when records start leaving on the channel.
  /// @param onend called once with `{ heldMs, failed }`: how long the path
  ///   carried the session in milliseconds, or null if it never did, and
  ///   whether the path itself gave out rather than the session under it.
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
      // Text is nothing this protocol sends.
      if (typeof event.data === "string" || this.#state === "closed") return;
      // Not awaited: the session takes records one at a time by itself, and
      // one that will not open is its business to ignore.
      this.#session.handleRecord(new Uint8Array(event.data)).catch(() => {});
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
    this.#session.sendOn({ send: (record) => this.#send(record) });
    this.onopen();
  }

  async #send(record) {
    while (this.#state === "open" && this.#channel.bufferedAmount > HIGH_WATER) {
      this.#drained ??= new Deferred();
      await this.#drained.promise;
    }
    if (this.#state !== "open") throw new Error("the direct path was lost");
    this.#channel.send(record);
  }

  #onConnectionState(state) {
    if (this.#state === "closed") return;
    clearTimeout(this.#grace);
    this.#grace = null;
    if (state === "connected" || state === "completed") {
      this.#connected = true;
    } else if (state === "failed" || state === "closed") {
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
  // nothing had been tried. After, it cannot: see `#connected`.
  #fail(error) {
    if (this.#state === "closed") return;
    const sessionGoesToo = this.#connected;
    this.#finish(true);
    if (sessionGoesToo) {
      this.#session.close(new Error(`the direct path was lost: ${error?.message ?? "closed"}`));
    } else {
      // One path again, and the rule for one: a gap is a record lost.
      this.#session.expectTwoPaths(false);
    }
  }

  #finish(failed = false) {
    if (this.#state === "closed") return;
    const wasBegun = this.#state !== "idle";
    this.#state = "closed";
    clearTimeout(this.#deadline);
    clearTimeout(this.#grace);
    const drained = this.#drained;
    this.#drained = null;
    drained?.resolve();
    const channel = this.#channel;
    if (channel) channel.onopen = channel.onmessage = channel.onclose = channel.onerror = null;
    const pc = this.#pc;
    if (pc) {
      pc.onicecandidate = pc.onconnectionstatechange = pc.oniceconnectionstatechange = pc.ondatachannel = null;
      try {
        pc.close();
      } catch {
        // Already closed, which is what was wanted.
      }
    }
    if (wasBegun) this.onend({ heldMs: this.#openedAt === null ? null : Date.now() - this.#openedAt, failed });
  }
}

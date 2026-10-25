// The listening end of the relay: where machines that dial this host arrive.
//
// `@tailcat/link` dials, so one WebSocket there carries one session. A host
// is dialled by several machines through one relay connection, and answers
// each in the region it says it listens in — a .NET machine far from this
// one listens somewhere else, and an answer sent into the host's own region
// would reach nobody. So this keeps the home connection up for good, opens
// others as answers need them, and routes what arrives by the sender's key.
//
// What it does with a hello is `TailcatNode.AnswerHelloAsync`, restricted to
// relay1: there is no QUIC in a page, so every session a host here holds is
// one the relay carries. A .NET machine that offers QUIC first still agrees,
// because it offers relay1 after it and the first transport both ends have
// wins.

import { Deferred, Queue, delay, hex, readU64be, withTimeout } from "@tailcat/link/internal/bytes.js";
import {
  PeerMessageType,
  PeerTransport,
  decodeHello,
  encodeHello,
  isPeerMessage,
  open as openPeerMessage,
  peerMessageType,
  seal,
} from "@tailcat/link/internal/peer.js";
import { Relay1Session, deriveKeys } from "@tailcat/link/internal/relay1.js";
import { nacl } from "@tailcat/link/internal/nacl.js";

// Sessions handed up and not yet taken. A stranger who knows the address can
// open as many as it has keys, and each waits for a handshake; past this they
// are refused at the door rather than queued without end, as the .NET node's
// bounded inbound queue does.
const MAX_WAITING = 64;

// Machines whose hello is being answered at once. Each is cheap until it
// waits on a relay connection to another region, and a stranger has as many
// keys as it cares to make; past this a hello is dropped, and a real dialler
// sends it again half a second later.
const MAX_HELLOS_AT_ONCE = 256;

// What a session may deliver before its pairing handshake has let it in: the
// hello frame, with room for the records around it. Anyone who knows the
// address can open a session, and relay1 has no limit of its own on how many
// streams or bytes it buffers for a reader that never reads them. Counted
// only for records that open: a machine whose relay connection was cut resends
// what it sent lately, the session before's records among them, and those are
// ignored rather than held.
const BYTES_BEFORE_PAIRING = 64 * 1024;

// Records not yet opened, before pairing. They cannot be counted against the
// bound above until they are, and a stranger can send them faster than they
// open; past this they are dropped, which before pairing costs nothing.
const UNOPENED_BEFORE_PAIRING = 1024 * 1024;

/// One session a relay carries, as the link layer wants to see it: streams,
/// the ones the peer opened, and a way to give up on it. The relay connection
/// under it is shared with every other peer, so closing this closes only this.
export class HostedConnection {
  #session;
  #relay;
  #inbound = Promise.resolve();
  #untrusted = BYTES_BEFORE_PAIRING;
  #trusted = false;
  #unopened = 0;
  #ignored = 0;

  constructor({ session, relay, peer }) {
    this.#session = session;
    this.#relay = relay;
    session.onignored = () => this.#ignored++;
    /// The dialling machine's public key: the only thing about it a session
    /// authenticates.
    this.peer = peer;
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

  /// Resolves once what was written has left the socket.
  flush(timeoutMs) {
    return this.#relay.flush(timeoutMs);
  }

  close(error = new Error("the session was closed")) {
    this.#session.close(error);
  }

  /// Lifts the bound on what the peer may send, once pairing has let it in.
  /// Called before the answer goes out, so nothing it sends after hearing
  /// "yes" can arrive while the bound still holds.
  trust() {
    this.#trusted = true;
  }

  // One at a time: `handleRecord` checks a counter, and two running at once
  // would report a gap the relay never made.
  _deliver(record) {
    if (this.#trusted) {
      this.#inbound = this.#inbound.then(() => this.#session.handleRecord(record)).catch(() => {});
      return;
    }
    if (this.#unopened + record.length > UNOPENED_BEFORE_PAIRING) return;
    this.#unopened += record.length;
    this.#inbound = this.#inbound
      .then(async () => {
        const ignored = this.#ignored;
        try {
          await this.#session.handleRecord(record);
        } finally {
          this.#unopened -= record.length;
        }
        if (this.#trusted || this.#ignored !== ignored) return;
        this.#untrusted -= record.length;
        if (this.#untrusted < 0) {
          this.close(new Error("the peer sent more than a pairing handshake before it was let in"));
        }
      })
      .catch(() => {});
  }
}

export class RelayHub {
  #identity;
  #homeRegionId;
  #connect;
  #options;
  #emit;
  #home = null;
  #homeUp = new Deferred();
  #elsewhere = new Map();
  // Hellos being answered into each region, which a connection must outlive
  // as much as the sessions it carries.
  #answering = new Map();
  #sessions = new Map();
  #hellos = new Map();
  // Places taken by hellos between the check against MAX_WAITING and the
  // session reaching `incoming`: the key derivation in between is awaited, and
  // hellos from different keys run side by side.
  #reserved = 0;
  #closing = new AbortController();
  #closed = false;

  /// @param identity this host's key pair; its public half is the address.
  /// @param connectRelay `({ regionId, identity, signal }) => DerpConnection`:
  ///   the seam a test replaces to stand a host up with no socket, the same
  ///   one `@tailcat/link` takes for joining.
  /// @param emit `(name, detail)`, for what an operator may want to see.
  constructor({ identity, homeRegionId, connectRelay, options, emit = () => {} }) {
    this.#identity = identity;
    this.#homeRegionId = homeRegionId;
    this.#connect = connectRelay;
    this.#options = options;
    this.#emit = emit;
    /// Sessions that have finished the relay's handshake and wait for the
    /// pairing one: `HostedConnection`s, in the order they arrived.
    this.incoming = new Queue();
  }

  get homeRegionId() {
    return this.#homeRegionId;
  }

  /// Starts keeping the home connection up, and resolves once it first is.
  async start() {
    this.#keepHome();
    await this.#homeUp.promise;
  }

  close() {
    if (this.#closed) return;
    this.#closed = true;
    this.#closing.abort(new Error("the host was closed"));
    const error = new Error("the host was closed");
    for (const { connection } of this.#sessions.values()) connection.close(error);
    this.#sessions.clear();
    this.incoming.close(error);
    this.#home?.close();
    for (const pending of this.#elsewhere.values()) pending.then((relay) => relay.close()).catch(() => {});
    this.#elsewhere.clear();
    if (!this.#homeUp.settled) this.#homeUp.reject(error);
  }

  // The connection a host's address names. Kept up for as long as the host
  // runs, because a machine that cannot reach it cannot reach the host at all.
  async #keepHome() {
    let failures = 0;
    while (!this.#closed) {
      let ended;
      try {
        const relay = await this.#connectTo(this.#homeRegionId);
        if (this.#closed) {
          relay.close();
          return;
        }
        this.#home = relay;
        failures = 0;
        if (!this.#homeUp.settled) this.#homeUp.resolve();
        this.#emit("relay-connected", { regionId: this.#homeRegionId });
        ended = await this.#pump(relay);
      } catch (error) {
        if (this.#closed) return;
        failures++;
        ended = error;
        // The first attempt is the one `start` is waiting on, and a host that
        // cannot reach its relay at all should say so rather than hang.
        if (!this.#homeUp.settled && failures >= 3) this.#homeUp.reject(error);
      }
      this.#home = null;
      if (this.#closed) return;

      // Everything a peer sends arrives here, whichever region it is answered
      // in, so every session went with the connection. The machines at the
      // other end dial again; relay1 has no resend to carry a session over.
      this.#endSessions(() => true, new Error(`the relay connection was lost: ${ended?.message ?? "closed"}`));
      this.#emit("relay-lost", { regionId: this.#homeRegionId, error: ended });

      const wait = failures === 0
        ? this.#options.minReconnectDelay
        : Math.min(this.#options.maxReconnectDelay, this.#options.minReconnectDelay * 2 ** Math.min(failures, 8));
      await this.#pause(wait);
    }
  }

  // A connection to a region other than home, for answering a machine that
  // listens there. Opened on demand, and closed when it fails or nothing uses
  // it any more; the next hello from that region opens another.
  #relayFor(regionId) {
    if (regionId === this.#homeRegionId) {
      if (!this.#home) return Promise.reject(new Error("the relay connection is down"));
      return Promise.resolve(this.#home);
    }
    let pending = this.#elsewhere.get(regionId);
    if (!pending) {
      // Bounded, because every hello for the region waits on it: a relay that
      // never answers would hold their places for as long as the host runs,
      // and a stranger can name any region it likes. One that answers late
      // is closed on arrival.
      const connecting = this.#connectTo(regionId);
      pending = withTimeout(connecting, this.#options.handshakeTimeout, `the relay of region ${regionId}`);
      pending.catch(() => connecting.then((relay) => relay.close()).catch(() => {}));
      this.#elsewhere.set(regionId, pending);
      pending.then(
        async (relay) => {
          const ended = await this.#pump(relay);
          if (this.#elsewhere.get(regionId) === pending) this.#elsewhere.delete(regionId);
          this.#endSessions(
            // Only this connection's: one opened since, for the same region,
            // carries sessions of its own, and its reader may end later.
            (entry) => entry.relay === relay,
            new Error(`the connection to region ${regionId} was lost: ${ended?.message ?? "closed"}`),
          );
        },
        () => {
          if (this.#elsewhere.get(regionId) === pending) this.#elsewhere.delete(regionId);
        },
      );
    }
    return pending;
  }

  // Kept only while a session or a hello in progress uses it: anyone who knows
  // the address can send hellos naming every region in the map, and without
  // this the host would hold a connection to each of them for good.
  #releaseIfUnused(regionId) {
    if (regionId === this.#homeRegionId || this.#answering.get(regionId)) return;
    for (const entry of this.#sessions.values()) if (entry.regionId === regionId) return;
    const pending = this.#elsewhere.get(regionId);
    if (!pending) return;
    this.#elsewhere.delete(regionId);
    pending.then((relay) => relay.close()).catch(() => {});
  }

  // Ended by closing as well as by time: a timer left running would hold a
  // Node process open for up to the whole backoff after the host was closed.
  #pause(ms) {
    const signal = this.#closing.signal;
    if (signal.aborted) return Promise.resolve();
    return new Promise((resolve) => {
      const done = () => {
        clearTimeout(timer);
        signal.removeEventListener("abort", done);
        resolve();
      };
      const timer = setTimeout(done, ms);
      signal.addEventListener("abort", done);
    });
  }

  #connectTo(regionId) {
    return Promise.resolve(this.#connect({ regionId, identity: this.#identity, signal: this.#closing.signal }));
  }

  // Reads one relay connection until it ends, and returns why.
  async #pump(relay) {
    for (;;) {
      let packet;
      try {
        packet = await relay.packets.next();
      } catch (error) {
        return error;
      }
      this.#onPacket(packet.source, packet.payload);
    }
  }

  #onPacket(source, payload) {
    if (!isPeerMessage(payload)) return;
    switch (peerMessageType(payload)) {
      case PeerMessageType.Hello:
        this.#queueHello(source, payload);
        return;
      case PeerMessageType.Relay1Record:
        // From the sender's session only: a record from anybody else cannot
        // open, and must not cost the session that can.
        this.#sessions.get(hex(source))?.connection._deliver(payload);
        return;
      default:
        // An answer to a hello this host never sent, or a QUIC path's traffic
        // from a .NET machine before it learned relay1 was agreed.
        return;
    }
  }

  // One hello per peer at a time. A dialler resends its hello every half
  // second, and over a slow relay the copies overlap: answered side by side,
  // two copies built two sessions under two keys, and the dialler got one
  // nothing it sent could open. TailcatNode takes a per-peer lock for this.
  // Behind the one being answered waits only the newest: it is what the
  // dialler wants now, and a queue of every copy would grow for as long as
  // an answer is stuck.
  #queueHello(source, message) {
    const key = hex(source);
    const queued = this.#hellos.get(key);
    if (queued) {
      queued.next = message;
      return;
    }
    if (this.#hellos.size >= MAX_HELLOS_AT_ONCE) return;
    const entry = { next: message };
    this.#hellos.set(key, entry);
    (async () => {
      while (entry.next && !this.#closed) {
        const next = entry.next;
        entry.next = null;
        try {
          await this.#answerHello(source, key, next);
        } catch (error) {
          if (!this.#closed) this.#emit("handshake-failed", { peer: key, error });
        }
      }
      this.#hellos.delete(key);
    })();
  }

  async #answerHello(source, key, message) {
    if (this.#closed) return;
    const opened = openPeerMessage(message, this.#identity.secretKey, source);
    // Sealed to somebody else, or forged: not a hello this host was sent.
    if (!opened) return;
    const hello = decodeHello(opened.payload);
    const sessionId = readU64be(opened.payload, 0);

    // Into the region the dialler listens in, which is ours only when the
    // two happen to be near each other.
    const regionId = hello.homeRegionId || this.#homeRegionId;
    this.#answering.set(regionId, (this.#answering.get(regionId) ?? 0) + 1);
    try {
      await this.#answerInto(regionId, source, key, hello, sessionId);
    } finally {
      const left = this.#answering.get(regionId) - 1;
      if (left) this.#answering.set(regionId, left);
      else this.#answering.delete(regionId);
      this.#releaseIfUnused(regionId);
    }
  }

  async #answerInto(regionId, source, key, hello, sessionId) {
    const relay = await this.#relayFor(regionId);

    if (!hello.transports.includes(PeerTransport.Relay1)) {
      // Told what this end speaks rather than dropped: one relayed message
      // turns what would be a handshake timeout into an error naming the cause.
      this.#emit("handshake-failed", {
        peer: key,
        error: new Error(`no transport in common: the peer offered [${hello.transports.join(", ")}], this host speaks [relay1]`),
      });
      this.#sendAck(relay, source, sessionId, null);
      return;
    }

    // A repeat of a hello already answered is the dialler's retry, because
    // the answer was lost or slow. It gets the same answer — the same key —
    // or it builds a session that cannot open a single record of ours.
    const existing = this.#sessions.get(key);
    if (existing && existing.sessionId === sessionId) {
      this.#sendAck(relay, source, sessionId, existing.ephemeral.publicKey);
      return;
    }

    if (!hello.ephemeral) throw new Error("the peer agreed to relay1 without sending an ephemeral key");
    if (this.incoming.length + this.#reserved >= MAX_WAITING) {
      throw new Error("too many sessions are waiting for a pairing handshake");
    }

    const ephemeral = nacl.box.keyPair();
    let keys;
    this.#reserved++;
    try {
      keys = await deriveKeys({
        ephemeralPrivate: ephemeral.secretKey,
        peerEphemeral: hello.ephemeral,
        sessionId,
        // The dialler is whoever sent the hello; which end is which decides
        // which direction's key each uses, and is not negotiable.
        dialerPublic: source,
        hostPublic: this.#identity.publicKey,
      });
    } finally {
      this.#reserved--;
    }
    if (this.#closed) return;

    const session = new Relay1Session({ derp: relay, peerPublic: source, keys, isDialer: false });
    const connection = new HostedConnection({ session, relay, peer: source });
    const entry = { sessionId, ephemeral, connection, regionId, relay };
    session.onclose = () => {
      if (this.#sessions.get(key) === entry) this.#sessions.delete(key);
      this.#releaseIfUnused(regionId);
    };

    // A new session from a machine that already had one is that machine
    // having given up on the old: nothing will arrive on it again.
    existing?.connection.close(new Error("the peer dialled again"));
    // In place before the answer goes out: the dialler's first record follows
    // the answer at once, and one arriving before this would be dropped.
    this.#sessions.set(key, entry);
    this.#sendAck(relay, source, sessionId, ephemeral.publicKey);
    this.incoming.push(connection);
  }

  #sendAck(relay, peer, sessionId, ephemeral) {
    const ack = encodeHello({
      sessionId,
      homeRegionId: this.#homeRegionId,
      transports: [PeerTransport.Relay1],
      ephemeral,
    });
    relay.sendPacket(peer, seal(PeerMessageType.HelloAck, ack, this.#identity.secretKey, peer));
  }

  #endSessions(which, error) {
    for (const [key, entry] of [...this.#sessions]) {
      if (!which(entry)) continue;
      this.#sessions.delete(key);
      entry.connection.close(error);
    }
  }
}

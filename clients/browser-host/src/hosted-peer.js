// One machine a host is paired with.
//
// Underneath is the same `TailcatLink` a page holds when it joins: requests
// that wait through reconnections, exchanges resumed from where each end got
// to, the heartbeat, channels and streams. What differs is only where its
// sessions come from — handed in by the host once the pairing handshake let
// them through, rather than dialled — which is the split `LinkHost.cs` makes
// with one `LinkPeer` for both ends. Two copies of that loop would drift, and
// the drift would be in what a host promises.

import { Deferred, withAbort } from "@tailcat/link/internal/bytes.js";
import { TailcatLink } from "@tailcat/link/internal/link.js";

/// Sessions handed in by the host. `OfferedSessionSource.cs` is the .NET half.
export class OfferedSessionSource {
  #offered = [];
  #closed = null;
  // Woken by every offer and by closing, whoever is waiting for either.
  #arrived = new Deferred();

  offer(connection) {
    if (this.#closed) {
      connection.close(this.#closed);
      return;
    }
    this.#offered.push(connection);
    this.#wake();
  }

  async nextSession(signal) {
    while (!this.#offered.length) {
      if (this.#closed) throw this.#closed;
      await withAbort(this.#arrived.promise, signal);
    }
    // A peer that dialled twice while this end was busy is asking on its
    // newest connection; the older ones are already stale, and serving one
    // would leave the peer waiting on a session nobody reads.
    const newest = this.#offered.pop();
    for (const stale of this.#offered.splice(0)) stale.close(new Error("the peer dialled again"));
    return newest;
  }

  /// Not a backoff: on a host the session is handed in already paired, and
  /// nobody serves it until the loop takes it. A pause here is dead time in
  /// which the machine at the other end is connected, asking, and hearing
  /// nothing — long enough, and it gives up and dials again inside every
  /// pause. So the pause ends the moment a session is offered.
  async pause(ms, signal) {
    if (this.#offered.length || this.#closed) return;
    let timer;
    const elapsed = new Promise((resolve) => {
      timer = setTimeout(resolve, ms);
    });
    try {
      await withAbort(Promise.race([elapsed, this.#arrived.promise]), signal);
    } catch {
      // Closed while pausing: the loop sees it is stopped and goes.
    } finally {
      clearTimeout(timer);
    }
  }

  close() {
    this.#closed = new Error("the peer was closed");
    for (const spare of this.#offered.splice(0)) spare.close(this.#closed);
    this.#wake();
  }

  #wake() {
    const arrived = this.#arrived;
    this.#arrived = new Deferred();
    arrived.resolve();
  }
}

/// A paired machine, as the application sees it: who it is, whether it is
/// there, and everything a joined link can do — in the other direction.
export class LinkPeer {
  #link;
  #source;
  #remembered;
  #connected = false;

  constructor({ remembered, options, emit }) {
    this.#remembered = remembered;
    this.#source = new OfferedSessionSource();
    this.#link = TailcatLink._fromSource({
      options,
      source: this.#source,
      // A host's peer is never refused — only a joining end is — so the store
      // the link would drop a refused pairing from is never asked.
      store: { remove: async () => {} },
    });
    this.events = this.#link.events;
    this.events.addEventListener("connected", () => {
      this.#connected = true;
      emit("peer-joined", { peer: this });
    });
    this.events.addEventListener("disconnected", (event) => {
      this.#connected = false;
      emit("peer-left", { peer: this, error: event.detail });
    });
  }

  /// The machine's public key, in hex: the one thing about it a session
  /// authenticates, and what the host knows it by.
  get key() {
    return this.#remembered.key;
  }

  /// What the machine called itself when it joined. A hint from the other
  /// machine, authenticated by nothing: never key anything off it.
  get name() {
    return this.#remembered.name;
  }

  get pairedAt() {
    return new Date(this.#remembered.pairedAt);
  }

  get lastSeen() {
    return new Date(this.#remembered.lastSeen);
  }

  get connected() {
    return this.#connected && this.#link.connected;
  }

  /// Asks the machine something; see `TailcatLink.request`. Waits through
  /// reconnections, and the machine's handler runs once.
  request(message, options) {
    return this.#link.request(message, options);
  }

  notify(message, options) {
    return this.#link.notify(message, options);
  }

  send(message, options) {
    return this.#link.send(message, options);
  }

  openChannel(name, options) {
    return this.#link.openChannel(name, options);
  }

  openStream(name, options) {
    return this.#link.openStream(name, options);
  }

  waitUntilConnected(timeout) {
    return this.#link.waitUntilConnected(timeout);
  }

  // ---- for the host ------------------------------------------------------

  /// The link the host registers its handlers on.
  get _link() {
    return this.#link;
  }

  _remember(remembered) {
    this.#remembered = remembered;
  }

  _offer(connection) {
    this.#source.offer(connection);
  }

  async _close(options) {
    this.#source.close();
    await this.#link.close(options);
  }
}

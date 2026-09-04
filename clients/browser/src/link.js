// The durable link: what a page holds on to.
//
// A session dies for many reasons — the relay went away, the tab was
// backgrounded, the host rebooted, the network changed underneath — and from
// here they all look the same and all get the same answer. Nothing above this
// is told; a request simply waits for the next session and is re-sent with
// the id it already had, which is what stops the far end running it twice.
//
// Only the reconnection lives here. What one session does is `link-session.js`
// and where a session comes from is `session-source.js`, the same split
// `DurableLink.cs` keeps on the .NET side.

import { Deferred, delay, str, utf8, withTimeout } from "./bytes.js";
import { parseAddress, parseInvitationCode } from "./address.js";
import { FrameKind, ensureSendable, newExchange } from "./link-frame.js";
import { DialingSessionSource, relayDialer } from "./session-source.js";
import { PairingRefusedError } from "./pairing-handshake.js";
import { LinkSession, RemoteHandlerError } from "./link-session.js";
import { ExchangeLedger } from "./exchange-ledger.js";
import { IndexedDbStore } from "./store.js";
import { nacl } from "./nacl.js";

const DEFAULTS = {
  derpMap: "/derpmap.json",
  requestTimeout: 30_000,
  requestDeadline: 90_000,
  heartbeatInterval: 20_000,
  minReconnectDelay: 500,
  maxReconnectDelay: 30_000,
  handshakeTimeout: 20_000,
};

export class TailcatLink {
  #options;
  #source;
  #store;
  #handler = null;
  #notifyHandler = null;
  #session = null;
  #connected = new Deferred();
  #stopped = false;
  #fatal = null;
  #closing = new AbortController();
  #ledger;

  constructor({ options, source, store }) {
    this.#options = options;
    // Longer than the far end may keep retrying one request, so that its last
    // retry still meets the answer its first attempt produced. `LinkProtocol`
    // ties the two together the same way, from the other side.
    this.#ledger = new ExchangeLedger({ retention: options.requestDeadline + 30_000 });
    this.#source = source;
    this.#store = store;
    this.events = new EventTarget();
  }

  /// Brings up the end that connects to a host. The invitation code is needed
  /// the first time only; afterwards it is stored, and joining again needs
  /// nothing from anybody.
  static async join({ appName, invitationCode = null, ...rest }) {
    if (!appName) throw new Error("a link needs an appName to store its identity under");
    const options = { ...DEFAULTS, ...rest };
    const store = options.store ?? IndexedDbStore;

    const saved = (await store.load(appName)) ?? {};
    const identity = saved.privateKey ? nacl.box.keyPair.fromSecretKey(saved.privateKey) : nacl.box.keyPair();

    let peer;
    let pairingToken;
    if (invitationCode) {
      const code = parseInvitationCode(invitationCode);
      peer = code.address;
      pairingToken = code.pairingToken;
    } else if (saved.peerAddress) {
      peer = parseAddress(saved.peerAddress);
      pairingToken = saved.pairingToken;
    } else {
      throw new Error(`this browser has not been paired for "${appName}" yet; pass the host's invitation code once`);
    }

    await store.save(appName, {
      privateKey: identity.secretKey,
      peerAddress: peer.value,
      pairingToken,
    });

    const source = new DialingSessionSource({
      // `dial` is the seam a test replaces to stand the whole link up without
      // a socket, as TailcatNodeOptions.ConnectRelay does on the .NET side.
      dial: options.dial ?? relayDialer({ options, identity, peer }),
      pairingToken,
      handshakeTimeout: options.handshakeTimeout,
    });

    const link = new TailcatLink({ options: { ...options, appName }, source, store });
    link.#run();
    return link;
  }

  /// Forgets the identity and the pairing, so the next join demands a code.
  static async forget(appName, store = IndexedDbStore) {
    await store.remove(appName);
  }

  /// Answers whatever the host asks. A notification arrives here too, as a
  /// request whose answer is thrown away.
  onRequest(handler) {
    this.#handler = handler;
  }

  /// Called for messages the host sent without expecting an answer.
  onNotify(handler) {
    this.#notifyHandler = handler;
  }

  get connected() {
    return this.#session !== null && !this.#session.closed;
  }

  /// Resolves once a session is up. Sending does not need it — a request
  /// waits by itself — but a page that wants to show a state does.
  async waitUntilConnected(timeout = this.#options.requestDeadline) {
    if (this.connected) return;
    await withTimeout(this.#connected.promise, timeout, "the host");
  }

  async request(message, { timeout } = {}) {
    const payload = typeof message === "string" ? utf8(message) : message;
    const answer = await this.#exchange(FrameKind.Request, payload, timeout);
    return typeof message === "string" ? str(answer) : answer;
  }

  async notify(message, { timeout } = {}) {
    const payload = typeof message === "string" ? utf8(message) : message;
    await this.#exchange(FrameKind.Notify, payload, timeout, { expectAnswer: false });
  }

  /// Stops the link, but not before the answers already produced have left.
  async close({ drainTimeout = this.#options.requestTimeout } = {}) {
    this.#stopped = true;
    // A connect in flight owns a connection this method cannot see yet — the
    // session is assigned only once the source has produced one — so the
    // attempt itself is abandoned rather than left to finish into a link
    // nobody holds.
    this.#closing.abort(new Error("the link was closed"));

    const session = this.#session;
    await session?.drain(drainTimeout);
    session?.close(new Error("the link was closed"));
  }

  // ---- the exchange, across as many sessions as it takes ---------------

  async #exchange(kind, payload, timeout, { expectAnswer = true } = {}) {
    // Before the retry loop: a payload over the cap is refused by every
    // session alike, and inside the loop it would be retried until the
    // deadline and then reported as silence rather than as the caller's own
    // mistake.
    ensureSendable(payload);

    const exchange = newExchange(); // Kept across retries; that is the point.
    const deadline = Date.now() + (timeout ?? this.#options.requestDeadline);

    let lastError;
    for (;;) {
      if (this.#stopped) throw this.#fatal ?? new Error("the link is closed");
      if (Date.now() > deadline) {
        throw new Error(`the host did not answer within the deadline${lastError ? `: ${lastError.message}` : ""}`);
      }

      let session;
      try {
        session = await withTimeout(this.#sessionAsync(), Math.max(1, deadline - Date.now()), "the host");
      } catch (error) {
        lastError = error;
        continue;
      }

      try {
        return await session.exchange(kind, exchange, payload, { expectAnswer });
      } catch (error) {
        // A handler that failed is an answer: retrying would only run it again.
        if (error instanceof RemoteHandlerError) throw error;
        lastError = error;
      }
    }
  }

  async #sessionAsync() {
    for (;;) {
      if (this.#session && !this.#session.closed) return this.#session;
      if (this.#stopped) throw this.#fatal ?? new Error("the link is closed");
      await this.#connected.promise;
    }
  }

  // ---- the loop that keeps a session up -------------------------------

  async #run() {
    let attempt = 0;
    while (!this.#stopped) {
      try {
        const connection = await this.#source.nextSession(this.#closing.signal);
        // close() while a connect was running found no session and so closed
        // nothing; without this check the connection just made would be
        // served forever, holding the relay open for a link nobody holds.
        if (this.#stopped) {
          connection.close(new Error("the link was closed"));
          return;
        }

        this.#session = this.#openSession(connection);
        attempt = 0;
        if (!this.#connected.settled) this.#connected.resolve();
        this.#emit("connected");
        await this.#session.serve();
      } catch (error) {
        if (error instanceof PairingRefusedError) await this.#giveUp(error);
        else if (!this.#stopped) this.#emit("disconnected", error);
      }

      this.#session?.close();
      this.#session = null;
      // Not after a refusal: that rejection is the answer every waiter is
      // owed, and replacing it with a fresh Deferred would park them instead.
      if (this.#connected.settled && !this.#fatal) this.#connected = new Deferred();

      if (this.#stopped) return;
      const wait = Math.min(
        this.#options.maxReconnectDelay,
        this.#options.minReconnectDelay * 2 ** Math.min(attempt++, 8),
      );
      await delay(wait);
    }
  }

  // The handlers are passed as functions rather than values so that a page
  // that calls onRequest after connecting still answers.
  #openSession(connection) {
    return new LinkSession({
      connection,
      handler: () => this.#handler,
      notifyHandler: () => this.#notifyHandler,
      ledger: this.#ledger,
      options: this.#options,
      emit: (name, detail) => this.#emit(name, detail),
    });
  }

  // A refused pairing is not a session that broke: the code is wrong or spent,
  // and only a new one can help. Reconnecting on the usual schedule would ask
  // the host the same rejected question every half minute while every caller
  // here saw nothing but a deadline, so the link stops and says why.
  async #giveUp(error) {
    this.#stopped = true;
    this.#fatal = error;
    // join() stores the code before anything has tried it, so a refused one is
    // already on disk and would be offered again on the next load of the page.
    try {
      await this.#store.remove(this.#options.appName);
    } catch {
      // The pairing outliving its refusal is worth less than the failure that
      // is about to be reported.
    }
    if (!this.#connected.settled) this.#connected.reject(error);
    this.#emit("failed", error);
  }

  #emit(name, detail) {
    this.events.dispatchEvent(new CustomEvent(name, { detail }));
  }
}

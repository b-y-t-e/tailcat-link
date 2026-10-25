// A machine that publishes invitations and holds the machines that accept
// them: `ILinkHost` / `LinkHost.cs`, for a page or for Node.
//
// Everything per-machine lives here — the stored identity, the pinned region,
// the relay, the invitations, and the handlers the application registered.
// Everything per-peer lives in `LinkPeer`, which is the joining end's link
// with its sessions handed in. Handlers are registered here rather than per
// peer and are told which peer each call came from.

import { hex } from "@tailcat/link/internal/bytes.js";
import { DerpConnection, relayHostFor } from "@tailcat/link/internal/derp.js";
import { LinkError } from "@tailcat/link/internal/errors.js";
import { IndexedDbStore } from "@tailcat/link/internal/store.js";
import { DEFAULTS as LINK_DEFAULTS } from "@tailcat/link/internal/link.js";
import { nacl } from "@tailcat/link/internal/nacl.js";
import { encodeAddress, formatInvitationCode } from "./address.js";
import { LinkPeer } from "./hosted-peer.js";
import { acceptPairing } from "./pairing-accept.js";
import { PairingRecord, newOffer } from "./pairing-record.js";
import { pickRegion } from "./region.js";
import { RelayHub } from "./relay-hub.js";

const DEFAULTS = {
  // The link's own, not copies: every peer is a TailcatLink.
  ...LINK_DEFAULTS,
  // A page fetches the map from its own origin, because the control plane
  // serves it without the CORS header a cross-origin fetch needs. Node has no
  // origin and no such rule.
  derpMap: globalThis.document ? "/derpmap.json" : "https://tailcat.dev/derpmap.json",
  // How long a freshly minted invitation can pair a machine; LinkOptions.PairingWindow.
  pairingWindow: 60 * 60_000,
  maxPeers: 1,
};

// Candidates are heard side by side, because one at a time is a host a
// stranger can starve: knowing the address is enough to connect and say
// nothing. A cap rather than none, so that a flood costs bounded memory.
const MAX_HANDSHAKES_AT_ONCE = 8;

export class TailcatHost {
  #options;
  #record;
  #hub;
  #address;
  #peers = new Map();
  #closed = false;
  #handshakes = 0;
  #admitting = new Set();
  #slotFreed = null;
  #request = null;
  #notify = null;
  #transfer = null;
  #channels = new Map();
  #streams = new Map();

  constructor({ options, record, hub, address }) {
    this.#options = options;
    this.#record = record;
    this.#hub = hub;
    this.#address = address;
    /// `peer-joined` and `peer-left` with `{ peer, error? }`, every time a
    /// session comes up or goes down; `refused`, `invitation-expired` and
    /// `handshake-failed` with `{ peer, error? }` naming the machine by key;
    /// `relay-connected` and `relay-lost` for the relay under it all; and
    /// `answer-failed` with `{ peer, error }`.
    this.events = new EventTarget();
  }

  /// Starts hosting under `appName`: the identity and region stored for it,
  /// or new ones the first time, and the invitation worth publishing.
  ///
  /// @param regionId the relay region to listen in, the first time only:
  ///   once chosen it is stored and kept, because the region is half of the
  ///   address every code names. Left out, the nearest relay is measured.
  /// @param maxPeers how many machines may be paired at once. A security
  ///   bound, not a resource one: every admitted machine reaches the handlers,
  ///   and lowering it unpairs the ones seen longest ago.
  /// @param store where the identity and the pairings are kept. IndexedDB in
  ///   a page; under Node, `fileStore` from `@tailcat/link-host/file-store`.
  /// @param connectRelay `({ regionId, identity, signal }) => relay`, the seam
  ///   a test stands a host up on without a socket.
  static async start({ appName, regionId = null, store = null, connectRelay = null, ...rest }) {
    if (!appName) throw new Error("a host needs an appName to store its identity under");
    const options = { ...DEFAULTS, ...rest, appName };
    if (!Number.isInteger(options.maxPeers) || options.maxPeers < 1) {
      throw new Error(`maxPeers must be a whole number of at least 1, not ${options.maxPeers}`);
    }
    store ??= globalThis.indexedDB ? IndexedDbStore : null;
    if (!store) {
      throw new Error(
        "there is no IndexedDB here to keep the host's identity in; pass a store " +
          '(under Node: fileStore from "@tailcat/link-host/file-store")',
      );
    }

    const record = await PairingRecord.load({ appName, store, maxPeers: options.maxPeers });
    if (!record.state.privateKey) await record.setPrivateKey(nacl.box.keyPair().secretKey);
    const identity = nacl.box.keyPair.fromSecretKey(record.state.privateKey);

    // Before anything else is let in: a store written under a larger bound
    // would otherwise come back up with every one of its peers admitted.
    await record.enforcePeerLimit();

    let homeRegionId = record.state.homeRegionId;
    if (!homeRegionId) {
      homeRegionId = regionId ?? (await pickRegion(options.derpMap));
      await record.rememberHomeRegion(homeRegionId);
    }

    let host = null;
    const hub = new RelayHub({
      identity,
      homeRegionId,
      connectRelay:
        connectRelay ??
        (async ({ regionId: region, signal }) =>
          DerpConnection.connect({
            url: `wss://${options.relayHost ?? (await relayHostFor(options.derpMap, region))}/derp`,
            privateKey: identity.secretKey,
            publicKey: identity.publicKey,
            signal,
          })),
      options,
      emit: (name, detail) => host?.#emit(name, detail),
    });
    host = new TailcatHost({
      options,
      record,
      hub,
      address: encodeAddress({ publicKey: identity.publicKey, regionId: homeRegionId }),
    });

    await record.offerPairing(options.pairingWindow);
    for (const remembered of record.peers) host.#entry(remembered);
    try {
      await hub.start();
    } catch (error) {
      await host.close();
      throw error;
    }
    host.#acceptLoop();
    return host;
  }

  /// Forgets this machine's identity, its peers and its invitations: the next
  /// start is a new machine with a new address.
  static async forget(appName, store = IndexedDbStore) {
    await store.remove(appName);
  }

  /// This machine's address: its key and its region, and nothing secret.
  get address() {
    return this.#address;
  }

  get regionId() {
    return this.#hub.homeRegionId;
  }

  /// The code worth publishing now: the newest invitation's.
  get invitationCode() {
    const offer = this.#record.offer;
    return offer ? formatInvitationCode(this.#address, offer.token) : null;
  }

  /// When `invitationCode` stops being able to pair a machine, or null when
  /// it has nothing left to buy: a full host admits its peers by key.
  get invitationExpiresAt() {
    if (this.#record.full) return null;
    const offer = this.#record.offer;
    return offer ? new Date(offer.expiresAt) : null;
  }

  /// Every invitation still live, oldest first.
  get invitations() {
    return this.#record.liveOffers.map((offer) => this.#asInvitation(offer));
  }

  /// The machines paired with this one, in the order they joined.
  get peers() {
    return [...this.#peers.values()].sort((a, b) => a.pairedAt - b.pairedAt);
  }

  get maxPeers() {
    return this.#record.maxPeers;
  }

  /// Answers requests from any peer. The handler is given
  /// `(peer, text, bytes, content)` and may answer with text, bytes or a
  /// `LinkContent`; it runs once per request, however many sessions the
  /// request takes to arrive. A host without one refuses requests.
  onRequest(handler) {
    this.#request = handler;
    this.#applyAll();
  }

  /// Takes messages a peer sent without expecting an answer, with the same
  /// arguments a request handler is given. Without one, the request handler
  /// takes them and its answer is thrown away.
  onNotify(handler) {
    this.#notify = handler;
    this.#applyAll();
  }

  /// Takes transfers, given `(peer, content)`; finished for the sender once
  /// the handler has returned. A host without one refuses them.
  onTransfer(handler) {
    this.#transfer = handler;
    this.#applyAll();
  }

  /// Takes the channels peers open under `name`, given `(peer, channel)`. The
  /// channel ends when the handler returns. A name nothing is listening for
  /// is refused rather than swallowed.
  onChannel(name, handler) {
    this.#channels.set(name, handler);
    this.#applyAll();
  }

  /// Takes the streams peers open under `name`, given `(peer, stream)`. The
  /// stream is closed when the handler returns and aborted if it throws.
  onStream(name, handler) {
    this.#streams.set(name, handler);
    this.#applyAll();
  }

  /// Mints an invitation and starts offering it.
  ///
  /// @param lifetime how long it can pair a machine, in ms; the pairing
  ///   window by default.
  /// @param singleUse whether admitting one machine spends it.
  /// @param label for the operator's list; it never goes on the wire.
  /// @throws {LinkError} when the host already holds `maxPeers` machines: the
  ///   code could pair nothing, and the device scanning it would be the only
  ///   one told. Forget a peer first.
  async invite({ lifetime = this.#options.pairingWindow, singleUse = false, label = null } = {}) {
    this.#throwIfClosed();
    if (this.#record.full) {
      throw new LinkError(
        `this machine is already paired with ${this.#record.peers.length} of ${this.#record.maxPeers} machines; ` +
          "forget one before inviting another",
      );
    }
    const offer = await this.#record.addOffer(
      newOffer({ lifetimeMs: lifetime, now: this.#record.now, label, singleUse }),
    );
    return this.#asInvitation(offer);
  }

  /// Withdraws one invitation. Resolves with whether it was still there.
  revokeInvitation(id) {
    return this.#record.revokeOffer(id);
  }

  /// The code worth showing now, minted afresh only if the last has run out —
  /// so an application renewing on a timer never invalidates a code somebody
  /// is reading off the screen.
  async renewInvitation() {
    this.#throwIfClosed();
    const offer = await this.#record.offerPairing(this.#options.pairingWindow);
    return formatInvitationCode(this.#address, offer.token);
  }

  /// Unpairs one machine: dropped at once, forgotten, and refused if it comes
  /// back without a fresh invitation. The invitation it came in by is
  /// withdrawn with it, since it still holds that code.
  async forgetPeer(peer) {
    const key = typeof peer === "string" ? peer : peer.key;
    try {
      await this.#record.forgetPeer(key);
    } finally {
      // Dropped even if the write failed: the pairing is already gone from
      // what this host admits by, and a machine unpaired while connected must
      // stop reaching the handlers now, not at its next reconnection.
      const entry = this.#peers.get(key);
      if (entry && !this.#record.peerWith(key)) {
        this.#peers.delete(key);
        await entry._close({ drainTimeout: 0 });
      }
    }
  }

  /// Resolves with a peer once one has a session up — or, with
  /// `{ connected: false }`, once one is paired at all.
  waitForPeer({ connected = true } = {}) {
    const ready = () => this.peers.find((peer) => !connected || peer.connected);
    const found = ready();
    if (found) return Promise.resolve(found);
    // `closed` has already fired once the host is closed, and will not again.
    if (this.#closed) return Promise.reject(new LinkError("the host was closed"));
    return new Promise((resolve, reject) => {
      const check = () => {
        const peer = ready();
        if (peer) {
          stop();
          resolve(peer);
        } else if (this.#closed) {
          stop();
          reject(new LinkError("the host was closed"));
        }
      };
      const stop = () => {
        for (const name of ["peer-joined", "peer-appeared", "closed"]) this.events.removeEventListener(name, check);
      };
      for (const name of ["peer-joined", "peer-appeared", "closed"]) this.events.addEventListener(name, check);
    });
  }

  /// Stops hosting, but not before the answers already produced have left.
  async close({ drainTimeout = this.#options.requestTimeout } = {}) {
    if (this.#closed) return;
    this.#closed = true;
    await Promise.allSettled([...this.#peers.values()].map((peer) => peer._close({ drainTimeout })));
    this.#peers.clear();
    this.#hub.close();
    // A handshake still running may yet decide a pairing and write it. Closed
    // before that write lands, a `forget` straight after would be undone by
    // it, bringing back the identity and peers it had just removed.
    await Promise.allSettled([...this.#admitting]);
    await this.#record.settled();
    this.#emit("closed");
  }

  // ---- admitting -------------------------------------------------------

  async #acceptLoop() {
    for (;;) {
      let connection;
      try {
        connection = await this.#hub.incoming.next();
      } catch {
        return; // The hub was closed.
      }
      while (this.#handshakes >= MAX_HANDSHAKES_AT_ONCE) {
        this.#slotFreed ??= Promise.withResolvers();
        await this.#slotFreed.promise;
      }
      this.#handshakes++;
      const admitting = this.#admit(connection).finally(() => {
        this.#admitting.delete(admitting);
        this.#handshakes--;
        const freed = this.#slotFreed;
        this.#slotFreed = null;
        freed?.resolve();
      });
      this.#admitting.add(admitting);
    }
  }

  /// Decides whether the machine that has just connected may stay, and if it
  /// may, hands the session to the peer it belongs to. One stranger's bad
  /// handshake is not this host's problem: it is dropped, and the next
  /// machine is heard.
  async #admit(connection) {
    const key = hex(connection.peer);
    let verdict;
    try {
      verdict = await acceptPairing(
        connection,
        // Nothing more is written once closing has begun: `close` waits only
        // for the writes already decided.
        (hello) => (this.#closed ? { admitted: null } : this.#record.admit(key, hello)),
        this.#options.requestTimeout,
      );
    } catch (error) {
      connection.close(error);
      if (!this.#closed) this.#emit("handshake-failed", { peer: key, error });
      return;
    }

    if (this.#closed) {
      connection.close(new Error("the host was closed"));
      return;
    }
    if (!verdict.admitted) {
      connection.close(new Error("refused"));
      // Told apart from every other refusal, because it is the one with a
      // cure: the machine outside heard the same "no" as a wrong token, but
      // inviting again is something only somebody at this end can do.
      this.#emit(verdict.lapsed ? "invitation-expired" : "refused", { peer: key });
      return;
    }

    const peer = this.#entry(verdict.admitted);
    if (!this.#record.peerWith(key)) {
      // Unpaired while the handshake was running. `forgetPeer` forgets first
      // and drops the entry after, so it found nothing to drop; left alone
      // this would be a live peer the store no longer knows, which nothing
      // would ever unpair again.
      this.#peers.delete(key);
      connection.close(new Error("unpaired"));
      await peer._close({ drainTimeout: 0 });
      return;
    }
    peer._offer(connection);
  }

  // One peer per key, made the first time it is seen and kept until it is
  // forgotten — a machine that is switched off is still paired, and shows.
  #entry(remembered) {
    let peer = this.#peers.get(remembered.key);
    if (peer) {
      peer._remember(remembered);
      return peer;
    }
    peer = new LinkPeer({
      remembered,
      options: this.#options,
      emit: (name, detail) => this.#emit(name, detail),
    });
    peer.events.addEventListener("answer-failed", (event) =>
      this.#emit("answer-failed", { peer, error: event.detail }),
    );
    this.#peers.set(remembered.key, peer);
    this.#apply(peer);
    this.#emit("peer-appeared", { peer });
    return peer;
  }

  #applyAll() {
    for (const peer of this.#peers.values()) this.#apply(peer);
  }

  // The handlers live here; each peer's link is given a view of them that
  // says which peer is asking. Set rather than wrapped once, so that a
  // handler the application has not set is absent — and refused — on every
  // peer, rather than present and failing.
  #apply(peer) {
    const link = peer._link;
    const request = this.#request;
    const notify = this.#notify;
    const transfer = this.#transfer;
    link.onRequest(request && ((text, bytes, content) => request(peer, text, bytes, content)));
    link.onNotify(notify && ((text, bytes, content) => notify(peer, text, bytes, content)));
    link.onTransfer(transfer && ((content) => transfer(peer, content)));
    for (const [name, handler] of this.#channels) link.onChannel(name, handler && ((channel) => handler(peer, channel)));
    for (const [name, handler] of this.#streams) link.onStream(name, handler && ((stream) => handler(peer, stream)));
  }

  #asInvitation(offer) {
    return {
      id: offer.id,
      code: formatInvitationCode(this.#address, offer.token),
      expiresAt: new Date(offer.expiresAt),
      label: offer.label ?? null,
      singleUse: !!offer.singleUse,
    };
  }

  #throwIfClosed() {
    if (this.#closed) throw new LinkError("the host was closed");
  }

  #emit(name, detail) {
    this.events.dispatchEvent(new CustomEvent(name, { detail }));
  }
}

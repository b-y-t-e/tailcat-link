// Where the next session comes from.
//
// The browser only ever dials, so there is one source here where the .NET
// side has two (`SessionSources.cs`); what the split buys is the same thing
// it buys there. Everything about reaching a relay, agreeing a transport and
// deriving the traffic keys lives here, and the reconnect loop above knows
// none of it — which is also what lets a test stand the whole link up against
// a session that never touched a socket, the way
// `TailcatNodeOptions.ConnectRelay` does on the .NET side.

import { equal, readU64be, withTimeout } from "./bytes.js";
import { DerpConnection, relayHostFor } from "./derp.js";
import {
  PeerMessageType,
  PeerTransport,
  decodeHello,
  encodeHello,
  isPeerMessage,
  open as openPeerMessage,
  peerMessageType,
  seal,
} from "./peer.js";
import { Relay1Session, deriveKeys } from "./relay1.js";
import { DirectPath, DirectPathBackoff, decodeSignal, directPathConfig } from "./direct-path.js";
import { offerPairing } from "./pairing-handshake.js";
import { nacl } from "./nacl.js";

/// One session and the relay connection carrying it, as one thing to own.
///
/// The layer above opens streams, accepts them and lets go; that it is a
/// relay underneath rather than QUIC is this class's business alone.
export class RelayedConnection {
  #derp;
  #session;
  #peerPublic;
  #direct;
  #path = null;
  #retry = null;

  /// @param direct `{ config, backoff }` when a direct path may be offered on
  ///   this session, or null: see `direct-path.js`.
  constructor({ derp, session, peerPublic, direct = null }) {
    this.#derp = derp;
    this.#session = session;
    this.#peerPublic = peerPublic;
    this.#direct = direct;
    this.relayInfo = derp.serverInfo;
    /// Called with "direct" when the session's records leave the relay for a
    /// path between the two machines.
    this.onroute = null;
    derp.onclose = (error) => session.close(error);
    session.oncontrol = (payload) => {
      const signal = decodeSignal(payload);
      // Only answers to an offer this end made: a page is not asked by its
      // host for a path, and a host that asks is told nothing.
      if (signal && this.#path) this.#path.handle(signal);
    };
    session.onClosed(() => clearTimeout(this.#retry));
    this.#pumpRecords();
  }

  /// How the session's records travel: "relayed", or "direct".
  get route() {
    return this.#path?.open ? "direct" : "relayed";
  }

  /// Offers the host a direct path, if this link asks for one — now, or once
  /// the wait after a failed one is over. Called once the host has let this
  /// end in, never before: the offer names this machine's addresses, and
  /// until the pairing handshake has been answered nothing says the machine
  /// at the other end is one this end means to tell them to.
  startDirectPath() {
    const direct = this.#direct;
    if (!direct || this.#session.closed || (this.#path && !this.#path.ended)) return;
    clearTimeout(this.#retry);
    this.#retry = null;

    // Waited out inside the session, not left for the next one: the session
    // that follows a lost path is up within half a second, well inside the
    // wait, and asked only at its start it stayed relayed for as long as it
    // lived — which for a session nothing disturbs is for good.
    const wait = direct.backoff.dueInMs;
    if (wait > 0) {
      this.#retry = setTimeout(() => this.startDirectPath(), wait);
      this.#retry.unref?.();
      return;
    }

    this.#path = new DirectPath({
      session: this.#session,
      config: direct.config,
      onopen: () => this.onroute?.("direct"),
      onend: (outcome) => {
        direct.backoff.ended(outcome);
        // An attempt that came to nothing left the session where it was, on
        // the relay; it is tried again from there.
        this.startDirectPath();
      },
    });
    this.#path.offer();
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

  /// Resolves once everything written has left the socket. Written is not
  /// sent: closing on top of a full send buffer throws the answers away.
  flush(timeoutMs) {
    return this.#path?.open ? this.#path.flush(timeoutMs) : this.#derp.flush(timeoutMs);
  }

  close(error = new Error("the session was closed")) {
    this.#session.close(error);
    clearTimeout(this.#retry);
    this.#path?.close();
    this.#derp.close();
  }

  // Records arrive on the relay's queue and belong to the session; everything
  // else is somebody else talking to this key and is dropped.
  #pumpRecords() {
    (async () => {
      try {
        for (;;) {
          const packet = await this.#derp.packets.next();
          // The sender first, as TailcatNode does: a record from anyone else
          // cannot open, and handleRecord answers a record that will not open
          // by closing the session. Without this, one packet from anybody who
          // knows this browser's public key ends the session, over and over.
          if (!equal(packet.source, this.#peerPublic)) continue;
          if (!isPeerMessage(packet.payload)) continue;
          if (peerMessageType(packet.payload) !== PeerMessageType.Relay1Record) continue;
          await this.#session.handleRecord(packet.payload);
        }
      } catch (error) {
        this.#session.close(error);
      }
    })();
  }
}

/// The joining end: it knows where the host is, dials it, and does not hand
/// the session on until the pairing has been accepted.
export class DialingSessionSource {
  #dial;
  #hello;
  #handshakeTimeout;

  /// @param dial how a connection is reached; the seam a test replaces.
  /// @param displayName what the host is to call this browser. A hint the
  ///   host authenticates in no way, and it may be left out.
  constructor({ dial, pairingToken, displayName = null, handshakeTimeout }) {
    this.#dial = dial;
    this.#hello = { pairingToken, displayName };
    this.#handshakeTimeout = handshakeTimeout;
  }

  /// Produces the next session, already paired.
  async nextSession(signal) {
    const connection = await this.#dial(signal);
    try {
      // Before anything else is asked, because until it has run the host will
      // refuse everything anyway.
      await withTimeout(offerPairing(connection, this.#hello), this.#handshakeTimeout, "the host");
      // Optional, because `dial` is a seam and a test's connection need not
      // know about paths.
      connection.startDirectPath?.();
      return connection;
    } catch (error) {
      connection.close(error);
      throw error;
    }
  }
}

/// Reaches the host over a relay: the DERP map, the WebSocket, the sealed
/// hello, the ephemeral exchange, and a relay1 session on top of the lot.
export function relayDialer({ options, identity, peer }) {
  const config = directPathConfig(options.directPath);
  // One for every session this link dials, because what it remembers is how
  // the sessions before this one fared.
  const direct = config ? { config, backoff: new DirectPathBackoff({ firstMs: config.retryMs }) } : null;

  return async (signal) => {
    // `connectRelay` is the seam a test stands the whole handshake up on
    // without a socket, as TailcatNodeOptions.ConnectRelay is on the .NET side.
    const derp = options.connectRelay
      ? await options.connectRelay({ regionId: peer.regionId, identity, signal })
      : await DerpConnection.connect({
          url: `wss://${options.relayHost ?? (await relayHostFor(options.derpMap, peer.regionId))}/derp`,
          privateKey: identity.secretKey,
          publicKey: identity.publicKey,
          signal,
        });

    try {
      const keys = await negotiate({ derp, options, identity, peer });
      const session = new Relay1Session({ derp, peerPublic: peer.serverPublic, keys, isDialer: true });
      return new RelayedConnection({ derp, session, peerPublic: peer.serverPublic, direct });
    } catch (error) {
      derp.close();
      throw error;
    }
  };
}

const HELLO_RESEND_MS = 500;

// The hello and its answer: which transport the two have in common, and the
// two ephemeral halves the traffic keys come out of.
async function negotiate({ derp, options, identity, peer }) {
  const sessionId = Number(BigInt.asUintN(48, BigInt(Date.now())) * 1000n + BigInt(Math.floor(Math.random() * 1000)));
  const ephemeral = nacl.box.keyPair();

  const hello = seal(
    PeerMessageType.Hello,
    encodeHello({
      sessionId,
      homeRegionId: peer.regionId,
      transports: [PeerTransport.Relay1],
      ephemeral: ephemeral.publicKey,
    }),
    identity.secretKey,
    peer.serverPublic,
  );
  derp.sendPacket(peer.serverPublic, hello);

  // The same hello again every half second until it is answered, as
  // TailcatNode.SendUntilAnsweredAsync does: a relay drops what it cannot
  // deliver, and the host's connection to it may be mid-reconnect. Sent once,
  // one lost packet cost the whole handshake timeout. The host answers a
  // repeat with the same key, so the copies are harmless.
  const resend = setInterval(() => {
    if (!derp.closed) derp.sendPacket(peer.serverPublic, hello);
  }, HELLO_RESEND_MS);
  let ack;
  try {
    ack = await withTimeout(awaitHelloAck(derp, identity, peer, sessionId), options.handshakeTimeout, "the host");
  } finally {
    clearInterval(resend);
  }
  if (ack.transports.length !== 1 || ack.transports[0] !== PeerTransport.Relay1) {
    throw new Error(
      `no transport in common: this client speaks [relay1], the host answered [${ack.transports.join(", ")}]`,
    );
  }
  if (!ack.ephemeral) throw new Error("the host agreed to relay1 without sending an ephemeral key");

  return deriveKeys({
    ephemeralPrivate: ephemeral.secretKey,
    peerEphemeral: ack.ephemeral,
    sessionId,
    dialerPublic: identity.publicKey, // this end dialled
    hostPublic: peer.serverPublic,
  });
}

async function awaitHelloAck(derp, identity, peer, sessionId) {
  for (;;) {
    const packet = await derp.packets.next();
    if (!isPeerMessage(packet.payload)) continue;
    if (peerMessageType(packet.payload) !== PeerMessageType.HelloAck) continue;
    const opened = openPeerMessage(packet.payload, identity.secretKey, peer.serverPublic);
    if (!opened) continue;
    // An answer to the dial before this one names that dial's session. Every
    // dial is a new socket under the same key, so the relay hands a late one
    // to whichever is open now; taken, it gave this session the other's
    // ephemeral key, and nothing either end sent would open. TailcatNode
    // checks the same on its side.
    if (opened.payload.length < 8 || readU64be(opened.payload, 0) !== BigInt(sessionId)) continue;
    return decodeHello(opened.payload);
  }
}

// The relay, reached the only way a browser can: a WebSocket.
//
// A DERP relay routes packets between public keys and reads none of them. It
// is the meeting place, and for this client it is also the whole transport —
// there is no direct path to move onto.

import { Queue, concat, equal, readU32be, str, u32be, utf8 } from "./bytes.js";
import { nacl } from "./nacl.js";

export const DerpFrame = {
  ServerKey: 0x01,
  ClientInfo: 0x02,
  ServerInfo: 0x03,
  SendPacket: 0x04,
  RecvPacket: 0x05,
  KeepAlive: 0x06,
  PeerGone: 0x08,
  PeerPresent: 0x09,
  Ping: 0x12,
  Pong: 0x13,
  Health: 0x14,
  Restarting: 0x15,
};

const MAGIC = utf8("DERP\u{1F511}");
const PROTOCOL_VERSION = 2;
const KEY_LEN = 32;
const NONCE_LEN = 24;

// The largest frame this client will read: the DERP packet limit plus the
// small headers a frame can carry, matching DerpFrameStream on the .NET
// side. A length beyond this is a broken or hostile relay, and the socket
// goes rather than the buffer — the buffer is what such a length is for.
const MAX_PACKET_SIZE = 64 * 1024;
const MAX_FRAME_LEN = MAX_PACKET_SIZE + 1024;

export class DerpConnection {
  #socket;
  #privateKey;
  #publicKey;
  #buffer = new Uint8Array(0);
  #frames = new Queue();
  #closed = false;

  constructor(socket, privateKey, publicKey) {
    this.#socket = socket;
    this.#privateKey = privateKey;
    this.#publicKey = publicKey;
    this.packets = new Queue();
    this.onclose = null;

    socket.binaryType = "arraybuffer";
    socket.onmessage = (event) => this.#onBytes(new Uint8Array(event.data));
    socket.onclose = (event) =>
      this.#end(new Error(`the relay closed the connection (code ${event.code}${event.reason ? `, ${event.reason}` : ""})`));
    socket.onerror = () => this.#end(new Error("the relay connection failed"));
  }

  static async connect({ url, privateKey, publicKey, signal }) {
    const socket = new WebSocket(url, "derp");
    await new Promise((resolve, reject) => {
      socket.onopen = resolve;
      socket.onerror = () => reject(new Error(`could not reach ${url}`));
      signal?.addEventListener("abort", () => {
        socket.close();
        reject(signal.reason ?? new Error("aborted"));
      }, { once: true });
    });

    const conn = new DerpConnection(socket, privateKey, publicKey);
    await conn.#login();
    return conn;
  }

  get closed() {
    return this.#closed;
  }

  // A WebSocket delivers messages, not a stream: one DERP frame may span
  // several of them and several may share one, so everything is buffered and
  // frames are cut out as they complete.
  #onBytes(chunk) {
    if (this.#closed) {
      // The close is already under way; whatever still arrives — a WebSocket
      // keeps delivering until the handshake completes — is not for a
      // connection anybody is reading.
      return;
    }
    this.#buffer = concat(this.#buffer, chunk);
    for (;;) {
      if (this.#buffer.length < 5) return;
      const length = readU32be(this.#buffer, 1);
      if (length > MAX_FRAME_LEN) {
        this.#end(new Error(`the relay announced a ${length}-byte frame, over the ${MAX_FRAME_LEN} limit`));
        this.#shutSocket();
        return;
      }
      if (this.#buffer.length < 5 + length) return;

      const type = this.#buffer[0];
      const payload = this.#buffer.slice(5, 5 + length);
      this.#buffer = this.#buffer.slice(5 + length);
      this.#dispatch(type, payload);
    }
  }

  #dispatch(type, payload) {
    switch (type) {
      case DerpFrame.Ping:
        // The relay measures liveness with these and nothing above cares.
        this.send(DerpFrame.Pong, payload);
        return;
      case DerpFrame.KeepAlive:
      case DerpFrame.PeerGone:
      case DerpFrame.PeerPresent:
      case DerpFrame.Health:
      case DerpFrame.Restarting:
        return;
      case DerpFrame.RecvPacket:
        if (payload.length >= KEY_LEN) {
          this.packets.push({ source: payload.slice(0, KEY_LEN), payload: payload.slice(KEY_LEN) });
        }
        return;
      default:
        this.#frames.push({ type, payload });
    }
  }

  #end(error) {
    if (this.#closed) return;
    this.#closed = true;
    this.#frames.close(error);
    this.packets.close(error);
    this.onclose?.(error);
  }

  // The socket may already be gone — a close event raced us here — which is
  // not an error.
  #shutSocket() {
    try {
      this.#socket.close();
    } catch {
      // Already gone; there is nothing to close.
    }
  }

  async #login() {
    const greeting = await this.#frames.next();
    if (greeting.type !== DerpFrame.ServerKey) {
      throw new Error(`expected a ServerKey frame, got 0x${greeting.type.toString(16)}`);
    }
    if (!equal(greeting.payload.slice(0, MAGIC.length), MAGIC)) {
      throw new Error("the greeting is not DERP");
    }
    this.serverKey = greeting.payload.slice(MAGIC.length, MAGIC.length + KEY_LEN);

    const info = utf8(JSON.stringify({ version: PROTOCOL_VERSION, CanAckPings: true }));
    const nonce = nacl.randomBytes(NONCE_LEN);
    const sealed = nacl.box(info, nonce, this.serverKey, this.#privateKey);
    this.send(DerpFrame.ClientInfo, concat(this.#publicKey, nonce, sealed));

    const answer = await this.#frames.next();
    if (answer.type !== DerpFrame.ServerInfo) {
      throw new Error(`expected ServerInfo, got 0x${answer.type.toString(16)}`);
    }
    const opened = nacl.box.open(
      answer.payload.slice(NONCE_LEN),
      answer.payload.slice(0, NONCE_LEN),
      this.serverKey,
      this.#privateKey,
    );
    if (!opened) throw new Error("the relay's ServerInfo would not open");
    this.serverInfo = JSON.parse(str(opened));
  }

  send(type, payload) {
    if (this.#closed) throw new Error("the relay connection is closed");
    this.#socket.send(concat(new Uint8Array([type]), u32be(payload.length), payload));
  }

  sendPacket(destination, packet) {
    this.send(DerpFrame.SendPacket, concat(destination, packet));
  }

  /// Resolves once everything sent has left the socket, or the wait runs out.
  ///
  /// `send()` hands bytes to the WebSocket and returns; they leave later, and
  /// closing — or exiting the process — in that window discards whatever is
  /// still queued. `bufferedAmount` is the only progress a WebSocket reports,
  /// and it fires no event when it drains, so this polls it.
  async flush(timeoutMs) {
    const deadline = Date.now() + timeoutMs;
    while (!this.#closed && this.#socket.bufferedAmount > 0) {
      if (Date.now() >= deadline) return false;
      await new Promise((resolve) => setTimeout(resolve, 5));
    }
    return !this.#closed;
  }

  close() {
    this.#end(new Error("closed locally"));
    this.#shutSocket();
  }
}

/// Turns a region id into the relay to dial. Same-origin by default: the
/// control plane will not serve its map to a page from another origin, which
/// is why the upstream browser build ships its own copy too.
export async function relayHostFor(derpMapUrl, regionId) {
  const response = await fetch(derpMapUrl);
  if (!response.ok) {
    throw new Error(`the DERP map at ${derpMapUrl} answered ${response.status}`);
  }
  const map = await response.json();
  const region = map.Regions?.[String(regionId)];
  if (!region) throw new Error(`the DERP map has no region ${regionId}`);

  const node = region.Nodes?.find((n) => !n.STUNOnly);
  if (!node?.HostName) throw new Error(`region ${regionId} names no usable relay`);
  return node.HostName;
}

// A DERP relay in memory, for standing a host and the pages that join it up
// with no socket.
//
// `Tailcat.TestSupport`'s in-memory relay is the .NET half of the idea. Both
// ends plug in through `connectRelay`, so everything above the relay is the
// real thing: sealed hellos, the ephemeral exchange, relay1 records, the
// pairing handshake and the link frames.

import { Queue, hex } from "@tailcat/link/internal/bytes.js";

/// A relay that routes by public key within a region, as a real one does: a
/// packet sent into region 1 reaches a key only if it is connected there.
export function memoryRelay() {
  const clients = new Map();
  const address = (regionId, publicKey) => `${regionId}:${hex(publicKey)}`;

  class MemoryRelayConnection {
    constructor(regionId, publicKey) {
      this.regionId = regionId;
      this.publicKey = publicKey;
      this.packets = new Queue();
      this.closed = false;
      this.onclose = null;
      this.serverInfo = {};
    }

    sendPacket(destination, payload) {
      if (this.closed) throw new Error("the relay connection is closed");
      const to = clients.get(address(this.regionId, destination));
      // Copied, because the sender may reuse what it handed over.
      to?.packets.push({ source: this.publicKey, payload: payload.slice() });
    }

    async flush() {
      return !this.closed;
    }

    close(error = new Error("closed locally")) {
      if (this.closed) return;
      this.closed = true;
      const at = address(this.regionId, this.publicKey);
      if (clients.get(at) === this) clients.delete(at);
      this.packets.close(error);
      this.onclose?.(error);
    }
  }

  return {
    /// What both `connectRelay` seams are given.
    connect({ regionId, identity }) {
      const connection = new MemoryRelayConnection(regionId, identity.publicKey);
      const at = address(regionId, identity.publicKey);
      // A second login under one key replaces the first, as on a real relay.
      clients.get(at)?.close(new Error("replaced by a newer connection"));
      clients.set(at, connection);
      return connection;
    },

    /// Drops every connection `publicKey` holds, the way a relay that
    /// restarted would.
    cut(publicKey) {
      for (const connection of [...clients.values()]) {
        if (hex(connection.publicKey) === hex(publicKey)) connection.close(new Error("the relay went away"));
      }
    },

    /// The connections a key holds, by region.
    connectionsOf(publicKey) {
      return [...clients.values()].filter((c) => hex(c.publicKey) === hex(publicKey));
    },
  };
}

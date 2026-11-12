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
///
/// @param delayMs how long a packet takes to cross. Order is kept, as on a
///   real relay; what a delay adds is packets still on their way when
///   something faster than the relay overtakes them.
export function memoryRelay({ delayMs = 0 } = {}) {
  const clients = new Map();
  let packets = 0;
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
      packets++;
      // Copied, because the sender may reuse what it handed over.
      const packet = { source: this.publicKey, payload: payload.slice() };
      if (!delayMs) to?.packets.push(packet);
      // Timers of one length fire in the order they were set.
      else setTimeout(() => !to?.closed && to?.packets.push(packet), delayMs);
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
    /// How many packets have been handed to the relay, by anyone.
    get packets() {
      return packets;
    },

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

    /// Every connection open now.
    connections() {
      return [...clients.values()];
    },

    /// The connections a key holds, by region.
    connectionsOf(publicKey) {
      return [...clients.values()].filter((c) => hex(c.publicKey) === hex(publicKey));
    },
  };
}

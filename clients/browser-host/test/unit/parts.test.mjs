// The pieces a host is made of, one at a time: the address it writes, the
// relay's handshake it answers, the pairing rules it keeps, and the file it
// keeps them in under Node.

import assert from "node:assert/strict";
import { execFile } from "node:child_process";
import { mkdtemp, readdir, rm, stat } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { promisify } from "node:util";

import { memoryStore } from "@tailcat/link";
import { parseInvitationCode } from "@tailcat/link/internal/address.js";
import { base64urlDecode, delay, hex } from "@tailcat/link/internal/bytes.js";
import {
  PeerMessageType,
  PeerTransport,
  decodeHello,
  encodeHello,
  open,
  seal,
} from "@tailcat/link/internal/peer.js";
import { nacl } from "@tailcat/link/internal/nacl.js";
import { Relay1Session, deriveKeys } from "@tailcat/link/internal/relay1.js";
import { encodeAddress, formatInvitationCode } from "../../src/address.js";
import { fileStore } from "../../src/file-store.js";
import { PairingRecord, newOffer } from "../../src/pairing-record.js";
import { RelayHub } from "../../src/relay-hub.js";
import { memoryRelay } from "./memory-relay.mjs";

// ---- the address -----------------------------------------------------------

// `ConnBlobTests.Cases` "just_key": the key with bytes 1, 2 and 31 set, no
// region. The exact encoding the .NET side promises to keep producing.
const DOTNET_JUST_KEY = "tcoWFwWCAAAQIAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAHw";

const testKey = () => {
  const key = new Uint8Array(32);
  key[1] = 1;
  key[2] = 2;
  key[31] = 31;
  return key;
};

test("an address is the .NET encoding of the key, with the region after it", () => {
  const ours = base64urlDecode(encodeAddress({ publicKey: testKey(), regionId: 22 }).slice(2));
  const dotnet = base64urlDecode(DOTNET_JUST_KEY.slice(2));
  // A map of two rather than one, then the same "p" entry byte for byte.
  assert.equal(ours[0], 0xa2);
  assert.equal(dotnet[0], 0xa1);
  assert.equal(hex(ours.slice(1, dotnet.length)), hex(dotnet.slice(1)));
  // And "i": 22, as a one-byte unsigned integer.
  assert.equal(hex(ours.slice(dotnet.length)), hex(Uint8Array.of(0x61, 0x69, 0x16)));
});

test("what a host writes, a joining page reads back", () => {
  const publicKey = nacl.randomBytes(32);
  for (const regionId of [1, 23, 255, 256, 70_000]) {
    const code = formatInvitationCode(encodeAddress({ publicKey, regionId }), "tok-en_1");
    const parsed = parseInvitationCode(code);
    assert.equal(parsed.pairingToken, "tok-en_1");
    assert.equal(parsed.address.regionId, regionId);
    assert.equal(hex(parsed.address.serverPublic), hex(publicKey));
  }
});

test("a token that would split the code is refused", () => {
  const address = encodeAddress({ publicKey: testKey(), regionId: 1 });
  assert.throws(() => formatInvitationCode(address, "a.b"));
  assert.throws(() => formatInvitationCode(address, ""));
});

// ---- the relay's handshake -------------------------------------------------

/// A hub on an in-memory relay, and a dialler that speaks to it by hand.
async function hubWithDialler({ hostRegion = 1, diallerRegion = 1 } = {}) {
  const relay = memoryRelay();
  const host = nacl.box.keyPair();
  const hub = new RelayHub({
    identity: host,
    homeRegionId: hostRegion,
    connectRelay: (args) => relay.connect(args),
    options: { minReconnectDelay: 5, maxReconnectDelay: 20, handshakeTimeout: 1_000 },
  });
  await hub.start();

  const dialler = nacl.box.keyPair();
  // Sends into the host's region, and hears answers in its own.
  const out = relay.connect({ regionId: hostRegion, identity: dialler });
  const home = diallerRegion === hostRegion ? out : relay.connect({ regionId: diallerRegion, identity: dialler });

  const hello = ({ sessionId = 42n, transports = [PeerTransport.Quic, PeerTransport.Relay1], ephemeral } = {}) =>
    out.sendPacket(
      host.publicKey,
      seal(
        PeerMessageType.Hello,
        encodeHello({ sessionId, homeRegionId: diallerRegion, transports, ephemeral: ephemeral ?? nacl.box.keyPair().publicKey }),
        dialler.secretKey,
        host.publicKey,
      ),
    );
  const ack = async () => {
    const packet = await home.packets.next();
    const opened = open(packet.payload, dialler.secretKey, host.publicKey);
    assert.ok(opened, "the answer is sealed to the dialler");
    assert.equal(opened.type, PeerMessageType.HelloAck);
    return decodeHello(opened.payload);
  };
  return { hub, hello, ack, relay, host, out, dialler };
}

test("a hello offering QUIC first is answered with relay1 and a key", async () => {
  const { hub, hello, ack } = await hubWithDialler();
  try {
    hello();
    const answer = await ack();
    assert.deepEqual(answer.transports, [PeerTransport.Relay1]);
    assert.equal(answer.ephemeral?.length, 32);
    assert.equal(hub.incoming.length, 1, "one session waits for its pairing handshake");
  } finally {
    hub.close();
  }
});

test("hellos from many machines at once wait no more than the bound allows", async () => {
  const { hub, relay, host } = await hubWithDialler();
  try {
    const strangers = Array.from({ length: 100 }, () => {
      const identity = nacl.box.keyPair();
      return { identity, out: relay.connect({ regionId: 1, identity }) };
    });
    // All sent before any is answered, so every one reaches the check while
    // the others are still deriving their keys.
    for (const { identity, out } of strangers) {
      out.sendPacket(
        host.publicKey,
        seal(
          PeerMessageType.Hello,
          encodeHello({ sessionId: 1n, homeRegionId: 1, transports: [PeerTransport.Relay1], ephemeral: nacl.box.keyPair().publicKey }),
          identity.secretKey,
          host.publicKey,
        ),
      );
    }
    await delay(200);
    assert.equal(hub.incoming.length, 64);
  } finally {
    hub.close();
  }
});

test("a repeated hello is answered with the same key, and builds no second session", async () => {
  const { hub, hello, ack } = await hubWithDialler();
  try {
    const ephemeral = nacl.box.keyPair().publicKey;
    hello({ ephemeral });
    hello({ ephemeral });
    hello({ ephemeral });
    // The second and third arrive while the first is answered, and are one
    // answer: the newest copy waits, not each of them.
    const answers = [await ack(), await ack()];
    hello({ ephemeral });
    answers.push(await ack());
    assert.equal(new Set(answers.map((a) => hex(a.ephemeral))).size, 1);
    assert.equal(hub.incoming.length, 1);
  } finally {
    hub.close();
  }
});

test("copies of a hello sent while its answer is stuck are not kept", async () => {
  const relay = memoryRelay();
  const host = nacl.box.keyPair();
  const stuck = Promise.withResolvers();
  const hub = new RelayHub({
    identity: host,
    homeRegionId: 1,
    // Region 5 answers only once the test lets it.
    connectRelay: async (args) => {
      if (args.regionId === 5) await stuck.promise;
      return relay.connect(args);
    },
    options: { minReconnectDelay: 5, maxReconnectDelay: 20, handshakeTimeout: 1_000 },
  });
  await hub.start();
  const dialler = nacl.box.keyPair();
  const out = relay.connect({ regionId: 1, identity: dialler });
  const home = relay.connect({ regionId: 5, identity: dialler });
  try {
    for (let copy = 0; copy < 100; copy++) {
      out.sendPacket(
        host.publicKey,
        seal(
          PeerMessageType.Hello,
          encodeHello({ sessionId: 42n, homeRegionId: 5, transports: [PeerTransport.Relay1], ephemeral: nacl.box.keyPair().publicKey }),
          dialler.secretKey,
          host.publicKey,
        ),
      );
    }
    await delay(20);
    stuck.resolve();
    await home.packets.next();
    await home.packets.next();
    await delay(50);
    assert.equal(home.packets.length, 0, "the one being answered and the newest copy, and nothing else");
  } finally {
    stuck.resolve();
    hub.close();
  }
});

test("a relay in another region that never answers releases the hellos waiting on it", async () => {
  const relay = memoryRelay();
  const host = nacl.box.keyPair();
  const late = Promise.withResolvers();
  const failed = [];
  const hub = new RelayHub({
    identity: host,
    homeRegionId: 1,
    connectRelay: async (args) => {
      if (args.regionId === 5) await late.promise;
      return relay.connect(args);
    },
    options: { minReconnectDelay: 5, maxReconnectDelay: 20, handshakeTimeout: 50 },
    emit: (name, detail) => name === "handshake-failed" && failed.push(detail),
  });
  await hub.start();
  const dialler = nacl.box.keyPair();
  const out = relay.connect({ regionId: 1, identity: dialler });
  try {
    out.sendPacket(
      host.publicKey,
      seal(
        PeerMessageType.Hello,
        encodeHello({ sessionId: 42n, homeRegionId: 5, transports: [PeerTransport.Relay1], ephemeral: nacl.box.keyPair().publicKey }),
        dialler.secretKey,
        host.publicKey,
      ),
    );
    await delay(150);
    assert.equal(failed.length, 1);
    assert.match(failed[0].error.message, /region 5/);

    // Arriving after it was given up on, it is closed rather than kept.
    late.resolve();
    await delay(20);
    assert.deepEqual(relay.connectionsOf(host.publicKey).map((c) => c.regionId), [1]);
  } finally {
    late.resolve();
    hub.close();
  }
});

test("a connection to a region that ends late does not take the next one's sessions", async () => {
  const relay = memoryRelay();
  const host = nacl.box.keyPair();
  const oldReaderEnds = Promise.withResolvers();
  let opened = 0;
  const hub = new RelayHub({
    identity: host,
    homeRegionId: 1,
    connectRelay: (args) => {
      const connection = relay.connect(args);
      if (args.regionId === 5 && opened++ === 0) {
        // The first one's reader hears that it ended only when the test says.
        const next = connection.packets.next.bind(connection.packets);
        connection.packets.next = async () => {
          try {
            return await next();
          } catch (error) {
            await oldReaderEnds.promise;
            throw error;
          }
        };
      }
      return connection;
    },
    options: { minReconnectDelay: 5, maxReconnectDelay: 20, handshakeTimeout: 1_000 },
  });
  await hub.start();
  const helloFrom = (dialler, sessionId) => {
    const out = relay.connect({ regionId: 1, identity: dialler });
    const home = relay.connect({ regionId: 5, identity: dialler });
    out.sendPacket(
      host.publicKey,
      seal(
        PeerMessageType.Hello,
        encodeHello({ sessionId, homeRegionId: 5, transports: [PeerTransport.Relay1], ephemeral: nacl.box.keyPair().publicKey }),
        dialler.secretKey,
        host.publicKey,
      ),
    );
    return home.packets.next();
  };
  try {
    await helloFrom(nacl.box.keyPair(), 1n);
    // Its only session ends, so the connection is let go — but its reader
    // has not heard yet.
    (await hub.incoming.next()).close();
    await delay(10);

    await helloFrom(nacl.box.keyPair(), 2n);
    const second = await hub.incoming.next();
    oldReaderEnds.resolve();
    await delay(10);
    assert.equal(second.closed, false);
  } finally {
    oldReaderEnds.resolve();
    hub.close();
  }
});

test("a session that sends more than a handshake before pairing is closed", async () => {
  const { hub, hello, ack, out, dialler, host } = await hubWithDialler();
  try {
    // The dialler's half of a real session, so that what it sends opens.
    const mine = nacl.box.keyPair();
    hello({ ephemeral: mine.publicKey });
    const answer = await ack();
    const keys = await deriveKeys({
      ephemeralPrivate: mine.secretKey,
      peerEphemeral: answer.ephemeral,
      sessionId: 42n,
      dialerPublic: dialler.publicKey,
      hostPublic: host.publicKey,
    });
    const session = new Relay1Session({ derp: out, peerPublic: host.publicKey, keys, isDialer: true });
    const connection = await hub.incoming.next();

    // Records of the session before, resent after a cut: ignored, and free.
    for (let record = 0; record < 3; record++) connection._deliver(new Uint8Array(30_000));
    await delay(20);
    assert.equal(connection.closed, false);

    session.openStream().write(new Uint8Array(100 * 1024)).catch(() => {});
    await delay(100);
    assert.equal(connection.closed, true);
  } finally {
    hub.close();
  }
});

test("a session let in may send as much as it likes", async () => {
  const { hub, hello, ack, out, dialler, host } = await hubWithDialler();
  try {
    const mine = nacl.box.keyPair();
    hello({ ephemeral: mine.publicKey });
    const answer = await ack();
    const keys = await deriveKeys({
      ephemeralPrivate: mine.secretKey,
      peerEphemeral: answer.ephemeral,
      sessionId: 42n,
      dialerPublic: dialler.publicKey,
      hostPublic: host.publicKey,
    });
    const session = new Relay1Session({ derp: out, peerPublic: host.publicKey, keys, isDialer: true });
    const connection = await hub.incoming.next();
    connection.trust();
    session.openStream().write(new Uint8Array(100 * 1024)).catch(() => {});
    await delay(100);
    assert.equal(connection.closed, false);
  } finally {
    hub.close();
  }
});
test("a dialler listening in another region is answered there", async () => {
  const { hub, hello, ack, relay, host } = await hubWithDialler({ hostRegion: 1, diallerRegion: 5 });
  try {
    hello();
    // `ack` reads the dialler's region-5 connection, so the answer got there.
    assert.deepEqual((await ack()).transports, [PeerTransport.Relay1]);
    assert.deepEqual(relay.connectionsOf(host.publicKey).map((c) => c.regionId).sort(), [1, 5]);
  } finally {
    hub.close();
  }
});

test("a connection opened to answer another region is closed once nothing uses it", async () => {
  const { hub, hello, ack, relay, host } = await hubWithDialler({ hostRegion: 1, diallerRegion: 5 });
  const regions = () => relay.connectionsOf(host.publicKey).map((c) => c.regionId).sort();
  try {
    // A hello that builds no session holds the connection only while it is answered.
    hello({ transports: [PeerTransport.Quic] });
    await ack();
    await delay(10);
    assert.deepEqual(regions(), [1]);

    // One that builds a session holds it until the session ends.
    hello({ sessionId: 43n });
    await ack();
    assert.deepEqual(regions(), [1, 5]);
    (await hub.incoming.next()).close();
    await delay(10);
    assert.deepEqual(regions(), [1]);
  } finally {
    hub.close();
  }
});

test("a dialler with no transport in common is told what the host speaks", async () => {
  const { hub, hello, ack } = await hubWithDialler();
  try {
    hello({ transports: [PeerTransport.Quic] });
    const answer = await ack();
    assert.deepEqual(answer.transports, [PeerTransport.Relay1]);
    assert.equal(answer.ephemeral, null, "no session, so no key");
    assert.equal(hub.incoming.length, 0);
  } finally {
    hub.close();
  }
});

// ---- the pairing rules -----------------------------------------------------

async function record({ maxPeers = 1, now = () => 1_000 } = {}) {
  return PairingRecord.load({ appName: "rules", store: memoryStore(), maxPeers, now });
}

test("a stranger needs a live token and room; a known machine needs neither", async () => {
  let now = 1_000;
  const rules = await record({ now: () => now });
  const offer = await rules.offerPairing(100);

  assert.equal((await rules.admit("aa", { pairingToken: "wrong" })).admitted, null);
  assert.ok((await rules.admit("aa", { pairingToken: offer.token, displayName: "A" })).admitted);
  assert.equal((await rules.admit("bb", { pairingToken: offer.token })).admitted, null, "full");

  now += 1_000;
  assert.ok((await rules.admit("aa", { pairingToken: "anything" })).admitted, "known by its key");
});

test("an expired token is refused and reported as lapsed only to this end", async () => {
  let now = 1_000;
  const rules = await record({ maxPeers: 2, now: () => now });
  const offer = await rules.offerPairing(100);
  now += 100;
  assert.deepEqual(await rules.admit("aa", { pairingToken: offer.token }), { admitted: null, lapsed: true });
  assert.deepEqual(await rules.admit("aa", { pairingToken: "never-offered" }), { admitted: null, lapsed: false });
});

test("forgetting a machine withdraws the invitation it came in by", async () => {
  const rules = await record({ maxPeers: 2 });
  const offer = await rules.addOffer(newOffer({ lifetimeMs: 10_000, now: 1_000 }));
  await rules.admit("aa", { pairingToken: offer.token });
  assert.equal(await rules.forgetPeer("aa"), true);
  assert.equal((await rules.admit("aa", { pairingToken: offer.token })).admitted, null);
  assert.equal(await rules.forgetPeer("aa"), false);
});

test("a host restarted inside its window shows the same code, and a new one after", async () => {
  let now = 1_000;
  const rules = await record({ now: () => now });
  const first = await rules.offerPairing(100);
  assert.equal((await rules.offerPairing(100)).token, first.token);
  now += 100;
  assert.notEqual((await rules.offerPairing(100)).token, first.token);
});

test("a store written by a newer version is refused rather than misread", async () => {
  const store = memoryStore();
  await store.save("future", { version: 99 });
  await assert.rejects(PairingRecord.load({ appName: "future", store }), /newer version/);
});

// ---- the file store --------------------------------------------------------

test("the file store keeps bytes as bytes, and only its owner may read it", async () => {
  const directory = await mkdtemp(join(tmpdir(), "tailcat-host-"));
  try {
    const store = fileStore(directory);
    assert.equal(await store.load("app"), null);
    const privateKey = nacl.randomBytes(32);
    await store.save("app", { privateKey, peers: [{ key: "aa" }] });
    const loaded = await store.load("app");
    assert.ok(loaded.privateKey instanceof Uint8Array);
    assert.equal(hex(loaded.privateKey), hex(privateKey));
    assert.deepEqual(loaded.peers, [{ key: "aa" }]);
    if (process.platform === "win32") {
      // One entry, and not inherited: nobody but this user is listed.
      const { stdout } = await promisify(execFile)("icacls", [join(directory, "app.json")]);
      const entries = stdout.split(/\r?\n/).filter((line) => line.includes(":("));
      assert.equal(entries.length, 1, stdout);
      assert.doesNotMatch(entries[0], /\(I\)/);
    } else {
      assert.equal((await stat(join(directory, "app.json"))).mode & 0o777, 0o600);
    }
    // Nothing written on the way is left behind.
    assert.deepEqual(await readdir(directory), ["app.json"]);
    await store.remove("app");
    assert.equal(await store.load("app"), null);
    await assert.rejects(store.load("../escape"), /cannot name a file/);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

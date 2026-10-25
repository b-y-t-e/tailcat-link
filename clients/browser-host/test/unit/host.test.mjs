// The whole host, offline: pages join it through an in-memory relay with
// nothing faked above the relay — sealed hellos, the ephemeral exchange,
// relay1, the pairing handshake, and the link on both ends.

import assert from "node:assert/strict";
import test from "node:test";

import { TailcatLink, LinkContent, PairingRefusedError, memoryStore } from "@tailcat/link";
import { parseAddress } from "@tailcat/link/internal/address.js";
import { delay, str, utf8 } from "@tailcat/link/internal/bytes.js";
import { TailcatHost } from "../../src/index.js";
import { memoryRelay } from "./memory-relay.mjs";

const HOST_APP = "host-tests";

// Short enough that a reconnection happens inside a test, long enough that
// nothing times out because a machine was busy.
const IMPATIENT = {
  requestTimeout: 500,
  requestDeadline: 5_000,
  heartbeatInterval: 100,
  minReconnectDelay: 5,
  maxReconnectDelay: 50,
  handshakeTimeout: 1_000,
};

async function startHost(relay, { store = memoryStore(), ...options } = {}) {
  const host = await TailcatHost.start({
    appName: HOST_APP,
    regionId: 7,
    store,
    connectRelay: (args) => relay.connect(args),
    ...IMPATIENT,
    ...options,
  });
  const events = [];
  for (const name of ["peer-joined", "peer-left", "refused", "invitation-expired", "handshake-failed"]) {
    host.events.addEventListener(name, (event) => events.push({ name, detail: event.detail }));
  }
  return { host, store, events };
}

function joinPage(relay, { appName = "page", store = memoryStore(), ...options } = {}) {
  return TailcatLink.join({
    appName,
    store,
    connectRelay: (args) => relay.connect(args),
    ...IMPATIENT,
    ...options,
  });
}

const until = async (condition, what) => {
  for (let waited = 0; waited < 5_000; waited += 5) {
    if (condition()) return;
    await delay(5);
  }
  throw new Error(`gave up waiting for ${what}`);
};

const failedWith = (link) =>
  new Promise((resolve) => link.events.addEventListener("failed", (event) => resolve(event.detail), { once: true }));

test("a page joins with the published code and asks the host something", async () => {
  const relay = memoryRelay();
  const { host } = await startHost(relay);
  host.onRequest((peer, text) => `${peer.name} asked: ${text}`);
  const page = await joinPage(relay, { invitationCode: host.invitationCode, displayName: "kitchen tablet" });
  try {
    assert.equal(await page.request("status"), "kitchen tablet asked: status");
    assert.equal(host.peers.length, 1);
    assert.equal(host.peers[0].name, "kitchen tablet");
  } finally {
    await page.close();
    await host.close();
  }
});

test("the host asks the page, and the page's handler answers once", async () => {
  const relay = memoryRelay();
  const { host } = await startHost(relay);
  const page = await joinPage(relay, { invitationCode: host.invitationCode });
  let runs = 0;
  page.onRequest((text) => {
    runs++;
    return text.toUpperCase();
  });
  try {
    const peer = await host.waitForPeer();
    assert.equal(await peer.request("hello page"), "HELLO PAGE");
    const answer = await peer.request(LinkContent.fromString("as content", { name: "note.txt" }));
    assert.equal(answer.text, "AS CONTENT");
    assert.equal(runs, 2);
  } finally {
    await page.close();
    await host.close();
  }
});

test("a wrong token is refused, and the page stops rather than retrying", async () => {
  const relay = memoryRelay();
  const { host, events } = await startHost(relay);
  const wrong = `${host.address}.not-the-token`;
  const page = await joinPage(relay, { invitationCode: wrong });
  try {
    await assert.rejects(page.waitUntilConnected(3_000), PairingRefusedError);
    assert.equal(host.peers.length, 0);
    assert.ok(events.some((e) => e.name === "refused"));
  } finally {
    await page.close();
    await host.close();
  }
});

test("a full host refuses another machine, whatever code it holds", async () => {
  const relay = memoryRelay();
  const { host } = await startHost(relay);
  const code = host.invitationCode;
  const first = await joinPage(relay, { appName: "first", invitationCode: code });
  await first.waitUntilConnected(3_000);
  const second = await joinPage(relay, { appName: "second", invitationCode: code });
  try {
    await assert.rejects(second.waitUntilConnected(3_000), PairingRefusedError);
    assert.equal(host.peers.length, 1);
    await assert.rejects(host.invite(), /already paired with 1 of 1/);
    assert.equal(host.invitationExpiresAt, null, "a full host's code has nothing left to buy");
  } finally {
    await first.close();
    await second.close();
    await host.close();
  }
});

test("a host with room for several tells its handlers which machine asked", async () => {
  const relay = memoryRelay();
  const { host } = await startHost(relay, { maxPeers: 2 });
  host.onRequest((peer, text) => `${peer.name}:${text}`);
  const a = await joinPage(relay, { appName: "a", invitationCode: host.invitationCode, displayName: "A" });
  const b = await joinPage(relay, { appName: "b", invitationCode: host.invitationCode, displayName: "B" });
  try {
    assert.equal(await a.request("x"), "A:x");
    assert.equal(await b.request("y"), "B:y");
    assert.deepEqual(host.peers.map((p) => p.name).sort(), ["A", "B"]);
  } finally {
    await a.close();
    await b.close();
    await host.close();
  }
});

test("a single-use invitation pairs one machine and no other", async () => {
  const relay = memoryRelay();
  const { host } = await startHost(relay, { maxPeers: 3 });
  const invitation = await host.invite({ singleUse: true, label: "phone" });
  assert.equal(invitation.label, "phone");
  const first = await joinPage(relay, { appName: "first", invitationCode: invitation.code });
  await first.waitUntilConnected(3_000);
  const second = await joinPage(relay, { appName: "second", invitationCode: invitation.code });
  try {
    await assert.rejects(second.waitUntilConnected(3_000), PairingRefusedError);
    assert.ok(!host.invitations.some((i) => i.id === invitation.id), "spent, so no longer offered");
  } finally {
    await first.close();
    await second.close();
    await host.close();
  }
});

test("an invitation whose window closed is refused, and the host is told why", async () => {
  const relay = memoryRelay();
  const { host, events } = await startHost(relay, { maxPeers: 2 });
  const invitation = await host.invite({ lifetime: 1 });
  await delay(5);
  const page = await joinPage(relay, { invitationCode: invitation.code });
  try {
    await assert.rejects(page.waitUntilConnected(3_000), PairingRefusedError);
    await until(() => events.some((e) => e.name === "invitation-expired"), "the expiry to be reported");
  } finally {
    await page.close();
    await host.close();
  }
});

test("a forgotten machine is dropped at once and refused when it comes back", async () => {
  const relay = memoryRelay();
  const { host } = await startHost(relay);
  host.onRequest(() => "yes");
  const page = await joinPage(relay, { invitationCode: host.invitationCode });
  const failed = failedWith(page);
  try {
    assert.equal(await page.request("still paired?"), "yes");
    await host.forgetPeer(host.peers[0]);
    assert.equal(host.peers.length, 0);
    assert.ok(await failed instanceof PairingRefusedError, "the page re-dialled and was refused");
  } finally {
    await page.close();
    await host.close();
  }
});

test("a restarted host keeps its address, and a paired page needs no code", async () => {
  const relay = memoryRelay();
  const hostStore = memoryStore();
  const pageStore = memoryStore();
  const first = await startHost(relay, { store: hostStore });
  const code = first.host.invitationCode;
  const page = await joinPage(relay, { store: pageStore, invitationCode: code });
  await page.waitUntilConnected(3_000);
  await page.close();
  await first.host.close();

  const second = await startHost(relay, { store: hostStore, regionId: 99 });
  second.host.onRequest(() => "back");
  const again = await joinPage(relay, { store: pageStore });
  try {
    assert.equal(second.host.invitationCode, code, "same key, same region, same token");
    assert.equal(second.host.regionId, 7, "the region is pinned, not re-chosen");
    assert.equal(await again.request("anyone?"), "back");
  } finally {
    await again.close();
    await second.host.close();
  }
});

test("a lowered peer limit unpairs the machines seen longest ago", async () => {
  const relay = memoryRelay();
  const store = memoryStore();
  const { host } = await startHost(relay, { store, maxPeers: 2 });
  const a = await joinPage(relay, { appName: "a", invitationCode: host.invitationCode, displayName: "A" });
  await a.waitUntilConnected(3_000);
  await delay(5);
  const b = await joinPage(relay, { appName: "b", invitationCode: host.invitationCode, displayName: "B" });
  await b.waitUntilConnected(3_000);
  await a.close();
  await b.close();
  await host.close();

  const narrower = await startHost(relay, { store, maxPeers: 1 });
  try {
    assert.deepEqual(narrower.host.peers.map((p) => p.name), ["B"]);
  } finally {
    await narrower.host.close();
  }
});

test("a session the relay cut is replaced, and requests carry on", async () => {
  const relay = memoryRelay();
  const { host } = await startHost(relay);
  host.onRequest((_peer, text) => text.toUpperCase());
  const page = await joinPage(relay, { invitationCode: host.invitationCode });
  try {
    assert.equal(await page.request("before"), "BEFORE");
    relay.cut(parseAddress(host.address).serverPublic);
    assert.equal(await page.request("after"), "AFTER");
  } finally {
    await page.close();
    await host.close();
  }
});

test("channels and streams reach the host's handlers with the peer", async () => {
  const relay = memoryRelay();
  const { host } = await startHost(relay);
  const frames = [];
  host.onChannel("telemetry", async (peer, channel) => {
    for await (const frame of channel.read()) frames.push(`${peer.name}:${str(frame)}`);
  });
  host.onStream("echo", async (_peer, stream) => {
    for (let bytes = await stream.read(); bytes.length; bytes = await stream.read()) await stream.write(bytes);
  });
  const page = await joinPage(relay, { invitationCode: host.invitationCode, displayName: "P" });
  try {
    const channel = await page.openChannel("telemetry");
    await channel.send(utf8("one"));
    await channel.send(utf8("two"));
    await channel.close();
    await until(() => frames.length === 2, "both frames");
    assert.deepEqual(frames, ["P:one", "P:two"]);

    const stream = await page.openStream("echo");
    await stream.write(utf8("ping"));
    await stream.finish();
    assert.equal(str(await stream.read()), "ping");
    await stream.close();

    await assert.rejects(page.openChannel("nobody-listens"), /nobody-listens/);
  } finally {
    await page.close();
    await host.close();
  }
});

test("a channel or stream handler set back to null is refused, not opened", async () => {
  const relay = memoryRelay();
  const { host } = await startHost(relay);
  host.onChannel("telemetry", async () => {});
  host.onStream("echo", async () => {});
  const page = await joinPage(relay, { invitationCode: host.invitationCode });
  try {
    await host.waitForPeer();
    host.onChannel("telemetry", null);
    host.onStream("echo", null);
    await assert.rejects(page.openChannel("telemetry"), /telemetry/);
    await assert.rejects(page.openStream("echo"), /echo/);
  } finally {
    await page.close();
    await host.close();
  }
});

test("a paired page may send more than a handshake's worth", { timeout: 10_000 }, async () => {
  const relay = memoryRelay();
  const { host } = await startHost(relay);
  host.onRequest((_peer, _text, bytes) => String(bytes.length));
  const page = await joinPage(relay, { invitationCode: host.invitationCode });
  try {
    // Past the bound a session has before pairing, which must lift once it is in.
    assert.equal(str(await page.request(new Uint8Array(200 * 1024))), String(200 * 1024));
  } finally {
    await page.close();
    await host.close();
  }
});

test("transfers reach the host's transfer handler", async () => {
  const relay = memoryRelay();
  const { host } = await startHost(relay);
  const received = [];
  host.onTransfer((peer, content) => received.push(content.text));
  const page = await joinPage(relay, { invitationCode: host.invitationCode });
  try {
    await page.send(LinkContent.fromString("a file", { name: "f.txt" }));
    assert.deepEqual(received, ["a file"]);
  } finally {
    await page.close();
    await host.close();
  }
});

test("waiting for a peer on a closed host fails at once", async () => {
  const { host } = await startHost(memoryRelay());
  await host.close();
  await assert.rejects(host.waitForPeer(), /closed/);
});

test("closing the host waits for a pairing still being written", async () => {
  const relay = memoryRelay();
  const backing = memoryStore();
  let gate = null;
  let saving = null;
  const store = {
    load: (name) => backing.load(name),
    remove: (name) => backing.remove(name),
    async save(name, state) {
      if (gate) {
        saving?.resolve();
        await gate.promise;
      }
      return backing.save(name, state);
    },
  };
  const { host } = await startHost(relay, { store });
  gate = Promise.withResolvers();
  saving = Promise.withResolvers();
  const page = await joinPage(relay, { invitationCode: host.invitationCode });
  try {
    await saving.promise;
    let closed = false;
    const closing = host.close().then(() => (closed = true));
    await delay(50);
    assert.equal(closed, false, "close returned with the pairing not yet written");
    gate.resolve();
    await closing;
    const saved = await backing.load(HOST_APP);
    assert.equal(saved.peers.length, 1);
  } finally {
    gate.resolve();
    await page.close();
    await host.close();
  }
});

test("a machine forgotten while the store fails is dropped all the same", async () => {
  const relay = memoryRelay();
  const backing = memoryStore();
  let failing = false;
  const store = {
    load: (name) => backing.load(name),
    remove: (name) => backing.remove(name),
    save: (name, state) => (failing ? Promise.reject(new Error("disk full")) : backing.save(name, state)),
  };
  const { host } = await startHost(relay, { store });
  let runs = 0;
  host.onRequest(() => String(++runs));
  const page = await joinPage(relay, { invitationCode: host.invitationCode });
  try {
    assert.equal(await page.request("before"), "1");
    const refused = failedWith(page);
    failing = true;
    await assert.rejects(host.forgetPeer(host.peers[0]), /disk full/);
    assert.equal(host.peers.length, 0);
    assert.ok((await refused) instanceof PairingRefusedError);
    assert.equal(runs, 1);
  } finally {
    await page.close();
    await host.close();
  }
});

test("forgetting a machine that stopped reading a channel does not wait for it", { timeout: 10_000 }, async () => {
  const relay = memoryRelay();
  const { host } = await startHost(relay);
  const page = await joinPage(relay, { invitationCode: host.invitationCode });
  const never = Promise.withResolvers();
  page.onChannel("feed", () => never.promise);
  try {
    const peer = await host.waitForPeer();
    const channel = await peer.openChannel("feed");
    // More than the stream's window, to a reader that never reads.
    channel.send(new Uint8Array(1024 * 1024)).catch(() => {});
    await delay(50);
    await host.forgetPeer(peer);
    assert.equal(host.peers.length, 0);
  } finally {
    never.resolve();
    await page.close();
    await host.close();
  }
});

test("closing waits no longer than its drain timeout in all", { timeout: 10_000 }, async () => {
  const relay = memoryRelay();
  const { host } = await startHost(relay);
  const never = Promise.withResolvers();
  host.onRequest(() => never.promise);
  const page = await joinPage(relay, { invitationCode: host.invitationCode });
  page.onChannel("feed", () => never.promise);
  try {
    const peer = await host.waitForPeer();
    // Both things a drain waits for, stuck: a channel the page never reads,
    // and an answer the handler never produces.
    const channel = await peer.openChannel("feed");
    channel.send(new Uint8Array(1024 * 1024)).catch(() => {});
    page.request("stuck").catch(() => {});
    await delay(50);
    const started = Date.now();
    await host.close({ drainTimeout: 400 });
    assert.ok(Date.now() - started < 700, `closing took ${Date.now() - started} ms`);
  } finally {
    never.resolve();
    await page.close();
    await host.close();
  }
});

test("closing the host twice is harmless", async () => {
  const { host } = await startHost(memoryRelay());
  await host.close();
  await host.close();
});

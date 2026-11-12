// The direct path, offline: a host and a page that joins it over an in-memory
// relay, with WebRTC replaced by `fake-rtc.mjs`. Everything between the two —
// the offer on the control stream, the gate behind the pairing handshake, the
// move from one path to the other and what happens when the new one dies — is
// the real thing.

import assert from "node:assert/strict";
import test from "node:test";

import { TailcatLink, memoryStore } from "@tailcat/link";
import { delay } from "@tailcat/link/internal/bytes.js";
import { DirectSignal, directPathConfig, encodeSignal } from "@tailcat/link/internal/direct-path.js";
import { nacl } from "@tailcat/link/internal/nacl.js";
import { Relay1Session, deriveKeys } from "@tailcat/link/internal/relay1.js";
import { TailcatHost } from "../../src/index.js";
import { HostedConnection } from "../../src/relay-hub.js";
import { fakeRtc } from "./fake-rtc.mjs";
import { memoryRelay } from "./memory-relay.mjs";

const IMPATIENT = {
  requestTimeout: 500,
  requestDeadline: 5_000,
  heartbeatInterval: 100,
  minReconnectDelay: 5,
  maxReconnectDelay: 50,
  handshakeTimeout: 1_000,
};

const startHost = (relay, options = {}) =>
  TailcatHost.start({
    appName: "direct-tests",
    regionId: 7,
    store: memoryStore(),
    connectRelay: (args) => relay.connect(args),
    ...IMPATIENT,
    ...options,
  });

const joinPage = (relay, options = {}) =>
  TailcatLink.join({
    appName: "page",
    store: memoryStore(),
    connectRelay: (args) => relay.connect(args),
    ...IMPATIENT,
    ...options,
  });

const until = async (condition, what) => {
  for (let waited = 0; waited < 5_000; waited += 5) {
    if (condition()) return;
    await delay(5);
  }
  throw new Error(`gave up waiting for ${what}`);
};

const routesOf = (events) => {
  const seen = [];
  events.addEventListener("route-changed", (event) => seen.push(event.detail));
  return seen;
};

test("two ends that ask for a direct path move to it, and the relay carries nothing more", async () => {
  const relay = memoryRelay();
  const net = fakeRtc();
  const host = await startHost(relay, { directPath: net });
  host.onRequest((peer, text) => text.toUpperCase());
  const page = await joinPage(relay, { invitationCode: host.invitationCode, directPath: net });
  const routes = routesOf(page.events);
  try {
    const peer = await host.waitForPeer();
    await until(() => page.route === "direct" && peer.route === "direct", "both ends to go direct");
    // Whatever was still crossing the relay when the path opened.
    await delay(20);

    const before = { relay: relay.packets, channel: net.sent };
    assert.equal(await page.request("over the channel"), "OVER THE CHANNEL");
    page.onRequest((text) => `page heard ${text}`);
    assert.equal(await peer.request("and back"), "page heard and back");

    assert.equal(relay.packets, before.relay, "a record went by the relay after the path opened");
    assert.ok(net.sent > before.channel);
    assert.deepEqual(routes, ["relayed", "direct"]);
  } finally {
    await page.close();
    await host.close();
  }
  // A moment after, once each end's goodbye has left (direct-path.js, LEAVE_MS).
  await until(() => net.connections.every((pc) => pc.connectionState === "closed"), "every peer connection to close");
});

test("what was on its way through a slow relay when the path opened still arrives in order", async () => {
  // The relay is the slow path, so the first records on the channel overtake
  // the last ones sent before it; they wait for them rather than end the session.
  const relay = memoryRelay({ delayMs: 15 });
  const net = fakeRtc();
  const host = await startHost(relay, { directPath: net });
  host.onRequest((peer, text) => String(text.length));
  const page = await joinPage(relay, { invitationCode: host.invitationCode, directPath: net });
  const disconnected = [];
  page.events.addEventListener("disconnected", (event) => disconnected.push(event.detail));
  try {
    // Sent while the path is still being arranged, and large enough to be in
    // flight on both sides of the move.
    const big = "x".repeat(2 * 1024 * 1024);
    assert.equal(await page.request(big), String(big.length));
    assert.equal(page.route, "direct");
    assert.deepEqual(disconnected, []);
  } finally {
    await page.close();
    await host.close();
  }
});

test("a host that takes no direct path says so, and the link stays on the relay", async () => {
  const relay = memoryRelay();
  const net = fakeRtc();
  const host = await startHost(relay);
  host.onRequest((peer, text) => text);
  const page = await joinPage(relay, { invitationCode: host.invitationCode, directPath: net });
  try {
    assert.equal(await page.request("still here"), "still here");
    // Declined, so not left open for the whole of its patience.
    await until(() => net.connections.length === 1 && net.connections[0].connectionState === "closed", "the offer to be let go");
    assert.equal(page.route, "relayed");
    assert.equal(await page.request("and still"), "and still");
  } finally {
    await page.close();
    await host.close();
  }
});

test("a page that asks for none is offered none", async () => {
  const relay = memoryRelay();
  const net = fakeRtc();
  const host = await startHost(relay, { directPath: net });
  host.onRequest((peer, text) => text);
  const page = await joinPage(relay, { invitationCode: host.invitationCode });
  try {
    assert.equal(await page.request("relayed"), "relayed");
    await delay(30);
    assert.equal(net.connections.length, 0);
    assert.equal((await host.waitForPeer()).route, "relayed");
  } finally {
    await page.close();
    await host.close();
  }
});

test("two networks that cannot reach each other cost the link nothing", async () => {
  const relay = memoryRelay();
  const net = fakeRtc({ connects: false });
  const direct = { RTCPeerConnection: net.RTCPeerConnection, _establishMs: 40 };
  const host = await startHost(relay, { directPath: direct });
  host.onRequest((peer, text) => text);
  const page = await joinPage(relay, { invitationCode: host.invitationCode, directPath: direct });
  const disconnected = [];
  page.events.addEventListener("disconnected", (event) => disconnected.push(event.detail));
  try {
    await until(() => net.connections.length === 2, "both ends to try");
    await until(() => net.connections.every((pc) => pc.connectionState === "closed"), "both ends to give up");
    assert.equal(await page.request("by the relay"), "by the relay");
    assert.equal(page.route, "relayed");
    assert.deepEqual(disconnected, []);
  } finally {
    await page.close();
    await host.close();
  }
});

test("a direct path that dies ends the session, and is offered again inside the next one", async () => {
  const relay = memoryRelay();
  const net = fakeRtc();
  const host = await startHost(relay, { directPath: net });
  host.onRequest((peer, text) => text);
  const direct = { RTCPeerConnection: net.RTCPeerConnection, _retryMs: 150 };
  const page = await joinPage(relay, { invitationCode: host.invitationCode, directPath: direct });
  const routes = routesOf(page.events);
  try {
    await until(() => page.route === "direct", "the page to go direct");
    net.cut();
    // What the channel had taken cannot be known, so the session is over; the
    // request is carried to the next one, as after any cut.
    assert.equal(await page.request("after the cut"), "after the cut");
    assert.equal(page.route, "relayed");
    assert.deepEqual(routes, ["relayed", "direct", "none", "relayed"]);

    // Not offered again straight away: a path that forms and dies would take
    // every session in turn.
    await delay(50);
    assert.equal(net.connections.length, 2);

    // But offered again once the wait is over, in the session that is up by
    // then rather than in whichever one happens to come after it.
    await until(() => page.route === "direct", "the path to be offered again");
    assert.deepEqual(routes, ["relayed", "direct", "none", "relayed", "direct"]);
    assert.equal(await page.request("direct again"), "direct again");
  } finally {
    await page.close();
    await host.close();
  }
});

test("a direct session outlives the relay connections under it", async () => {
  // Its records no longer cross the relay, and cutting the relay used to end
  // a session that was working, every time.
  const relay = memoryRelay();
  const net = fakeRtc();
  const store = memoryStore();
  const host = await startHost(relay, { directPath: net, store });
  host.onRequest((peer, text) => text);
  const page = await joinPage(relay, { invitationCode: host.invitationCode, directPath: net });
  const disconnected = [];
  page.events.addEventListener("disconnected", (e) => disconnected.push(e.detail));
  try {
    await until(() => page.route === "direct", "the page to go direct");
    const peer = await host.waitForPeer();
    for (const connection of relay.connections()) connection.close(new Error("the relay went away"));
    await delay(30);

    assert.equal(await page.request("still direct"), "still direct");
    assert.equal(page.route, "direct");
    assert.equal(peer.route, "direct");
    assert.deepEqual(disconnected, [], "a working direct session ended with its relay");
  } finally {
    await page.close();
    await host.close();
  }
});

test("a session the other end ended does not count against the path", async () => {
  // A host that restarts closes the channel while ICE is up: nothing to do
  // with the path, so the page goes straight back to it rather than waiting
  // out the backoff a failed path earns.
  const relay = memoryRelay();
  const net = fakeRtc();
  const store = memoryStore();
  let host = await startHost(relay, { directPath: net, store });
  host.onRequest((peer, text) => text);
  const page = await joinPage(relay, { invitationCode: host.invitationCode, directPath: net });
  try {
    await until(() => page.route === "direct", "the page to go direct");
    const routes = routesOf(page.events);
    await host.close();
    host = await startHost(relay, { directPath: net, store });
    host.onRequest((peer, text) => text);
    const restartedAt = Date.now();
    await until(() => routes.includes("none") && page.route === "direct", "the next session to go direct");
    assert.ok(Date.now() - restartedAt < 2_000, "the path was held back as if it had failed");
    assert.equal(await page.request("after the restart"), "after the restart");
  } finally {
    await page.close();
    await host.close();
  }
});

test("a page that closes its link is not reported as a lost path", async () => {
  const relay = memoryRelay();
  const net = fakeRtc();
  const host = await startHost(relay, { directPath: net });
  host.onRequest((peer, text) => text);
  const left = [];
  host.events.addEventListener("peer-left", (e) => left.push(e.detail.error?.message ?? ""));
  const page = await joinPage(relay, { invitationCode: host.invitationCode, directPath: net });
  try {
    await until(() => page.route === "direct", "the page to go direct");
    await page.close();
    await until(() => left.length > 0, "the host to see the page go");
    assert.doesNotMatch(left[0], /lost/, "a page that finished was logged as a failure");
  } finally {
    await page.close();
    await host.close();
  }
});

test("a host answers no offer before the pairing handshake has let the machine in", async () => {
  // The answer names the host's own addresses, and anyone who knows a host's
  // address can open a session.
  const net = fakeRtc();
  const dialerEphemeral = nacl.box.keyPair();
  const hostEphemeral = nacl.box.keyPair();
  const statics = { dialerPublic: nacl.box.keyPair().publicKey, hostPublic: nacl.box.keyPair().publicKey };
  const keysFor = (mine, theirs) =>
    deriveKeys({ ephemeralPrivate: mine.secretKey, peerEphemeral: theirs.publicKey, sessionId: 9n, ...statics });

  let hosted;
  const dialer = new Relay1Session({
    derp: { sendPacket: (_peer, record) => hosted._deliver(record) },
    peerPublic: statics.hostPublic,
    keys: await keysFor(dialerEphemeral, hostEphemeral),
    isDialer: true,
  });
  const answered = [];
  dialer.oncontrol = (payload) => answered.push(payload[0]);
  const hostSession = new Relay1Session({
    derp: { sendPacket: (_peer, record) => dialer.handleRecord(record) },
    peerPublic: statics.dialerPublic,
    keys: await keysFor(hostEphemeral, dialerEphemeral),
    isDialer: false,
  });
  hosted = new HostedConnection({
    session: hostSession,
    relay: { flush: async () => true },
    peer: statics.dialerPublic,
    directConfig: directPathConfig(net),
  });

  await dialer.sendControl(encodeSignal(DirectSignal.Offer, "fake offer 1"));
  await delay(20);
  assert.equal(net.connections.length, 0, "a stranger was answered");
  assert.deepEqual(answered, []);
  assert.equal(hosted.closed, false);

  hosted.trust();
  await dialer.sendControl(encodeSignal(DirectSignal.Offer, "fake offer 1"));
  await until(() => answered.includes(DirectSignal.Answer), "the host to answer once the machine is let in");
  assert.equal(net.connections.length, 1);
  hosted.close();
});

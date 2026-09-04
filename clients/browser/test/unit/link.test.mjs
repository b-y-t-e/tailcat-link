// The whole client, offline: pairing, requests in both directions, the
// reconnect loop, and the answers that must not be lost on the way out.
//
// Everything here is what the interop run against `tailcat-demo host` also
// covers, except that this needs neither a relay nor a host — so a regression
// in the reconnect loop or in the deduplication of a retried request fails a
// test run rather than waiting for somebody to notice by hand.

import assert from "node:assert/strict";
import test from "node:test";

import { TailcatLink } from "../../src/link.js";
import { PairingRefusedError } from "../../src/pairing-handshake.js";
import { memoryStore } from "../../src/store.js";
import { delay } from "../../src/bytes.js";
import { LoopbackHost, loopbackDialer } from "./loopback.mjs";
import { invitationCode } from "./invitation.mjs";

const APP = "link-tests";

// Short enough that a whole reconnection happens inside a test, long enough
// that nothing times out because a machine was busy.
const IMPATIENT = {
  requestTimeout: 300,
  requestDeadline: 5_000,
  heartbeatInterval: 40,
  minReconnectDelay: 5,
  maxReconnectDelay: 20,
  handshakeTimeout: 1_000,
};

/// Stands a link up against a host per attempt. `hostsFor` is given the
/// attempt number, so a session that refuses, one that dies and one that works
/// can be staged in order.
async function linkedTo(hostsFor, { store = memoryStore(), ...options } = {}) {
  const hosts = [];
  const connections = [];
  const link = await TailcatLink.join({
    appName: APP,
    invitationCode: invitationCode(),
    store,
    ...IMPATIENT,
    ...options,
    dial: loopbackDialer((connection, attempt) => {
      connections.push(connection);
      hosts.push(new LoopbackHost(connection, hostsFor(attempt)));
    }),
  });
  // Attached before anything is waited for: a link that gives up does so
  // during the first handshake, and a listener added afterwards would miss it.
  const events = [];
  for (const name of ["connected", "disconnected", "failed"]) {
    link.events.addEventListener(name, (event) => events.push({ name, detail: event.detail }));
  }

  // join() returns before the first dial has finished, so a test that reaches
  // for hosts[0] would otherwise race the connect it is about to exercise.
  await until(() => hosts.length > 0, "the first session to be dialled");
  return { link, hosts, connections, store, events };
}

const until = async (condition, what) => {
  for (let waited = 0; waited < 5_000; waited += 5) {
    if (condition()) return;
    await delay(5);
  }
  throw new Error(`gave up waiting for ${what}`);
};

const hangs = () => new Promise(() => {});

test("a request reaches the host and its answer comes back", async () => {
  const { link, hosts } = await linkedTo(() => ({}));
  try {
    assert.equal(await link.request("say something"), "SAY SOMETHING");
    assert.equal(hosts.length, 1, "one session was enough");
  } finally {
    await link.close();
  }
});

test("the host's own request is answered by this end", async () => {
  const { link, hosts } = await linkedTo(() => ({}));
  link.onRequest((text) => `the browser says: ${text}`);
  try {
    await hosts[0].paired.promise;
    assert.equal(await hosts[0].request("what time is it there?"), "the browser says: what time is it there?");
  } finally {
    await link.close();
  }
});

test("a notification is delivered without waiting for an answer", async () => {
  const { link, hosts } = await linkedTo(() => ({}));
  try {
    await link.notify("no answer wanted");
    await until(() => hosts[0].requests.length === 1, "the notification to arrive");
    assert.equal(hosts[0].requests[0].text, "no answer wanted");
  } finally {
    await link.close();
  }
});

test("a request outlives the session it started on, and is re-sent under the id it already had", async () => {
  // The first host takes the request and never answers; the second one does.
  const { link, hosts, connections } = await linkedTo((attempt) => (attempt === 0 ? { answer: hangs } : {}));
  try {
    const answer = link.request("try me twice");
    await until(() => hosts[0].requests.length === 1, "the first attempt to arrive");

    connections[0].cut();
    assert.equal(await answer, "TRY ME TWICE");

    assert.equal(hosts.length, 2, "the link built a second session");
    assert.equal(
      hosts[1].requests[0].exchange,
      hosts[0].requests[0].exchange,
      "the retry carried the id of the original, which is what stops the far end running it twice",
    );
  } finally {
    await link.close();
  }
});

test("a request the host retries after a session died is answered again but never run again", async () => {
  const { link, hosts, connections } = await linkedTo(() => ({}));
  let runs = 0;
  link.onRequest((text) => {
    runs++;
    return text.split("").reverse().join("");
  });

  try {
    await hosts[0].paired.promise;
    const exchange = new Uint8Array(16).fill(9);
    assert.equal(await hosts[0].requestAs(exchange, "stop"), "pots");

    connections[0].cut();
    await until(() => hosts.length === 2, "a second session");
    await hosts[1].paired.promise;

    assert.equal(await hosts[1].requestAs(exchange, "stop"), "pots", "the answer is remembered");
    assert.equal(runs, 1, "the handler ran once, which is the whole point of the ledger");
  } finally {
    await link.close();
  }
});

test("a host that stops answering the heartbeat loses the session, and the link builds another", async () => {
  const { link, hosts } = await linkedTo((attempt) => (attempt === 0 ? { answersPings: false } : {}));
  try {
    await until(() => hosts[0].pings > 0, "a heartbeat");
    // Nothing else is wrong with that session: writing into it still succeeds,
    // so the unanswered ping is the only symptom there is.
    await until(() => hosts.length === 2, "the session to be given up on and rebuilt");
    assert.equal(await link.request("still here?"), "STILL HERE?");
  } finally {
    await link.close();
  }
});

test("a host that refuses the pairing stops the link, and the code it refused is not kept", async () => {
  const store = memoryStore();
  const { link, hosts, events } = await linkedTo(() => ({ pairingToken: "some other machine's" }), { store });

  // Every caller is told, rather than left to a deadline that says nothing.
  await assert.rejects(link.request("let me in"), (error) => error instanceof PairingRefusedError);
  await until(() => events.some((event) => event.name === "failed"), "the failure to be announced");
  assert.ok(events.find((event) => event.name === "failed").detail instanceof PairingRefusedError);

  assert.equal(await store.load(APP), null, "a refused code would otherwise be offered again on the next load");
  assert.equal(hosts.length, 1, "asking the same rejected question again would only be refused again");
});

test("closing lets an answer already produced leave before the session goes", async () => {
  const { link, hosts } = await linkedTo(() => ({}));
  let running = false;
  link.onRequest(async (text) => {
    running = true;
    await delay(60);
    return `slowly: ${text}`;
  });

  try {
    await hosts[0].paired.promise;
    const asked = hosts[0].request("wait for me");
    // The handler has begun but has not returned: closing in that window is
    // what turns a delivered answer into silence the host can only time out.
    await until(() => running, "the handler to start");

    await link.close();
    assert.equal(await asked, "slowly: wait for me");
  } finally {
    await link.close();
  }
});

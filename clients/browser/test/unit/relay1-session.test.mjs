// A whole relay1 session between two ends, with the relay replaced by a
// function call.
//
// The interop run does this against a live relay and a .NET host; this does
// the parts that are ours either way — stream ids, the counter, chunking and
// the credit window — where they can fail in CI instead of by hand.

import assert from "node:assert/strict";
import test from "node:test";

import { Relay1Session, Relay1Stream, MAX_PLAINTEXT, deriveKeys } from "../../src/relay1.js";
import { nacl } from "../../src/nacl.js";
import { str, utf8 } from "../../src/bytes.js";

const SESSION_ID = 0x0102030405060708n;

async function sessionKeys() {
  const dialerEphemeral = nacl.box.keyPair();
  const hostEphemeral = nacl.box.keyPair();
  const dialerStatic = nacl.box.keyPair().publicKey;
  const hostStatic = nacl.box.keyPair().publicKey;

  const derive = (ephemeral, peer) =>
    deriveKeys({
      ephemeralPrivate: ephemeral.secretKey,
      peerEphemeral: peer.publicKey,
      sessionId: SESSION_ID,
      dialerPublic: dialerStatic,
      hostPublic: hostStatic,
    });

  return {
    dialer: await derive(dialerEphemeral, hostEphemeral),
    host: await derive(hostEphemeral, dialerEphemeral),
  };
}

/// Two sessions wired to each other. `drop` decides which records the relay
/// loses, which is the one failure relay1 has no answer to.
async function connectedPair({ drop = () => false } = {}) {
  const keys = await sessionKeys();
  const sent = { dialer: 0, host: 0 };

  const relayTo = (peer, from) => ({
    sendPacket: (_peerPublic, record) => {
      if (drop(from, sent[from]++)) return;
      // Records reach the far end in the order they were handed over, which is
      // the only ordering guarantee relay1 has and the one its counter checks.
      peer.queue = (peer.queue ?? Promise.resolve()).then(() => peer.session.handleRecord(record));
    },
  });

  const dialerSide = {};
  const hostSide = {};
  const dialer = new Relay1Session({
    derp: relayTo(hostSide, "dialer"),
    peerPublic: new Uint8Array(32),
    keys: keys.dialer,
    isDialer: true,
  });
  const host = new Relay1Session({
    derp: relayTo(dialerSide, "host"),
    peerPublic: new Uint8Array(32),
    keys: keys.host,
    isDialer: false,
  });
  dialerSide.session = dialer;
  hostSide.session = host;

  const settled = () => Promise.all([dialerSide.queue, hostSide.queue]);
  return { dialer, host, settled };
}

async function readAll(stream) {
  const parts = [];
  for (;;) {
    const chunk = await stream.read();
    if (!chunk.length) return parts.join("");
    parts.push(str(chunk));
  }
}

test("the two ends derive keys that open each other's records", async () => {
  const { dialer, host, settled } = await connectedPair();

  const out = dialer.openStream();
  await out.write(utf8("hello"));
  await out.finish();
  await settled();

  const accepted = await host.accepted.next();
  assert.equal(await readAll(accepted), "hello");
});

test("a dialler opens odd stream ids and a host even ones", async () => {
  const { dialer, host, settled } = await connectedPair();

  assert.deepEqual([dialer.openStream().id, dialer.openStream().id], [1, 3]);

  const fromHost = host.openStream();
  assert.equal(fromHost.id, 2);
  await fromHost.write(utf8("."));
  await settled();
  assert.equal((await dialer.accepted.next()).id, 2);
});

test("a payload larger than one record crosses in order and whole", async () => {
  const { dialer, host, settled } = await connectedPair();

  const payload = "x".repeat(MAX_PLAINTEXT * 2 + 17);
  const out = dialer.openStream();
  const writing = out.write(utf8(payload)).then(() => out.finish());

  const accepted = await host.accepted.next();
  const reading = readAll(accepted);
  await writing;
  await settled();

  assert.equal(await reading, payload);
});

test("a transfer past the initial window waits for credit and then continues", async () => {
  const { dialer, host, settled } = await connectedPair();

  const payload = "y".repeat(Relay1Stream.INITIAL_WINDOW * 2);
  const out = dialer.openStream();
  const writing = out.write(utf8(payload)).then(() => out.finish());

  const accepted = await host.accepted.next();
  // Reading is what grants credit back; without it the write above stops at
  // the initial window and never finishes.
  const received = await readAll(accepted);
  await writing;
  await settled();

  assert.equal(received.length, payload.length);
});

test("a dropped record ends the session rather than leaving a hole in a stream", async () => {
  // The second record the dialler sends, so the stream is already open when
  // the gap appears.
  const { dialer, host, settled } = await connectedPair({ drop: (from, index) => from === "dialer" && index === 1 });

  const out = dialer.openStream();
  await out.write(utf8("first"));
  await out.write(utf8("lost"));
  await out.write(utf8("third"));
  await settled();

  assert.equal(dialer.closed, false, "the sending end sees nothing wrong");
  assert.equal(host.closed, true);

  // The reader is told, rather than "lost" silently becoming "third": a hole
  // in the middle of a message is not something the layer above can be handed.
  const accepted = await host.accepted.next();
  await assert.rejects(accepted.read(), /one was dropped/);
});

test("a session that ends takes its open streams with it", async () => {
  const { dialer, host, settled } = await connectedPair();

  const out = dialer.openStream();
  await out.write(utf8("before"));
  await settled();
  const accepted = await host.accepted.next();
  assert.equal(str(await accepted.read()), "before");

  // Otherwise a reader waits for good: a relayed session stays writable after
  // the far end has gone, so silence is what a broken link looks like.
  host.close(new Error("the relay connection dropped"));
  await assert.rejects(accepted.read(), /relay connection dropped/);
});

test("closing a session refuses further streams instead of writing into nothing", async () => {
  const { dialer } = await connectedPair();

  const out = dialer.openStream();
  dialer.close(new Error("closed for the test"));

  assert.throws(() => dialer.openStream(), /closed for the test/);
  await assert.rejects(out.write(utf8("late")), /closed for the test/);
});

test("a FIN that arrives after this end closed the stream does not open a second one", async () => {
  // The order every host request takes: it writes the request, this end
  // answers and closes, and only then does the host's own FIN arrive — for an
  // id this end has already forgotten.
  const { dialer, host, settled } = await connectedPair();

  const asking = host.openStream();
  await asking.write(utf8("status"));
  await settled();

  const served = await dialer.accepted.next();
  await served.write(utf8("STATUS"));
  await served.close();
  await settled();

  await asking.finish();
  await settled();

  const nothingElse = Symbol("nothing else");
  const next = await Promise.race([dialer.accepted.next(), Promise.resolve(nothingElse)]);
  assert.equal(next, nothingElse);
});

test("streams the peer opened out of order are both accepted", async () => {
  // The .NET end numbers a stream before it takes the lock that serialises
  // its sending, so two concurrent requests can reach the relay with the
  // higher id first. Neither may be dropped for being below the other.
  const { dialer, host, settled } = await connectedPair();

  const later = host.openStream();
  const earlier = host.openStream();
  assert.deepEqual([later.id, earlier.id], [2, 4]);

  await earlier.write(utf8("second"));
  await later.write(utf8("first"));
  await settled();

  const first = await dialer.accepted.next();
  const second = await dialer.accepted.next();
  assert.deepEqual([first.id, second.id], [4, 2]);
  assert.equal(str(await first.read()), "second");
  assert.equal(str(await second.read()), "first");
});

test("a FIN for the older of two out-of-order streams does not reopen it", async () => {
  const { dialer, host, settled } = await connectedPair();

  const opened = [host.openStream(), host.openStream()];
  for (const stream of opened.reverse()) await stream.write(utf8("."));
  await settled();

  for (let i = 0; i < 2; i++) await (await dialer.accepted.next()).close();
  await settled();

  for (const stream of opened) await stream.finish();
  await settled();

  const nothingElse = Symbol("nothing else");
  assert.equal(await Promise.race([dialer.accepted.next(), Promise.resolve(nothingElse)]), nothingElse);
});

// The relay connection, with the WebSocket replaced by a fake that delivers
// what a relay would.
//
// The interop run does this against a live relay; this does the parts that
// are ours either way — frame cutting and what is refused — where they can
// fail in CI instead of by hand.

import assert from "node:assert/strict";
import test from "node:test";

import { concat, str, u32be, utf8 } from "../../src/bytes.js";
import { DERP_LIVENESS, DerpConnection, DerpFrame } from "../../src/derp.js";
import { nacl } from "../../src/nacl.js";

class FakeSocket {
  binaryType = "";
  onmessage = null;
  onclose = null;
  onerror = null;
  bufferedAmount = 0;
  sent = [];
  closed = false;

  send(data) {
    this.sent.push(data);
  }

  close() {
    this.closed = true;
  }

  // Puts bytes on the connection the way a WebSocket message would.
  deliver(bytes) {
    this.onmessage?.({ data: bytes.buffer });
  }

  frame(type, payload) {
    this.deliver(concat(new Uint8Array([type]), u32be(payload.length), payload));
  }
}

const connection = (options) => {
  const socket = new FakeSocket();
  const keys = nacl.box.keyPair();
  return { socket, conn: new DerpConnection(socket, keys.secretKey, keys.publicKey, options) };
};

// The frame types the connection wrote, in order.
const sentTypes = (socket) => socket.sent.map((frame) => frame[0]);

// A clock the test moves by hand. Date is mocked too, because the connection
// measures silence with it.
function withClock(t) {
  t.mock.timers.enable({ apis: ["setInterval", "Date"] });
  return (ms) => t.mock.timers.tick(ms);
}

test("a frame within the limit still arrives", async () => {
  const { socket, conn } = connection();
  const source = new Uint8Array(32).fill(9);
  socket.frame(DerpFrame.RecvPacket, concat(source, utf8("small")));

  const packet = await conn.packets.next();
  assert.deepEqual(packet.source, source);
  assert.equal(str(packet.payload), "small");
});

test("a frame split across messages still arrives whole", async () => {
  const { socket, conn } = connection();
  const payload = concat(new Uint8Array(32).fill(1), utf8("split"));
  const whole = concat(new Uint8Array([DerpFrame.RecvPacket]), u32be(payload.length), payload);
  socket.deliver(whole.slice(0, 7));
  socket.deliver(whole.slice(7));

  const packet = await conn.packets.next();
  assert.equal(str(packet.payload), "split");
});

test("a relay announcing an oversized frame is cut off rather than buffered", () => {
  const { socket, conn } = connection();
  const closed = [];
  conn.onclose = (error) => closed.push(error);

  // A header naming more bytes than any honest frame could carry. Buffering
  // for it is the attack: a hostile relay can dribble bytes forever and the
  // tab's memory goes with them. The .NET half treats the same length as a
  // hostile peer; so does this.
  socket.deliver(concat(new Uint8Array([DerpFrame.RecvPacket]), u32be(0xffffffff)));

  assert.equal(conn.closed, true);
  assert.equal(socket.closed, true, "the socket goes, not just the state");
  assert.match(closed[0].message, /announced a 4294967295-byte frame, over the \d+ limit/);
  assert.equal(conn.packets.length, 0);
});

test("bytes that arrive after the oversized frame are not buffered", () => {
  const { socket, conn } = connection();
  conn.onclose = () => {};

  socket.deliver(concat(new Uint8Array([DerpFrame.RecvPacket]), u32be(0xffffffff)));
  // Whatever the relay put on the wire before it saw the close.
  socket.frame(DerpFrame.RecvPacket, concat(new Uint8Array(32), utf8("late")));

  assert.equal(conn.packets.length, 0);
});

test("a connection that got nothing back for a packet asks the relay, and goes when nothing answers", (t) => {
  const tick = withClock(t);
  const { socket, conn } = connection();
  const closed = [];
  conn.onclose = (error) => closed.push(error);

  conn.sendPacket(new Uint8Array(32), utf8("into a flow a firewall forgot"));
  tick(DERP_LIVENESS.probeAfterSendMs);
  assert.deepEqual(sentTypes(socket), [DerpFrame.SendPacket, DerpFrame.Ping]);

  tick(DERP_LIVENESS.timeoutMs - DERP_LIVENESS.checkIntervalMs);
  assert.equal(conn.closed, false, "not before the timeout");

  tick(DERP_LIVENESS.checkIntervalMs);
  assert.equal(conn.closed, true);
  assert.equal(socket.closed, true, "the socket goes, so the link dials a new one");
  assert.match(closed[0].message, /went silent/);
});

test("any frame answers the ping, not only a pong", (t) => {
  const tick = withClock(t);
  const { socket, conn } = connection();

  conn.sendPacket(new Uint8Array(32), utf8("hello"));
  tick(DERP_LIVENESS.probeAfterSendMs);
  socket.frame(DerpFrame.KeepAlive, new Uint8Array(0));
  tick(DERP_LIVENESS.timeoutMs * 3);

  assert.equal(conn.closed, false);
});

test("an idle connection is asked only after the longer wait", (t) => {
  const tick = withClock(t);
  const { socket, conn } = connection();

  tick(DERP_LIVENESS.probeWhenIdleMs - DERP_LIVENESS.checkIntervalMs);
  assert.deepEqual(sentTypes(socket), []);

  tick(DERP_LIVENESS.checkIntervalMs);
  assert.deepEqual(sentTypes(socket), [DerpFrame.Ping]);
  socket.frame(DerpFrame.Pong, socket.sent[0].slice(5));
  tick(DERP_LIVENESS.timeoutMs * 2);
  assert.equal(conn.closed, false);
});

test("a ping queued behind a large upload is timed from when it leaves the socket", (t) => {
  // A slow uplink with a relay1 transfer on it holds the ping in the
  // WebSocket's buffer; the relay cannot answer what it has not been sent.
  const tick = withClock(t);
  const { socket, conn } = connection();

  conn.sendPacket(new Uint8Array(32), new Uint8Array(30_000));
  socket.bufferedAmount = 30_000;
  tick(DERP_LIVENESS.probeAfterSendMs);
  socket.bufferedAmount += 13; // the ping, behind the upload

  tick(DERP_LIVENESS.timeoutMs - DERP_LIVENESS.checkIntervalMs);
  socket.bufferedAmount = 0; // it all left, the ping last
  tick(DERP_LIVENESS.checkIntervalMs);
  tick(DERP_LIVENESS.timeoutMs - DERP_LIVENESS.checkIntervalMs);
  assert.equal(conn.closed, false, "still inside the timeout counted from leaving");

  tick(DERP_LIVENESS.checkIntervalMs);
  assert.equal(conn.closed, true);
});

test("a ping that cannot leave the socket at all is the same verdict", (t) => {
  const tick = withClock(t);
  const { socket, conn } = connection();

  conn.sendPacket(new Uint8Array(32), new Uint8Array(30_000));
  socket.bufferedAmount = 30_000;
  tick(DERP_LIVENESS.probeAfterSendMs);
  socket.bufferedAmount += 13;
  tick(DERP_LIVENESS.timeoutMs);

  assert.equal(conn.closed, true);
});

test("a closed connection stops asking", (t) => {
  const tick = withClock(t);
  const { socket, conn } = connection();

  conn.close();
  tick(DERP_LIVENESS.probeWhenIdleMs * 2);

  assert.deepEqual(sentTypes(socket), []);
});

test("liveness can be turned off", (t) => {
  const tick = withClock(t);
  const { socket, conn } = connection({ liveness: null });

  conn.sendPacket(new Uint8Array(32), utf8("hello"));
  tick(DERP_LIVENESS.probeWhenIdleMs * 2);

  assert.deepEqual(sentTypes(socket), [DerpFrame.SendPacket]);
  assert.equal(conn.closed, false);
});

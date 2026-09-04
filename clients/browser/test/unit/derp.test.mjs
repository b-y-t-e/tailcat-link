// The relay connection, with the WebSocket replaced by a fake that delivers
// what a relay would.
//
// The interop run does this against a live relay; this does the parts that
// are ours either way — frame cutting and what is refused — where they can
// fail in CI instead of by hand.

import assert from "node:assert/strict";
import test from "node:test";

import { concat, str, u32be, utf8 } from "../../src/bytes.js";
import { DerpConnection, DerpFrame } from "../../src/derp.js";
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

const connection = () => {
  const socket = new FakeSocket();
  const keys = nacl.box.keyPair();
  return { socket, conn: new DerpConnection(socket, keys.secretKey, keys.publicKey) };
};

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

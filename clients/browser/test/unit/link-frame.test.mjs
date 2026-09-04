// The frames the application layer exchanges, over a stream that is nothing
// but a byte queue.

import assert from "node:assert/strict";
import test from "node:test";

import { concat, str, utf8 } from "../../src/bytes.js";
import {
  FrameKind,
  MAX_PAYLOAD_BYTES,
  ensureSendable,
  newExchange,
  readFrame,
  writeFrame,
} from "../../src/link-frame.js";

/// Stands in for a relay1 stream: everything written can be read back, a chunk
/// at a time, which is all readFrame is entitled to assume.
class LoopbackStream {
  #buffered = new Uint8Array(0);

  async write(payload) {
    this.#buffered = concat(this.#buffered, payload);
  }

  async read(_signal, limit = Infinity) {
    const taken = Math.min(this.#buffered.length, limit);
    const chunk = this.#buffered.subarray(0, taken);
    this.#buffered = this.#buffered.subarray(taken);
    return chunk;
  }

  async readExactly(count) {
    const chunk = await this.read(null, count);
    if (chunk.length < count) throw new Error(`the peer stopped after ${chunk.length} of ${count} bytes`);
    return chunk;
  }
}

test("a frame round-trips with its kind, exchange id and payload", async () => {
  const stream = new LoopbackStream();
  const exchange = newExchange();
  await writeFrame(stream, FrameKind.Request, exchange, utf8("zażółć gęślą jaźń"));

  const frame = await readFrame(stream);
  assert.equal(frame.tag, FrameKind.Request);
  assert.deepEqual(frame.exchange, exchange);
  assert.equal(str(frame.payload), "zażółć gęślą jaźń");
});

test("an empty payload is a frame like any other", async () => {
  const stream = new LoopbackStream();
  await writeFrame(stream, FrameKind.Ping, newExchange(), new Uint8Array(0));
  assert.equal((await readFrame(stream)).payload.length, 0);
});

test("a payload written in several chunks arrives as one", async () => {
  const stream = new LoopbackStream();
  const payload = new Uint8Array(200_000).fill(0x2a);
  await writeFrame(stream, FrameKind.Notify, newExchange(), payload);
  assert.deepEqual((await readFrame(stream)).payload, payload);
});

test("a payload over the cap is refused by the sender, with no session involved", () => {
  assert.throws(() => ensureSendable(new Uint8Array(MAX_PAYLOAD_BYTES + 1)), /at most/);
  ensureSendable(new Uint8Array(MAX_PAYLOAD_BYTES));
});

test("an announced length over the cap is refused before anything is allocated for it", async () => {
  const stream = new LoopbackStream();
  // A header claiming 4 GiB, which no honest peer sends and which a reader
  // that trusted it would wait for.
  await stream.write(concat(new Uint8Array([FrameKind.Request]), newExchange(), Uint8Array.of(255, 255, 255, 255)));
  await assert.rejects(readFrame(stream), /the limit is/);
});

test("a peer that stops mid-payload is reported, not waited on for good", async () => {
  const stream = new LoopbackStream();
  await stream.write(concat(new Uint8Array([FrameKind.Request]), newExchange(), Uint8Array.of(0, 0, 0, 8)));
  await stream.write(utf8("half"));
  await assert.rejects(readFrame(stream), /stopped after 4 of 8 bytes/);
});

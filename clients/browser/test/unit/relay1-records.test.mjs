// The record and frame layers, against the vectors the .NET side also reads.
//
// Everything here is arithmetic and AES-GCM: no relay, no host, no network.
// It is what catches a varint or a nonce drifting apart from Relay1Protocol.cs
// between one manual interop run and the next.

import assert from "node:assert/strict";
import test from "node:test";

import { decodeHello, encodeHello } from "../../src/peer.js";
import { encodeFrame, keySchedule, openRecord, readVarint, sealRecord, writeVarint } from "../../src/relay1.js";
import { hex, recordVectors, unhex } from "./vectors.mjs";

const importKey = (keyHex) =>
  crypto.subtle.importKey("raw", unhex(keyHex), "AES-GCM", false, ["encrypt", "decrypt"]);

test("a varint round-trips at each width the wire uses", () => {
  for (const [value, width] of [
    [0, 1],
    [1, 1],
    [127, 1],
    [128, 2],
    [16383, 2],
    [16384, 3],
    [Number.MAX_SAFE_INTEGER, 8],
  ]) {
    const encoded = writeVarint(value);
    assert.equal(encoded.length, width, `${value} should take ${width} bytes`);
    assert.deepEqual(readVarint(encoded), { value, read: width });
  }
});

test("a varint that never ends is refused rather than read past its frame", () => {
  assert.throws(() => readVarint(new Uint8Array([0x80, 0x80])), /runs past the end/);
  assert.throws(() => readVarint(new Uint8Array(12).fill(0x80)), /too long/);
});

test("frames are laid out as the shared vectors say", () => {
  for (const vector of recordVectors.cases) {
    const frame = encodeFrame(vector.streamId, vector.flags, unhex(vector.payloadHex));
    assert.equal(hex(frame), vector.frameHex, vector.name);
  }
});

test("records are sealed as the shared vectors say", async () => {
  const key = await importKey(recordVectors.keyHex);
  for (const vector of recordVectors.cases) {
    const record = await sealRecord(unhex(vector.frameHex), key, BigInt(vector.counter));
    assert.equal(hex(record), vector.recordHex, vector.name);
  }
});

test("a sealed record opens with its counter and plaintext intact", async () => {
  const key = await importKey(recordVectors.keyHex);
  for (const vector of recordVectors.cases) {
    const opened = await openRecord(unhex(vector.recordHex), key);
    assert.equal(opened.counter, BigInt(vector.counter), vector.name);
    assert.equal(hex(opened.plaintext), vector.frameHex, vector.name);
  }
});

test("a record altered in flight does not open", async () => {
  const key = await importKey(recordVectors.keyHex);
  const record = unhex(recordVectors.cases[0].recordHex);
  record[record.length - 1] ^= 0x01;
  await assert.rejects(openRecord(record, key));
});

test("a record too short to hold a counter and a tag is refused", async () => {
  const key = await importKey(recordVectors.keyHex);
  await assert.rejects(openRecord(new Uint8Array(8), key), /cannot be one/);
});

test("the key schedule agrees with the shared vector", async () => {
  const vector = recordVectors.keySchedule;
  const keys = await keySchedule({
    shared: unhex(vector.sharedHex),
    sessionId: BigInt(vector.sessionId),
    dialerPublic: unhex(vector.dialerPublicHex),
    hostPublic: unhex(vector.hostPublicHex),
  });

  assert.equal(hex(keys.dialerToHost), vector.dialerToHostHex, "dialer to host");
  assert.equal(hex(keys.hostToDialer), vector.hostToDialerHex, "host to dialer");
});

test("the two directions never share a key, so a nonce cannot be reused", async () => {
  const vector = recordVectors.keySchedule;
  const keys = await keySchedule({
    shared: unhex(vector.sharedHex),
    sessionId: BigInt(vector.sessionId),
    dialerPublic: unhex(vector.dialerPublicHex),
    hostPublic: unhex(vector.hostPublicHex),
  });

  assert.notEqual(hex(keys.dialerToHost), hex(keys.hostToDialer));
});

test("a hello is laid out as the shared vectors say", () => {
  for (const vector of recordVectors.helloCases.filter((c) => c.encodedByBrowser)) {
    const hello = encodeHello({
      sessionId: BigInt(vector.sessionId),
      homeRegionId: vector.homeRegionId,
      transports: vector.transports,
      ephemeral: vector.ephemeralHex ? unhex(vector.ephemeralHex) : null,
    });
    assert.equal(hex(hello), vector.helloHex, vector.name);
  }
});

test("a hello the .NET side wrote reads back field for field", () => {
  for (const vector of recordVectors.helloCases) {
    const hello = decodeHello(unhex(vector.helloHex));
    assert.equal(hello.homeRegionId, vector.homeRegionId, vector.name);
    assert.deepEqual(hello.transports, vector.transports, vector.name);
    assert.equal(hello.ephemeral ? hex(hello.ephemeral) : null, vector.ephemeralHex, vector.name);
    assert.deepEqual(
      hello.endpoints.map((address) => hex(address)),
      vector.endpoints.map((endpoint) => endpoint.addressHex),
      vector.name,
    );
  }
});

// The link layer's own wire formats, against the vectors the .NET side reads.
//
// `LinkVectorTests.cs` asserts the same file. A round trip inside one
// implementation stays green through a change of prefix width, field order or
// endianness that would leave every browser refused, so these check the bytes
// instead — the same job `relay1-records.test.mjs` does one layer down.

import assert from "node:assert/strict";
import test from "node:test";

import { str, utf8 } from "../../src/bytes.js";
import {
  MAX_CHANNEL_FRAME_BYTES,
  MAX_CHANNEL_NAME_BYTES,
  ChannelReader,
  ChannelWriter,
  decodeChannelName,
  encodeChannelName,
} from "../../src/link-channel.js";
import { MAX_DISPLAY_NAME_BYTES, decodeLinkHello, encodeLinkHello } from "../../src/link-hello.js";
import { hex, linkVectors, unhex } from "./vectors.mjs";

/// Collects what a channel writes, which is all a vector needs of a stream.
const sink = () => {
  const written = [];
  return { written, write: async (bytes) => void written.push(bytes), close: async () => {} };
};

/// Serves bytes to a channel reader, and then behaves as a stream that ended.
const source = (bytes) => {
  let at = 0;
  return {
    readExactly: async (n) => {
      if (at + n > bytes.length) throw new Error("the stream ended");
      const slice = bytes.subarray(at, at + n);
      at += n;
      return slice;
    },
    close: async () => {},
  };
};

test("a hello is written as the .NET side reads it", () => {
  for (const vector of linkVectors.hellos) {
    const encoded = encodeLinkHello({
      pairingToken: vector.pairingToken,
      displayName: vector.displayName,
    });
    assert.equal(hex(encoded), vector.encodedHex, vector.name);
  }
});

test("a hello is read as the .NET side writes it", () => {
  for (const vector of [...linkVectors.hellos, ...linkVectors.legacyHellos]) {
    assert.deepEqual(
      decodeLinkHello(unhex(vector.encodedHex)),
      { pairingToken: vector.pairingToken, displayName: vector.displayName },
      vector.name,
    );
  }
});

test("the version byte is the one that tells the two shapes apart", () => {
  const [envelope] = linkVectors.hellos;
  const [bare] = linkVectors.legacyHellos;

  assert.equal(unhex(envelope.encodedHex)[0], linkVectors.helloVersionByte);
  assert.notEqual(unhex(bare.encodedHex)[0], linkVectors.helloVersionByte);
});

test("a channel name goes on the wire as its UTF-8 bytes", () => {
  for (const vector of linkVectors.channelNames) {
    assert.equal(hex(encodeChannelName(vector.channelName)), vector.encodedHex, vector.name);
    assert.equal(decodeChannelName(unhex(vector.encodedHex)), vector.channelName, vector.name);
  }
});

test("a channel frame carries its length in front of it, big-endian", async () => {
  for (const vector of linkVectors.channelFrames) {
    const payload = unhex(vector.payloadHex);
    const stream = sink();
    const writer = new ChannelWriter("audio", stream);

    // The empty one is not a frame anybody sends: it is the marker a channel
    // closed on purpose writes, so it is produced the only way it ever is.
    if (payload.length) {
      await writer.send(payload);
    } else {
      await writer.close();
    }

    assert.equal(hex(stream.written.at(-1)), vector.frameHex, vector.name);
  }
});

test("a channel frame is read back as the .NET side wrote it", async () => {
  const carrying = linkVectors.channelFrames.filter((vector) => vector.payloadHex.length);
  const ending = linkVectors.channelFrames.find((vector) => !vector.payloadHex.length);
  const bytes = unhex(carrying.map((vector) => vector.frameHex).join("") + ending.frameHex);

  const read = [];
  for await (const frame of new ChannelReader("audio", source(bytes)).read()) {
    read.push(hex(frame));
  }

  assert.deepEqual(read, carrying.map((vector) => vector.payloadHex));
});

test("both sides bound a name and a frame at the same size", () => {
  assert.equal(MAX_DISPLAY_NAME_BYTES, linkVectors.limits.maxDisplayNameBytes);
  assert.equal(MAX_CHANNEL_NAME_BYTES, linkVectors.limits.maxChannelNameBytes);
  assert.equal(MAX_CHANNEL_FRAME_BYTES, linkVectors.limits.maxChannelFrameBytes);
});

test("a name at the limit fits and one byte past it does not", () => {
  const atTheLimit = "n".repeat(MAX_CHANNEL_NAME_BYTES);

  assert.equal(str(encodeChannelName(atTheLimit)), atTheLimit);
  assert.equal(utf8(atTheLimit).length, MAX_CHANNEL_NAME_BYTES);
  assert.throws(() => encodeChannelName(atTheLimit + "n"), /1 to 256 bytes/);
});

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
  MAX_CHANNEL_NAME_BYTES,
  ChannelReader,
  ChannelWriter,
  decodeChannelName,
  encodeChannelName,
} from "../../src/link-channel.js";
import {
  Capabilities,
  HELLO_FRAME_BYTES,
  THIS_CLIENT,
  decodeCapabilities,
  encodeCapabilities,
} from "../../src/link-frame.js";
import { MAX_DISPLAY_NAME_BYTES, decodeLinkHello, encodeLinkHello } from "../../src/link-hello.js";
import { hex, linkVectors, unhex } from "./vectors.mjs";
import {
  BLOCK_BYTES,
  ExchangeFlags,
  decodeAnswerHeader,
  decodeExchangeHeader,
  decodeOffset,
  encodeAnswerHeader,
  encodeExchangeHeader,
  encodeOffset,
  writeBlocks,
} from "../../src/exchange-frame.js";
import { IncomingContent } from "../../src/incoming-content.js";

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

test("both sides agree on the bounds that are left", () => {
  assert.equal(MAX_DISPLAY_NAME_BYTES, linkVectors.limits.maxDisplayNameBytes);
  assert.equal(MAX_CHANNEL_NAME_BYTES, linkVectors.limits.maxChannelNameBytes);
  assert.equal(HELLO_FRAME_BYTES, linkVectors.limits.helloFrameBytes);
  // Also what decides whether content is pipelined, which both ends must agree on.
  assert.equal(BLOCK_BYTES, linkVectors.limits.blockBytes);
});

test("a ping answer reads as the shared vectors say, and this client says what the .NET library says", () => {
  assert.equal(Capabilities.LargeFrames, linkVectors.capabilities.largeFrames);
  assert.equal(Capabilities.Exchanges, linkVectors.capabilities.exchanges);
  for (const vector of linkVectors.capabilities.pingAnswers) {
    assert.equal(decodeCapabilities(unhex(vector.answerHex)), vector.capabilities, vector.name);
  }
  // The same as the .NET library, which is what makes a .NET host send this
  // client exchanges rather than single frames.
  assert.equal(hex(encodeCapabilities(THIS_CLIENT)), "03");
});

test("an exchange header is written as the .NET side reads it, and read as it writes it", () => {
  assert.equal(ExchangeFlags.Answer, linkVectors.exchangeFlags.answer);
  assert.equal(ExchangeFlags.Transfer, linkVectors.exchangeFlags.transfer);
  assert.equal(ExchangeFlags.Pipelined, linkVectors.exchangeFlags.pipelined);
  assert.equal(ExchangeFlags.AckOnDelivery, linkVectors.exchangeFlags.ackOnDelivery);

  for (const vector of linkVectors.exchangeHeaders) {
    const header = {
      flags: vector.flags,
      length: vector.length,
      answerOffset: vector.answerOffset,
      name: vector.contentName,
      contentType: vector.contentType,
      metadata: unhex(vector.metadataHex),
    };
    assert.equal(hex(encodeExchangeHeader(header)), vector.encodedHex, vector.name);

    const read = decodeExchangeHeader(unhex(vector.encodedHex));
    assert.deepEqual({ ...read, metadata: hex(read.metadata) }, { ...header, metadata: vector.metadataHex }, vector.name);
  }
});

test("an answer header is written as the .NET side reads it, and read as it writes it", () => {
  for (const vector of linkVectors.answerHeaders) {
    const header = { length: vector.length, contentType: vector.contentType, metadata: unhex(vector.metadataHex) };
    assert.equal(hex(encodeAnswerHeader(header)), vector.encodedHex, vector.name);

    const read = decodeAnswerHeader(unhex(vector.encodedHex));
    assert.deepEqual({ ...read, metadata: hex(read.metadata) }, { ...header, metadata: vector.metadataHex }, vector.name);
  }
});

test("an offset is eight bytes, big-endian, past what 32 bits can count", () => {
  for (const vector of linkVectors.exchangeOffsets) {
    assert.equal(hex(encodeOffset(vector.offset)), vector.encodedHex, vector.name);
    assert.equal(decodeOffset(unhex(vector.encodedHex), null), vector.offset, vector.name);
  }
});

test("content goes out in blocks the .NET side reads, and comes back from blocks it writes", async () => {
  for (const vector of linkVectors.exchangeBlocks) {
    const out = sink();
    await writeBlocks(out, unhex(vector.contentHex), 0);
    assert.equal(out.written.map(hex).join(""), vector.encodedHex, vector.name);

    const content = new IncomingContent({ id: new Uint8Array(16), length: unhex(vector.contentHex).length });
    await content.readBody(source(unhex(vector.encodedHex)), 0);
    assert.equal(hex(content.bytes), vector.contentHex, vector.name);
  }
});

test("a channel frame has no limit of its own", async () => {
  const frame = new Uint8Array(1024 * 1024);

  const out = sink();
  await new ChannelWriter("video", out).send(frame);
  assert.equal(out.written.reduce((total, bytes) => total + bytes.length, 0), 4 + frame.length);
});

test("a name at the limit fits and one byte past it does not", () => {
  const atTheLimit = "n".repeat(MAX_CHANNEL_NAME_BYTES);

  assert.equal(str(encodeChannelName(atTheLimit)), atTheLimit);
  assert.equal(utf8(atTheLimit).length, MAX_CHANNEL_NAME_BYTES);
  assert.throws(() => encodeChannelName(atTheLimit + "n"), /1 to 256 bytes/);
});

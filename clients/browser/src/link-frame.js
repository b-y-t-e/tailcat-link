// The layer the application actually speaks: one request per stream, with a
// length prefix, an exchange id, and an answer.
//
// The exchange id is not for routing — each exchange gets its own stream, so
// nothing needs demultiplexing here — but for identity across sessions. A
// request re-sent after a session died carries the id of the original, which
// is how the far end recognises it as the same request rather than a second
// one and answers from memory instead of running it again.

import { concat, randomBytes, readU32be, u32be } from "./bytes.js";

export const FrameKind = {
  Request: 1,
  Notify: 2,
  Ping: 3,
  Hello: 4,
};

export const FrameStatus = {
  Ok: 0,
  Failed: 1,
};

export const MAX_PAYLOAD_BYTES = 16 * 1024 * 1024;
const EXCHANGE_LEN = 16;
const HEADER_LENGTH = 1 + EXCHANGE_LEN + 4;

// Not a buffer size — the payload is already in memory — but how often a
// transfer can say that it is still moving.
const PROGRESS_CHUNK_BYTES = 64 * 1024;

/// A fresh exchange id. Sixteen random bytes, written big-endian, which is
/// what a .NET Guid written big-endian is.
export const newExchange = () => randomBytes(EXCHANGE_LEN);

export function encodeHeader(tag, exchange, payload) {
  ensureSendable(payload);
  return concat(new Uint8Array([tag]), exchange, u32be(payload.length));
}

/// Refuses a payload over the cap before anything is attempted with it. Kept
/// apart from writeFrame so a sender can find out that a message is too large
/// without a session: the failure is the caller's, not the link's, and a fresh
/// session would refuse it in exactly the same way.
export function ensureSendable(payload) {
  if (payload.length > MAX_PAYLOAD_BYTES) {
    throw new Error(`a message may be at most ${MAX_PAYLOAD_BYTES} bytes, this one is ${payload.length}`);
  }
}

/// Writes one frame. `idle` is told about every chunk that moves, so that a
/// slow transfer is not mistaken for a dead one; it is absent where the caller
/// imposes no limit.
export async function writeFrame(stream, tag, exchange, payload, idle) {
  await stream.write(encodeHeader(tag, exchange, payload), idle?.signal);
  for (let sent = 0; sent < payload.length; sent += PROGRESS_CHUNK_BYTES) {
    await stream.write(payload.subarray(sent, sent + PROGRESS_CHUNK_BYTES), idle?.signal);
    idle?.restart();
  }
}

/// Reads one frame. `idle` is as for writeFrame: told about every chunk that
/// arrives.
export async function readFrame(stream, idle) {
  const header = await stream.readExactly(HEADER_LENGTH, idle?.signal);
  idle?.restart();

  const tag = header[0];
  const exchange = header.slice(1, 1 + EXCHANGE_LEN);
  const length = readU32be(header, 1 + EXCHANGE_LEN);
  if (length < 0 || length > MAX_PAYLOAD_BYTES) {
    throw new Error(`the peer announced a ${length}-byte message; the limit is ${MAX_PAYLOAD_BYTES}`);
  }
  return { tag, exchange, payload: await readPayload(stream, length, idle) };
}

// A chunk at a time rather than in one call, for the same reason the write
// side sends one: a single read of sixteen megabytes looks exactly like a peer
// that has gone silent, and reports no progress until it is over.
async function readPayload(stream, length, idle) {
  const parts = [];
  let have = 0;
  while (have < length) {
    const chunk = await stream.read(idle?.signal, length - have);
    if (!chunk.length) throw new Error(`the peer stopped after ${have} of ${length} bytes`);
    parts.push(chunk);
    have += chunk.length;
    idle?.restart();
  }
  return concat(...parts);
}

// One session against a host that is staged frame by frame: what this client
// says about itself in a ping, and the heartbeat that must not end a session
// whose bytes are still moving.
//
// `SessionLivenessTests.cs` guards the same rules on the .NET side.

import assert from "node:assert/strict";
import test from "node:test";

import { delay, hex } from "../../src/bytes.js";
import { ChannelWriter, encodeChannelName } from "../../src/link-channel.js";
import {
  Capabilities,
  FrameKind,
  FrameStatus,
  encodeCapabilities,
  newExchange,
  readFrame,
  writeFrame,
} from "../../src/link-frame.js";
import { LinkSession } from "../../src/link-session.js";
import { sessionPair } from "./loopback.mjs";

const OPTIONS = { requestTimeout: 300, heartbeatInterval: 40 };

/// A browser session up against `host`, serving what the host opens.
function browserSession(connection, { channels } = {}) {
  const session = new LinkSession({
    connection,
    handler: () => null,
    notifyHandler: () => null,
    channels,
    ledger: null,
    options: OPTIONS,
    emit: () => {},
  });
  session.serve().catch(() => {});
  return session;
}

/// A host that answers a ping with `says`, or swallows it when `says` is null,
/// accepts every channel and counts every other frame it is sent.
function stageHost(connection, { says }) {
  const host = { frames: 0 };
  (async () => {
    for (;;) {
      const stream = await connection.accepted.next();
      (async () => {
        const { tag, exchange } = await readFrame(stream);
        if (tag === FrameKind.Ping) {
          if (says !== null) await writeFrame(stream, FrameStatus.Ok, exchange, encodeCapabilities(says));
          return;
        }
        host.frames++;
        if (tag === FrameKind.Channel) {
          await writeFrame(stream, FrameStatus.Ok, exchange, new Uint8Array(0));
          for (;;) await stream.read();
        }
      })().catch(() => {});
    }
  })().catch(() => {});
  return host;
}

test("a ping is answered with what this client can take", async () => {
  const { browser, host } = await sessionPair();
  const session = browserSession(browser);
  try {
    const stream = host.openStream();
    await writeFrame(stream, FrameKind.Ping, newExchange(), new Uint8Array(0));
    assert.equal(hex((await readFrame(stream)).payload), hex(encodeCapabilities(Capabilities.LargeFrames | Capabilities.Exchanges)));
  } finally {
    session.close();
  }
});

test("a channel opens and carries a large frame without asking the host anything", async () => {
  const { browser, host } = await sessionPair();
  // A host whose ping answer never comes: the saturated link, as far as a
  // ping can tell. Opening a channel must not depend on that answer.
  const staged = stageHost(host, { says: null });
  const session = browserSession(browser);
  try {
    const channel = await session.openChannel("video");
    await channel.send(new Uint8Array(1024 * 1024));
    assert.equal(staged.frames, 1, "the channel reached the host");
  } finally {
    session.close();
  }
});

/// A host that swallows pings and, while `moving`, keeps a channel into the
/// browser busy — the saturated link on which a ping's answer would queue.
async function silentHostWithTraffic(host, { moving }) {
  stageHost(host, { says: null });
  const stream = host.openStream();
  await writeFrame(stream, FrameKind.Channel, newExchange(), encodeChannelName("telemetry"));
  await readFrame(stream);
  const channel = new ChannelWriter("telemetry", stream);
  (async () => {
    while (moving()) {
      await channel.send(new Uint8Array(1024));
      await delay(30);
    }
  })().catch(() => {});
}

const readingEveryFrame = () => async (channel) => {
  for await (const _ of channel.read()) {
    // Read and dropped: only that the bytes move matters here.
  }
};

test("a heartbeat unanswered while bytes move does not end the session", async () => {
  const { browser, host } = await sessionPair();
  const session = browserSession(browser, { channels: readingEveryFrame });
  let moving = true;
  try {
    await silentHostWithTraffic(host, { moving: () => moving });
    await delay(OPTIONS.requestTimeout * 4);
    assert.equal(session.closed, false);
  } finally {
    moving = false;
    session.close();
  }
});

test("a heartbeat unanswered with nothing moving ends the session", async () => {
  const { browser, host } = await sessionPair();
  const session = browserSession(browser, { channels: readingEveryFrame });
  try {
    await silentHostWithTraffic(host, { moving: () => false });
    await delay(OPTIONS.requestTimeout * 4);
    assert.equal(session.closed, true);
  } finally {
    session.close();
  }
});

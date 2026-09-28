// A two-way stream between two ends of one relay1 session, with the relay
// replaced by a function call.
//
// `LinkStreamTests.cs` covers the same endings on the .NET side; the shared
// vectors keep the markers the two write in step. What is tested here is the
// part that is this client's own: how each end tells a clean end from an abort,
// a close and a session that died.

import assert from "node:assert/strict";
import test from "node:test";

import { concat, str, utf8 } from "../../src/bytes.js";
import { LinkStreamError } from "../../src/errors.js";
import { LinkStream, PIECE_BYTES, StreamEnding } from "../../src/link-stream.js";
import { TailcatLink } from "../../src/link.js";
import { memoryStore } from "../../src/store.js";
import { invitationCode } from "./invitation.mjs";
import { LoopbackHost, loopbackDialer, sessionPair } from "./loopback.mjs";

const PATIENCE = { patienceMs: 2_000 };

/// Both ends of one stream. The opener writes first, because a relay1 stream
/// reaches the other end only with its first bytes.
async function streamPair(first = utf8("hello")) {
  const { browser, host } = await sessionPair();
  const opener = new LinkStream("test", browser.openStream(), PATIENCE);
  await opener.write(first);
  const accepter = new LinkStream("test", await host.accepted.next(), PATIENCE);
  return { opener, accepter, browser, host };
}

async function readToEnd(stream) {
  const parts = [];
  for (;;) {
    const bytes = await stream.read();
    if (!bytes.length) return concat(...parts);
    parts.push(bytes);
  }
}

test("bytes cross both ways, and finishing ends only this end's writes", async () => {
  const { opener, accepter } = await streamPair(utf8("ping "));
  // Past a piece, so the pieces are cut and joined again.
  const large = new Uint8Array(PIECE_BYTES + 1234).map((_, i) => i % 251);

  const echoing = (async () => {
    const heard = await readToEnd(accepter);
    // Still writable after the other end finished: the half-close.
    await accepter.write(heard);
    await accepter.close();
  })();

  await opener.write(large);
  await opener.finish();
  const answer = await readToEnd(opener);
  await echoing;

  assert.deepEqual(answer, concat(utf8("ping "), large));
  assert.equal((await opener.read()).length, 0, "the clean end stays the clean end");
  await opener.close();
});

test("an abort reaches the other end's reads and writes as an abort, not an end", async () => {
  const { opener, accepter } = await streamPair();
  assert.equal(str(await accepter.read()), "hello");

  const writing = (async () => {
    try {
      for (;;) await accepter.write(new Uint8Array(32 * 1024));
    } catch (error) {
      return error;
    }
  })();

  await opener.abort();

  const writeFailed = await writing;
  assert.ok(writeFailed instanceof LinkStreamError);
  assert.equal(writeFailed.ending, StreamEnding.PeerAborted);
  await assert.rejects(accepter.read(), (error) => error.ending === StreamEnding.PeerAborted);
  await assert.rejects(opener.write(utf8("late")), /closed|aborted/);
  await accepter.close();
});

test("an end that only writes, holding bytes it never read, still hears an abort", async () => {
  // Past a short patience, an abort nobody heard would still end: the writer
  // must hear it well before then.
  const { browser, host } = await sessionPair();
  const opener = new LinkStream("test", browser.openStream(), { patienceMs: 60_000 });
  await opener.write(utf8("never read"));
  const accepter = new LinkStream("test", await host.accepted.next(), { patienceMs: 60_000 });

  const writing = (async () => {
    try {
      for (;;) await accepter.write(new Uint8Array(32 * 1024));
    } catch (error) {
      return error;
    }
  })();

  await opener.read();
  const started = Date.now();
  await opener.abort();

  assert.equal((await writing).ending, StreamEnding.PeerAborted);
  assert.ok(Date.now() - started < 5_000, "heard at once, not after the patience");
  await accepter.close();
});

test("closing while the other end still writes tells the writer it was closed", async () => {
  const { opener, accepter } = await streamPair();
  assert.equal(str(await accepter.read()), "hello");
  await accepter.close();

  const failed = await (async () => {
    try {
      for (;;) await opener.write(new Uint8Array(32 * 1024));
    } catch (error) {
      return error;
    }
  })();

  assert.equal(failed.ending, StreamEnding.PeerClosed);
  // The other end wrote nothing and ended its half on the way out.
  assert.equal((await opener.read()).length, 0);
  await opener.close();
});

test("a writer hears a close behind bytes it has not read, and reads them afterwards", async () => {
  const { opener, accepter } = await streamPair();
  assert.equal(str(await accepter.read()), "hello");
  // More pieces than the reading end holds, so its read-ahead is pacing.
  for (const piece of ["ban", "ner"]) await accepter.write(utf8(piece));
  await accepter.close();

  const failed = await (async () => {
    try {
      for (;;) await opener.write(new Uint8Array(32 * 1024));
    } catch (error) {
      return error;
    }
  })();

  assert.equal(failed.ending, StreamEnding.PeerClosed);
  assert.equal(str(await readToEnd(opener)), "banner");
  await opener.close();
});

test("a read waiting when the stream is closed is told so rather than spinning", async () => {
  const { opener, accepter } = await streamPair();
  assert.equal(str(await accepter.read()), "hello");

  const waiting = assert.rejects(accepter.read(), (error) => error.ending === StreamEnding.Aborted);
  await accepter.close();

  await waiting;
  await opener.close();
});

test("a stream ends with its session, and says so", async () => {
  const { opener, browser } = await streamPair();

  browser.cut();

  await assert.rejects(opener.read(), (error) => error.ending === StreamEnding.SessionEnded);
  await opener.close();
});

async function joinedLink(hosts = []) {
  const link = await TailcatLink.join({
    appName: "stream-tests",
    invitationCode: invitationCode(),
    store: memoryStore(),
    requestTimeout: 2_000,
    heartbeatInterval: 40,
    dial: loopbackDialer((connection) => hosts.push(new LoopbackHost(connection))),
  });
  await link.waitUntilConnected(5_000);
  return link;
}

test("a host's stream reaches the page's handler, and a handler that throws aborts it", async () => {
  const hosts = [];
  const link = await joinedLink(hosts);
  try {
    link.onStream("shout", async (stream) => {
      const heard = str(await readToEnd(stream));
      await stream.write(utf8(heard.toUpperCase()));
    });
    link.onStream("broken", async (stream) => {
      await stream.write(utf8("half"));
      throw new Error("the page gave up");
    });
    await hosts[0].paired.promise;

    const shout = await hosts[0].openStream("shout");
    await shout.write(utf8("quiet"));
    await shout.finish();
    assert.equal(str(await readToEnd(shout)), "QUIET");
    await shout.close();

    const warn = console.warn;
    console.warn = () => {}; // the throw is the point, and is reported by design
    try {
      const broken = await hosts[0].openStream("broken");
      await broken.write(utf8("go"));
      assert.equal(str(await broken.read()), "half");
      await assert.rejects(broken.read(), (error) => error.ending === StreamEnding.PeerAborted);
      await broken.close();
    } finally {
      console.warn = warn;
    }

    await assert.rejects(hosts[0].openStream("nothing"), /no "nothing" stream/);
  } finally {
    await link.close();
  }
});

test("a link opens a stream into a host's handler and reads its answer to the end", async () => {
  const link = await joinedLink();
  try {
    const echo = await link.openStream("echo");
    await echo.write(utf8("over a link"));
    await echo.finish();
    assert.equal(str(await readToEnd(echo)), "over a link");
    await echo.close();

    await assert.rejects(link.openStream("nothing"), /no "nothing" stream/);
  } finally {
    await link.close();
  }
});

// The one way content crosses a link, from a page: a request, a notification
// or a transfer, of any size, carrying on in both directions from where it
// stopped when a session dies, with the handler run once.
//
// `ExchangeTests.cs` is the .NET half and stages the same things. The host here
// serves exchanges with this client's own registry; the shared vectors are what
// hold that to the bytes the .NET library writes.

import assert from "node:assert/strict";
import test from "node:test";

import { concat, equal, hex, u32be, utf8 } from "../../src/bytes.js";
import { LinkTimeoutError, RemoteHandlerError } from "../../src/errors.js";
import { BLOCK_BYTES, ExchangeFlags, decodeExchangeHeader, encodeExchangeHeader } from "../../src/exchange-frame.js";
import { IncomingExchange } from "../../src/incoming-exchange.js";
import { TailcatLink } from "../../src/link.js";
import { LinkContent } from "../../src/link-content.js";
import { Capabilities, FrameStatus } from "../../src/link-frame.js";
import { OutboundExchange } from "../../src/outbound-exchange.js";
import { memoryStore } from "../../src/store.js";
import { LoopbackHost, hostRegistry, loopbackDialer } from "./loopback.mjs";
import { invitationCode } from "./invitation.mjs";

const LARGE = 4 * 1024 * 1024;

const IMPATIENT = {
  requestTimeout: 300,
  requestDeadline: 10_000,
  transferStallTimeout: 10_000,
  heartbeatInterval: 40,
  minReconnectDelay: 5,
  maxReconnectDelay: 20,
  handshakeTimeout: 1_000,
};

async function linkedTo(hostsFor, options = {}) {
  const hosts = [];
  const connections = [];
  const link = await TailcatLink.join({
    appName: "exchange-tests",
    invitationCode: invitationCode(),
    store: memoryStore(),
    ...IMPATIENT,
    ...options,
    dial: loopbackDialer((connection, attempt) => {
      connections.push(connection);
      hosts.push(new LoopbackHost(connection, hostsFor(attempt)));
    }),
  });
  await until(() => hosts.length > 0, "the first session to be dialled");
  return { link, hosts, connections };
}

const until = async (condition, what, patienceMs = 10_000) => {
  for (let waited = 0; waited < patienceMs; waited += 5) {
    if (condition()) return;
    await new Promise((resolve) => setTimeout(resolve, 5));
  }
  throw new Error(`gave up waiting for ${what}`);
};

const reversed = (bytes) => bytes.slice().reverse();

// WebCrypto hands out at most 65536 random bytes at a time.
function randomBytes(length) {
  const bytes = new Uint8Array(length);
  for (let at = 0; at < length; at += 65536) {
    crypto.getRandomValues(bytes.subarray(at, Math.min(length, at + 65536)));
  }
  return bytes;
}

test("a request of any size goes both ways as content, with what it says about itself", async () => {
  const registry = hostRegistry({
    answer: (_, content) =>
      LinkContent.fromBytes(reversed(content.bytes), {
        contentType: "application/x-reversed",
        metadata: utf8(`${content.name}:${hex(content.metadata)}`),
      }),
  });
  const { link } = await linkedTo(() => ({ registry }));
  try {
    const content = randomBytes(LARGE);
    const answer = await link.request(
      LinkContent.fromBytes(content, { name: "zrzut.bin", metadata: Uint8Array.of(1, 2, 3) }),
    );

    assert.equal(answer.contentType, "application/x-reversed");
    assert.equal(new TextDecoder().decode(answer.metadata), "zrzut.bin:010203");
    assert.ok(equal(answer.bytes, reversed(content)), "the answer is the request reversed, whole");
    // The browser said it had the answer, so the host let the exchange go at
    // once rather than holding it for the retention window.
    await until(() => registry.size === 0, "the host to let the exchange go");
  } finally {
    await link.close();
  }
});

test("a request broken mid-content carries on into the same handler", async () => {
  let runs = 0;
  const registry = hostRegistry({
    answer: (_, content) => {
      runs++;
      return `${content.bytes.length}`;
    },
  });
  const { link, connections } = await linkedTo(() => ({ registry }));
  try {
    let written = 0;
    let last = 0;
    let cut = false;
    const content = LinkContent.fromBytes(randomBytes(LARGE), {
      progress: (sent) => {
        written += Math.max(0, sent - last);
        last = sent;
        // Halfway, so that starting again from zero would cost half as much
        // again as the content, which is what the last assertion tells apart.
        if (!cut && sent >= LARGE / 2) {
          cut = true;
          connections[0].cut();
        }
      },
    });

    const answer = await link.request(content);

    assert.equal(answer.text, `${LARGE}`);
    assert.equal(runs, 1, "the handler ran once, however many sessions it took");
    assert.ok(connections.length >= 2, "the link had to build a second session");
    assert.ok(written < LARGE * 1.4, `${written} bytes were sent for ${LARGE}: the request started again`);
  } finally {
    await link.close();
  }
});

test("an answer broken midway carries on and is not made again", async () => {
  const answer = randomBytes(4 * LARGE);
  let runs = 0;
  const registry = hostRegistry({
    answer: () => {
      runs++;
      return LinkContent.fromBytes(answer);
    },
  });
  const { link, connections } = await linkedTo(() => ({ registry }));
  try {
    const asking = link.request(LinkContent.fromString("the recording"));
    await until(() => runs === 1, "the handler to answer");
    const before = connections[0].sentRecords;
    await until(() => connections[0].sentRecords > before + 20, "part of the answer to leave");
    connections[0].cut();

    const received = await asking;
    assert.ok(equal(received.bytes, answer), "the answer arrived whole");
    assert.equal(runs, 1, "the answer was sent again rather than made again");
    assert.ok(connections.length >= 2, "the link had to build a second session");
  } finally {
    await link.close();
  }
});

test("a notification survives the link breaking and is handled once", async () => {
  const heard = [];
  const registry = hostRegistry({
    answer: (_, content) => {
      heard.push(content.bytes.length);
    },
  });
  const { link, connections } = await linkedTo(() => ({ registry }));
  try {
    let cut = false;
    await link.notify(
      LinkContent.fromBytes(randomBytes(LARGE), {
        progress: (sent) => {
          if (!cut && sent >= LARGE / 4) {
            cut = true;
            connections[0].cut();
          }
        },
      }),
    );

    await until(() => heard.length === 1, "the notification to be handled");
    assert.deepEqual(heard, [LARGE]);
    assert.ok(connections.length >= 2, "the link had to build a second session");
  } finally {
    await link.close();
  }
});

test("an exchange with a host that has gone for good ends instead of waiting for it", async () => {
  const { link, connections } = await linkedTo((attempt) => (attempt === 0 ? {} : { answersHello: false }), {
    requestDeadline: 800,
    transferStallTimeout: 800,
  });
  try {
    await link.waitUntilConnected();
    connections[0].cut();

    const started = Date.now();
    await assert.rejects(link.request(LinkContent.fromBytes(randomBytes(1024))), LinkTimeoutError);
    await assert.rejects(link.send(LinkContent.fromBytes(randomBytes(1024))), LinkTimeoutError);
    assert.ok(Date.now() - started < 10_000, "an exchange with a host that is gone waited far past its patience");
  } finally {
    await link.close();
  }
});

test("the browser's handler hears what the host said about a request, and answers with its own", async () => {
  const { link, hosts } = await linkedTo(() => ({}));
  link.onRequest((text, _bytes, content) =>
    LinkContent.fromString(`${content.name} ${hex(content.metadata)} ${text}`, { contentType: "text/x-echo" }),
  );
  try {
    await hosts[0].paired.promise;
    const answer = await hosts[0].requestContent(
      LinkContent.fromString("hello", { name: "a.txt", metadata: Uint8Array.of(9, 8) }),
    );
    assert.equal(answer.text, "a.txt 0908 hello");
    assert.equal(answer.contentType, "text/x-echo");
  } finally {
    await link.close();
  }
});

test("a transfer reaches the browser's transfer handler, and one without a handler is refused", async () => {
  const { link, hosts } = await linkedTo(() => ({}));
  const content = randomBytes(1024 * 1024);
  const transfer = () =>
    hosts[0].requestContent(LinkContent.fromBytes(content, { name: "../../film.mp4" }), {
      exchange: new OutboundExchange(LinkContent.fromBytes(content, { name: "../../film.mp4" }), ExchangeFlags.Transfer, 5_000),
    });
  try {
    await hosts[0].paired.promise;
    await assert.rejects(transfer(), (error) => error instanceof RemoteHandlerError && /not receiving transfers/.test(error.message));

    let received = null;
    link.onTransfer((arrived) => {
      received = arrived;
    });
    await transfer();
    assert.ok(equal(received.bytes, content));
    assert.equal(received.suggestedFileName, "film.mp4", "a name from the other machine is not a path");
  } finally {
    await link.close();
  }
});

test("a transfer from the browser reaches the host's transfer handler once it has finished with it", async () => {
  let received = null;
  const { link } = await linkedTo(() => ({
    onTransfer: async (content) => {
      await new Promise((resolve) => setTimeout(resolve, 20));
      received = content;
    },
  }));
  try {
    const content = randomBytes(2 * 1024 * 1024);
    await link.send(LinkContent.fromBytes(content, { name: "nagranie.wav" }));
    assert.ok(received, "send resolved before the host's handler had finished");
    assert.equal(received.name, "nagranie.wav");
    assert.ok(equal(received.bytes, content));
  } finally {
    await link.close();
  }
});

test("an exchange the host carries on after a session died runs the browser's handler once", async () => {
  const { link, hosts, connections } = await linkedTo(() => ({}));
  let runs = 0;
  link.onRequest((_text, bytes) => {
    runs++;
    return `${bytes.length}`;
  });
  try {
    await hosts[0].paired.promise;
    let cut = false;
    const content = LinkContent.fromBytes(randomBytes(LARGE), {
      progress: (sent) => {
        if (!cut && sent >= LARGE / 4) {
          cut = true;
          connections[0].cut();
        }
      },
    });
    const exchange = new OutboundExchange(content, ExchangeFlags.Answer, 5_000);
    await assert.rejects(hosts[0].requestContent(content, { exchange }));

    await until(() => hosts.length === 2, "a second session");
    await hosts[1].paired.promise;
    const answer = await hosts[1].requestContent(content, { exchange });

    assert.equal(answer.text, `${LARGE}`);
    assert.equal(runs, 1, "the browser's handler ran once, however many sessions it took");
  } finally {
    await link.close();
  }
});

test("a request answered as an exchange and as a single frame is announced in one shape", async () => {
  const { link, hosts } = await linkedTo(() => ({}));
  link.onRequest((text) => text.toUpperCase());
  const announced = [];
  link.events.addEventListener("answered", (e) => announced.push(e.detail));
  try {
    await hosts[0].paired.promise;
    await hosts[0].requestContent(LinkContent.fromString("abc", { name: "a.txt" }));
    await hosts[0].request("abcd");

    await until(() => announced.length === 2, "both answers to be announced");
    assert.deepEqual(announced, [
      { name: "a.txt", requestLength: 3, length: 3 },
      { name: "", requestLength: 4, length: 4 },
    ]);
  } finally {
    await link.close();
  }
});

test("a notification whose handler throws is reported, since its sender cannot hear it", async () => {
  const { link, hosts } = await linkedTo(() => ({}));
  link.onNotify(() => {
    throw new Error("the page could not take it");
  });
  const failures = [];
  link.events.addEventListener("answer-failed", (e) => failures.push(e.detail));
  try {
    await hosts[0].paired.promise;
    await hosts[0].requestContent(LinkContent.fromString("news"), { flags: ExchangeFlags.AckOnDelivery });

    await until(() => failures.length === 1, "the handler's failure to be reported");
    assert.equal(failures[0].message, "the page could not take it");
  } finally {
    await link.close();
  }
});

test("an exchange header the browser cannot read is refused rather than cut off", async () => {
  const { link, hosts } = await linkedTo(() => ({}));
  link.onRequest((text) => text);
  try {
    await hosts[0].paired.promise;
    const unknownFlag = encodeExchangeHeader({ flags: 0x80, length: 0 });

    const answer = await hosts[0].exchangeWithHeader(unknownFlag);

    assert.equal(answer.tag, FrameStatus.Failed);
    assert.match(new TextDecoder().decode(answer.payload), /flags 0x80/);
  } finally {
    await link.close();
  }
});

test("content sent with a header the browser cannot read is drained before the refusal", async () => {
  const { link, hosts } = await linkedTo(() => ({}));
  link.onRequest((text) => text);
  try {
    await hosts[0].paired.promise;
    const unknownFlag = encodeExchangeHeader({ flags: 0x80 | ExchangeFlags.Pipelined, length: 3 * BLOCK_BYTES });

    // Several relay windows of it: left unread, the host could never finish writing.
    const answer = await hosts[0].exchangeWithHeader(unknownFlag, new Uint8Array(3 * BLOCK_BYTES));

    assert.equal(answer.tag, FrameStatus.Failed);
    assert.match(new TextDecoder().decode(answer.payload), /flags 0x84/);
  } finally {
    await link.close();
  }
});

test("a host that takes only single frames still gets requests, and one that says nothing is refused", async () => {
  const framesOnly = await linkedTo(() => ({ capabilities: Capabilities.LargeFrames }));
  const nothing = await linkedTo(() => ({ capabilities: Capabilities.None }));
  try {
    assert.equal(await framesOnly.link.request("frames"), "FRAMES");
    await assert.rejects(framesOnly.link.send(utf8("a file")), /does not take transfers/);
    await assert.rejects(nothing.link.request("anything"), /neither exchanges nor frames/);
  } finally {
    await framesOnly.link.close();
    await nothing.link.close();
  }
});

test("an exchange kept for its sender's acknowledgement holds none of the request's bytes", async () => {
  const header = decodeExchangeHeader(encodeExchangeHeader({ flags: ExchangeFlags.Answer, length: 3 }));
  const exchange = new IncomingExchange({
    id: new Uint8Array(16),
    header,
    retentionMs: 60_000,
    ackPatienceMs: 50,
    forget() {},
  });
  exchange.start(async (request) => LinkContent.fromString(request.text.toUpperCase()));

  // The request's blocks, and then nothing: the acknowledgement never comes.
  const incoming = concat(u32be(3), utf8("abc"), u32be(0));
  let at = 0;
  const stream = {
    write: async () => {},
    readExactly: async (n) => {
      if (at + n > incoming.length) throw new Error("the session died");
      return incoming.subarray(at, (at += n));
    },
    read: async () => new Uint8Array(0),
  };

  try {
    await exchange.deliver(stream, header, new AbortController().signal);

    assert.equal(exchange.request.bytesReceived, 3);
    assert.throws(() => exchange.request.bytes, /holds none of its bytes/);
  } finally {
    exchange.expire();
  }
});

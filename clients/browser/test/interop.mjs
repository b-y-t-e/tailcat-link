// Checks this client against the .NET one, over a real relay.
//
// Not a unit test and not run by CI: it needs a host to talk to and a relay
// to meet it at, which is exactly what makes it worth having — the failures
// it has caught were all disagreements between two implementations that each
// looked right on its own. Run it against `tailcat-demo host`.
//
//   dotnet run --project src/Tailcat.Demo -- host        # prints a code
//   node test/interop.mjs <code>
//
// Node supplies WebSocket, WebCrypto and fetch, so the same modules run
// unchanged. What differs from a browser is the TLS stack — Node uses
// OpenSSL — and IndexedDB, which the in-memory store stands in for.

import { TailcatLink } from "../src/index.js";
import { memoryStore } from "../src/store.js";

const [code, relayOrMap] = process.argv.slice(2);
if (!code) {
  console.error("usage: node test/interop.mjs <invitation-code> [relay-host | derp-map-url]");
  process.exit(2);
}

let failures = 0;
const check = (name, ok, detail = "") => {
  console.log(`${ok ? "  ok  " : "  FAIL"}  ${name}${detail ? `  ${detail}` : ""}`);
  if (!ok) failures++;
};

// The default is the map, not a fixed relay: the invitation code names the
// region the host pinned, and a hard-coded relay would meet it somewhere else
// as soon as the host sat in another one — reported as a host that never
// answered. A bare hostname still overrides it, for a relay of one's own.
// tailcat's own map, not Tailscale's: the two number their regions
// differently — a host pins 303 here and there is no 303 there — so the
// wrong one turns "the host is in Frankfurt" into "the map has no region
// 303". It is the same URL as DerpMapFetcher.DefaultUrl.
const DEFAULT_DERP_MAP = "https://tailcat.dev/derpmap.json";
const viaMap = !relayOrMap || relayOrMap.startsWith("http");

const link = await TailcatLink.join({
  appName: "interop",
  invitationCode: code,
  store: memoryStore(),
  heartbeatInterval: 5_000,
  ...(viaMap ? { derpMap: relayOrMap ?? DEFAULT_DERP_MAP } : { relayHost: relayOrMap }),
});

link.events.addEventListener("disconnected", (e) => console.log("  ..    reconnecting:", e.detail?.message));

// The host asks this end something a few seconds in; answering it is half the
// contract and the half that a one-directional test would miss. Two things are
// watched, not one: that the question arrived, and that the handler's answer
// was written rather than failing on the way out. Resolving inside the handler
// only proves the first.
//
// Neither proves the host received it — relay1 has no acknowledgement, and
// writing into a dead session succeeds — so that end of it is not claimed
// here. What it did once hide is now closed off instead of asserted: the
// answer used to be discarded by exiting before the socket had sent it, so
// this run closes the link (which drains it) before exiting, and the host's
// own "the peer answered" line is the delivery evidence.
const asked = Promise.withResolvers();
const answered = Promise.withResolvers();
link.events.addEventListener("answered", (e) => answered.resolve(e.detail));
// Resolved with nothing rather than rejected: a write that failed is a failed
// check, not a crash in the test harness.
link.events.addEventListener("answer-failed", () => answered.resolve(null));
link.onRequest((text) => {
  asked.resolve(text);
  return `the browser answered: ${text}`;
});

const started = Date.now();
await link.waitUntilConnected();
check("pairs and connects", true, `${Date.now() - started} ms`);

check("request answered", (await link.request("status")) === "STATUS");
check("unicode survives", (await link.request("zażółć gęślą jaźń")) === "ZAŻÓŁĆ GĘŚLĄ JAŹŃ");

// Past one record and past the initial credit window, so chunking, ordering
// and the window update all run.
const big = "x".repeat(300_000);
const echoed = await link.request(big);
check("300 kB round trip", echoed === big.toUpperCase(), `${echoed.length} B`);

// Concurrent, because streams are supposed to be independent of one another.
const many = await Promise.all(["one", "two", "three", "four", "five"].map((m) => link.request(m)));
check("five at once", many.join() === "ONE,TWO,THREE,FOUR,FIVE", many.join(" "));

await link.notify("just so you know");
check("notify returns without an answer", true);

const within = (promise, ms) =>
  Promise.race([promise.catch(() => null), new Promise((resolve) => setTimeout(() => resolve(null), ms))]);

const question = await within(asked.promise, 15_000);
check("host can ask this end", question !== null, question ?? "nothing arrived in 15 s");

// The answer, not just the question: a browser that returns from its handler
// has not yet written anything.
const written = await within(answered.promise, 15_000);
check("the answer to it is written", written != null, written ? `${written.length} B` : "the write failed");

await link.close();
check("closes cleanly", true);

console.log(failures ? `\n${failures} failed` : "\nall good");
process.exit(failures ? 1 : 0);

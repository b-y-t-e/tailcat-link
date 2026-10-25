// The host against a real relay: what the unit tests cannot reach.
//
// Not run by CI — it needs Tailscale's public relays, which are shared and
// rate-limited. Two ways to run it:
//
//   npm run live -- --self
//       Hosts, then joins itself with @tailcat/link in the same process, asks
//       both ways, and exits 0 or 1. Checks this package against the client
//       over a real relay.
//
//   npm run live
//       Hosts and prints the invitation code, then answers whoever joins until
//       interrupted. Join it from .NET with
//         dotnet run --project src/Tailcat.Demo -- join <code>
//       which is the check that matters: the other implementation, over the
//       relay, with QUIC offered first and relay1 agreed.
//
// The host's identity is kept in a temporary directory unless --keep <dir> is
// given, so that a second run with the same directory is the same address.

import { mkdtemp } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";

import { TailcatLink, memoryStore } from "@tailcat/link";
import { TailcatHost } from "../src/index.js";
import { fileStore } from "../src/file-store.js";

const args = process.argv.slice(2);
const self = args.includes("--self");
const keepAt = args.includes("--keep") ? args[args.indexOf("--keep") + 1] : null;
const regionAt = args.includes("--region") ? Number(args[args.indexOf("--region") + 1]) : null;
const DERP_MAP = process.env.DERP_MAP ?? "https://tailcat.dev/derpmap.json";

const directory = keepAt ?? (await mkdtemp(join(tmpdir(), "tailcat-host-live-")));
console.log(`measuring relays and connecting (state in ${directory}) ...`);
const started = Date.now();
const host = await TailcatHost.start({
  appName: "tailcat-host-live",
  store: fileStore(directory),
  derpMap: DERP_MAP,
  regionId: regionAt,
  maxPeers: 4,
});
console.log(`listening in region ${host.regionId} after ${Date.now() - started} ms`);

for (const name of ["peer-joined", "peer-left", "refused", "invitation-expired", "handshake-failed", "relay-lost"]) {
  host.events.addEventListener(name, (event) => {
    const { peer, error } = event.detail ?? {};
    const who = typeof peer === "string" ? peer.slice(0, 12) : (peer?.name ?? peer?.key?.slice(0, 12) ?? "");
    console.log(`  [${name}] ${who}${error ? `: ${error.message}` : ""}`);
  });
}

host.onRequest((peer, text) => {
  console.log(`  <- ${peer.name ?? peer.key.slice(0, 12)}: ${text}`);
  return text.toUpperCase();
});

// Asks every machine that arrives something back: the direction nobody
// requested is the one that breaks first when a transport only half works.
host.events.addEventListener("peer-joined", async (event) => {
  const { peer } = event.detail;
  try {
    console.log(`  -> ${peer.name ?? peer.key.slice(0, 12)} answered: ${await peer.request("what time is it there?")}`);
  } catch (error) {
    console.log(`  -> asking ${peer.name ?? "the peer"} failed: ${error.message}`);
  }
});

const code = host.invitationCode;
console.log("\nGive this to the other end, once:\n");
console.log(`    ${code}\n`);

if (!self) {
  console.log("waiting for peers; Ctrl+C to stop");
  process.on("SIGINT", async () => {
    await host.close();
    process.exit(0);
  });
} else {
  let failed = false;
  const page = await TailcatLink.join({
    appName: "tailcat-host-live-page",
    invitationCode: code,
    displayName: "live self-check",
    store: memoryStore(),
    derpMap: DERP_MAP,
  });
  page.onRequest((text) => `the page heard "${text}"`);
  try {
    await page.waitUntilConnected(60_000);
    const answer = await page.request("hello host");
    console.log(`page -> host: ${answer}`);
    if (answer !== "HELLO HOST") failed = true;
    const peer = await host.waitForPeer();
    const back = await peer.request("hello page");
    console.log(`host -> page: ${back}`);
    if (back !== 'the page heard "hello page"') failed = true;
  } catch (error) {
    console.error(`failed: ${error.message}`);
    failed = true;
  } finally {
    await page.close();
    await host.close();
  }
  console.log(failed ? "FAILED" : "ok");
  process.exit(failed ? 1 : 0);
}

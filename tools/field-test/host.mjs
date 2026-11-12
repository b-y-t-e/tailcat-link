// The JavaScript host of the field test, under Node with real WebRTC: answers
// "ping" with "pong", and prints every client's report as one REPORT line
// (and appends it to --reports, if given). The .NET host is
// `tailcat-demo field-host`; the two speak the same, so any client pairs with
// either.
//
//   node host.mjs [--no-direct] [--no-stun] [--reports <file>]

import { appendFile, mkdtemp } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { RTCPeerConnection } from "node-datachannel/polyfill";

import { TailcatHost } from "../../clients/browser-host/src/index.js";
import { fileStore } from "../../clients/browser-host/src/file-store.js";
import { STUN, tailscaleFree } from "./web/filter.js";

const args = process.argv.slice(2);
const option = (name) => (args.includes(name) ? args[args.indexOf(name) + 1] : null);
const direct = !args.includes("--no-direct");
const reports = option("--reports");

const host = await TailcatHost.start({
  appName: "tailcat-field",
  // A new identity every run, so a run never meets the pairings of the last.
  store: fileStore(await mkdtemp(join(tmpdir(), "tailcat-field-host-"))),
  derpMap: process.env.DERP_MAP ?? "https://tailcat.dev/derpmap.json",
  maxPeers: 16,
  directPath: direct
    ? { RTCPeerConnection: tailscaleFree(RTCPeerConnection), iceServers: args.includes("--no-stun") ? [] : STUN }
    : false,
});

const stamp = () => new Date().toISOString().slice(11, 23);
console.log(`${stamp()} host up in region ${host.regionId}, directPath ${direct}`);
console.log(`CODE ${host.invitationCode}`);

// A peer joins again on every new session; its route is listened to once.
const watched = new WeakSet();
for (const name of ["peer-joined", "peer-left", "handshake-failed", "refused", "relay-lost"]) {
  host.events.addEventListener(name, (e) => {
    const { peer, error } = e.detail ?? {};
    const who = peer?.name ?? (typeof peer === "string" ? peer.slice(0, 12) : "");
    console.log(`${stamp()} [${name}] ${who}${error ? `: ${error.message}` : ""}`);
    if (name === "peer-joined" && peer?.events && !watched.has(peer)) {
      watched.add(peer);
      peer.events.addEventListener("route-changed", (r) => console.log(`${stamp()} [route] ${peer.name}: ${r.detail}`));
    }
  });
}

host.onRequest(async (peer, text) => {
  if (!text.startsWith("report ")) return "pong";
  // The route this end saw beside the one the client reported: the two ends
  // decide it independently and should agree.
  const report = { ...JSON.parse(text.slice(7)), hostSaw: peer.route, host: "javascript" };
  console.log(`${stamp()} REPORT ${JSON.stringify(report)}`);
  if (reports) await appendFile(reports, JSON.stringify(report) + "\n");
  return "ok";
});

// A client for the field test: joins the host, pings it once a second, and
// reports what it saw — the route over time and the round trips on each — back
// to the host over the link itself, so the result arrives even from a phone
// nobody can read.
//
//   field.html?code=<invitation>&name=<label>[&seconds=40][&direct=0][&stun=0]
//
// A tab in the background is throttled to a timer a minute by Chrome, so a
// phone runs one test at a time, in the foreground.

import { TailcatLink, memoryStore } from "/src/index.js";
import { STUN, chosenPath, tailscaleFree } from "/filter.js";

const params = new URLSearchParams(location.search);
const code = params.get("code");
const name = params.get("name") ?? "page";
const seconds = Number(params.get("seconds") ?? 40);
const direct = params.get("direct") !== "0";
const stun = params.get("stun") !== "0";

const log = (line) => {
  document.getElementById("log").textContent += `${new Date().toISOString().slice(11, 23)} ${line}\n`;
  console.log(line);
};
const state = (text) => (document.getElementById("state").textContent = text);

const started = performance.now();
const at = () => Math.round(performance.now() - started);
const samples = [];
const events = [];

const link = await TailcatLink.join({
  appName: `field-${name}-${Date.now()}`,
  store: memoryStore(),
  invitationCode: code,
  displayName: name,
  derpMap: "/derpmap.json",
  directPath: direct
    ? {
        iceServers: stun ? STUN : [],
        RTCPeerConnection: tailscaleFree(globalThis.RTCPeerConnection),
      }
    : false,
});
for (const eventName of ["connected", "disconnected", "route-changed", "failed"]) {
  link.events.addEventListener(eventName, (e) => {
    const detail = e.detail?.message ?? e.detail ?? "";
    events.push({ at: at(), event: eventName, detail: String(detail) });
    log(`[${eventName}] ${detail}`);
    state(`${name}: ${link.route}`);
  });
}

await link.waitUntilConnected(60_000);
const connectMs = at();
log(`connected after ${connectMs} ms, route ${link.route}`);

const deadline = performance.now() + seconds * 1000;
while (performance.now() < deadline) {
  const route = link.route;
  const t = performance.now();
  try {
    await link.request("ping", { timeout: 10_000 });
    const rtt = Math.round(performance.now() - t);
    samples.push({ at: at(), route, rtt });
    log(`ping ${rtt} ms (${route})`);
  } catch (error) {
    samples.push({ at: at(), route, error: error.message });
    log(`ping failed: ${error.message}`);
  }
  await new Promise((r) => setTimeout(r, 1000));
}

const median = (xs) => {
  if (!xs.length) return null;
  const s = [...xs].sort((a, b) => a - b);
  return s[Math.floor(s.length / 2)];
};
const of = (route) => samples.filter((s) => s.route === route && s.rtt !== undefined).map((s) => s.rtt);
const report = {
  name,
  ua: navigator.userAgent,
  finalRoute: link.route,
  connectMs,
  relayedMedianMs: median(of("relayed")),
  directMedianMs: median(of("direct")),
  relayedSamples: of("relayed").length,
  directSamples: of("direct").length,
  failures: samples.filter((s) => s.error).length,
  path: await chosenPath(),
  publicIp: await fetch("https://api.ipify.org", { signal: AbortSignal.timeout(5000) }).then((r) => r.text()).catch(() => null),
  events,
};
await link.request(`report ${JSON.stringify(report)}`, { timeout: 15_000 });
log(`reported: ${JSON.stringify(report)}`);
state(`${name}: done, ${report.finalRoute}`);
document.title = "done";
await link.close();

// Where a host listens, chosen once.
//
// The .NET host measures its relays with STUN, which a page cannot send.
// What a page can do is time an HTTPS request to each relay's probe endpoint
// — the one Tailscale's own clients use for this, served with a CORS header
// so a page may read it — and the nearest one answers first. The result is
// pinned by the host and never measured again: the region is half of the
// address, and every code already published names it.

import { delay } from "@tailcat/link/internal/bytes.js";

const ATTEMPTS = 2;

/// The region whose relay answered fastest.
///
/// @param derpMap the URL of the DERP map, as for joining.
/// @throws if no relay answered at all, which a host cannot recover from by
///   guessing: an address in a region it cannot reach is a dead code.
export async function pickRegion(derpMap, { timeoutMs = 3_000, fetchImpl = fetch } = {}) {
  const response = await fetchImpl(derpMap);
  if (!response.ok) throw new Error(`the DERP map at ${derpMap} answered ${response.status}`);
  const map = await response.json();

  const candidates = Object.entries(map.Regions ?? {})
    .filter(([, region]) => !region.Avoid)
    .map(([id, region]) => ({ id: Number(id), node: region.Nodes?.find((n) => !n.STUNOnly && n.HostName) }))
    .filter((candidate) => candidate.node);
  if (!candidates.length) throw new Error(`the DERP map at ${derpMap} names no usable relay`);

  const timed = await Promise.all(
    candidates.map(async ({ id, node }) => ({ id, ms: await fastest(node.HostName, timeoutMs, fetchImpl) })),
  );
  const answered = timed.filter((t) => Number.isFinite(t.ms)).sort((a, b) => a.ms - b.ms);
  if (!answered.length) {
    throw new Error("no relay answered a probe; pass regionId to choose the region this host listens in");
  }
  return answered[0].id;
}

// The best of a few: the first request to a host pays for DNS and TLS, which
// says more about the resolver than about the distance.
async function fastest(hostName, timeoutMs, fetchImpl) {
  let best = Infinity;
  for (let attempt = 0; attempt < ATTEMPTS; attempt++) {
    const started = performance.now();
    try {
      const answer = await Promise.race([
        fetchImpl(`https://${hostName}/derp/probe`, { cache: "no-store" }),
        delay(timeoutMs).then(() => null),
      ]);
      if (answer?.ok) best = Math.min(best, performance.now() - started);
    } catch {
      // Unreachable from here, which is what this is finding out.
    }
  }
  return best;
}

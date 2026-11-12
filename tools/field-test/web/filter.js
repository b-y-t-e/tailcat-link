// Keeps Tailscale's addresses (100.64.0.0/10 and fd7a:115c:a1e0::/48) out of
// ICE, on both ends and in both directions: with them a "direct" path is a
// path through Tailscale's own tunnel, which real players do not have, and the
// test would measure the wrong thing.
//
// Three ways a candidate travels, and each is filtered: as an icecandidate
// event (the browser fires the attribute's handler natively; node-datachannel's
// polyfill reads the property, so the getter must hand back the filter too),
// inside the SDP itself, and through addIceCandidate on the far end.

/// The STUN server the field test uses, at both ends. Whoever runs it learns
/// every participating machine's public address; this is Google's, which is
/// the usual choice and the owner of the test machines agreed to.
export const STUN = [{ urls: "stun:stun.l.google.com:19302" }];

const isTailscale = (candidate) => {
  const text = ` ${candidate ?? ""} `;
  if (/\sfd7a:115c:a1e0:/i.test(text)) return true;
  const m = /\s(\d+)\.(\d+)\.\d+\.\d+\s\d+\s/.exec(text);
  return Boolean(m) && Number(m[1]) === 100 && Number(m[2]) >= 64 && Number(m[2]) <= 127;
};

const stripSdp = (sdp) =>
  sdp
    .split(/\r?\n/)
    .filter((line) => !(line.startsWith("a=candidate:") && isTailscale(line.slice(2))))
    .join("\r\n");

export function tailscaleFree(Base) {
  return class extends Base {
    #userHandler = null;
    #filter = (event) => {
      if (event?.candidate && isTailscale(event.candidate.candidate)) return;
      this.#userHandler?.(event);
    };
    constructor(...args) {
      super(...args);
      (globalThis.__tcPeerConnections ??= []).push(this);
      super.onicecandidate = this.#filter;
    }
    set onicecandidate(handler) {
      this.#userHandler = handler;
    }
    get onicecandidate() {
      return this.#filter;
    }
    setRemoteDescription(description) {
      return super.setRemoteDescription(description?.sdp ? { type: description.type, sdp: stripSdp(description.sdp) } : description);
    }
    addIceCandidate(candidate) {
      if (candidate && isTailscale(candidate.candidate)) return Promise.resolve();
      return super.addIceCandidate(candidate);
    }
  };
}

/// How the newest peer connection actually travels: the candidate pair ICE
/// chose, by type (host = same network, srflx/prflx = through a NAT, relay =
/// TURN) and address.
export async function chosenPath() {
  const pcs = globalThis.__tcPeerConnections ?? [];
  for (const pc of [...pcs].reverse()) {
    let stats;
    try {
      stats = await pc.getStats();
    } catch {
      continue;
    }
    const all = [...stats.values()];
    const pair = all.find((s) => s.type === "candidate-pair" && s.nominated && s.state === "succeeded")
      ?? all.find((s) => s.type === "candidate-pair" && s.state === "succeeded");
    if (!pair) continue;
    const local = all.find((s) => s.id === pair.localCandidateId) ?? {};
    const remote = all.find((s) => s.id === pair.remoteCandidateId) ?? {};
    return {
      local: `${local.candidateType ?? "?"} ${local.address ?? local.ip ?? ""}:${local.port ?? ""}`,
      remote: `${remote.candidateType ?? "?"} ${remote.address ?? remote.ip ?? ""}:${remote.port ?? ""}`,
      iceRttMs: pair.currentRoundTripTime !== undefined ? Math.round(pair.currentRoundTripTime * 1000) : null,
    };
  }
  return null;
}

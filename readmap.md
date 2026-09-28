# Roadmap

Suggested next changes, most valuable first. Principle: everything works well on defaults, with
no settings to tune, and the relay is for finding the peer — carrying traffic is its last resort.

1. **Relay1 that survives receiver-side cuts.** Resending covers a cut on the sending side only.
   Add acknowledgements and retransmission of unacknowledged records across relay reconnects,
   in .NET, `relay1.js` and the vectors. Matters for browsers and machines without QUIC.

2. **Direct path on hostile NATs.** Find out why a working direct path dies seconds in on some
   routers (mapping timeout, flood protection) and adapt: keepalive rate, fresh UDP port after
   repeated loss, gentler punching bursts.

3. **Faster relay recovery for the other side.** When the relay reports a peer gone and back,
   prompt QUIC to retransmit immediately instead of waiting for its timers.

4. **One timing model.** Derive link request timeout, heartbeat, relay liveness and QUIC idle
   timeout from each other, so no single cut can end a session by accident.

5. **Chaos suite as a gate.** Extend `RelayChaosTests`: packet loss and delay on the relay,
   relay restarts under load, many peers, long soak runs — run in CI on every push.

6. **Browser parity, after item 1.** Let a browser's relay1 session outlive its WebSocket, then
   resend what a dead relay connection swallowed, as `LostPacketResender` does in .NET. Neither
   helps alone: today a browser session ends with its WebSocket, so a resend on the new one
   reaches no session — which is why `derp.js` only notices a silent relay and closes it, and
   the link dials again and resumes exchanges on the next session.

7. **Health you can see.** One snapshot per peer (path, RTT, relay reconnects, resends, last
   cut) so an application can show "direct / relayed / struggling" without parsing logs.

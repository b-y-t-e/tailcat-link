// The hello, in both shapes.
//
// The versioned envelope and the bare token it replaced are told apart by one
// byte, and a host older than the envelope has to keep pairing. The bytes
// themselves live in `link-frames.test.mjs`, against the vector file the .NET
// side reads; what is here is the behaviour around them. `LinkHelloTests` is
// the .NET half of this file.

import assert from "node:assert/strict";
import test from "node:test";

import { utf8 } from "../../src/bytes.js";
import { MAX_DISPLAY_NAME_BYTES, decodeLinkHello, encodeLinkHello } from "../../src/link-hello.js";

test("a hello without a name says so with an empty field rather than by leaving it out", () => {
  assert.deepEqual(decodeLinkHello(encodeLinkHello({ pairingToken: "tok" })), {
    pairingToken: "tok",
    displayName: null,
  });
});

test("a bare token is read as the older shape, so a host that predates the envelope still pairs", () => {
  assert.deepEqual(decodeLinkHello(utf8("just-a-token")), {
    pairingToken: "just-a-token",
    displayName: null,
  });
});

test("a name a host would have to store megabytes of is refused before it is sent", () => {
  const tooLong = "n".repeat(MAX_DISPLAY_NAME_BYTES + 1);
  assert.throws(() => encodeLinkHello({ pairingToken: "tok", displayName: tooLong }), /at most 256 bytes/);
});

test("a hello that ends mid-field is the other machine's fault and says so", () => {
  assert.throws(() => decodeLinkHello(Uint8Array.from([0x02, 0x00])), /ended mid-field/);
});

// The hello, in both shapes.
//
// The versioned envelope and the bare token it replaced are told apart by one
// byte, and a host older than the envelope has to keep pairing — so this
// checks the bytes rather than a round trip alone. `LinkHelloTests` is the
// .NET half.

import assert from "node:assert/strict";
import test from "node:test";

import { hex, utf8 } from "../../src/bytes.js";
import { MAX_DISPLAY_NAME_BYTES, decodeLinkHello, encodeLinkHello } from "../../src/link-hello.js";

test("a hello carries the token and the name in the shape the .NET side reads", () => {
  const encoded = encodeLinkHello({ pairingToken: "tok", displayName: "phone" });
  assert.equal(hex(encoded), "02" + "0003" + hex(utf8("tok")) + "0005" + hex(utf8("phone")));
});

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

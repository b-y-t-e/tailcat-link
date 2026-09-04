// Reading an invitation code: the one thing a user copies by hand, and the
// only CBOR this client decodes.

import assert from "node:assert/strict";
import test from "node:test";

import { parseAddress, parseInvitationCode } from "../../src/address.js";
import { hex } from "./vectors.mjs";
import { connBlob } from "./invitation.mjs";

const base64url = (bytes) => Buffer.from(bytes).toString("base64url");

test("an address carries the host's key and the region it listens in", () => {
  const address = parseAddress(connBlob());
  assert.equal(hex(address.serverPublic), "07".repeat(32));
  assert.equal(address.regionId, 22);
});

test("surrounding whitespace from a copy and paste is ignored", () => {
  assert.equal(parseAddress(`  ${connBlob()}\n`).regionId, 22);
});

test("something that is not an address says so rather than decoding rubbish", () => {
  assert.throws(() => parseAddress("https://example.com/"), /starts with "tc"/);
});

test("an address without a region id is refused, because a bare key reaches the wrong relay", () => {
  const withoutRegion =
    "tc" + base64url(Uint8Array.from([0xa1, 0x61, 0x70, 0x58, 0x20, ...new Uint8Array(32).fill(7)]));
  assert.throws(() => parseAddress(withoutRegion), /embeds its own relay list/);
});

test("an invitation code is an address and the token that buys one pairing with it", () => {
  const code = parseInvitationCode(`${connBlob()}.s3cret`);
  assert.equal(code.pairingToken, "s3cret");
  assert.equal(code.address.regionId, 22);
});

test("an invitation code missing its token is refused before anything is dialled", () => {
  assert.throws(() => parseInvitationCode(connBlob()), /not an invitation code/);
  assert.throws(() => parseInvitationCode(`${connBlob()}.`), /no pairing token/);
});

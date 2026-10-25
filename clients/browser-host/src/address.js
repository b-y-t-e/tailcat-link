// Writing the one thing that travels between two machines by hand.
//
// `@tailcat/link` only reads an address, because a page that joins never has
// one of its own. A host does: a ConnBlob holding its public key and the relay
// region it listens in, and a pairing token after a "." — written as
// `ConnInfo.ToConnBlob` and `InvitationCode.ForAddress` write them, so that
// the .NET library and the browser client both read what this writes.

const PREFIX = "tc";
const TOKEN_SEPARATOR = ".";

// The CBOR head for a major type and a length or value, in its shortest form:
// what .NET's CborWriter writes.
function head(major, value) {
  const top = major << 5;
  if (value < 24) return [top | value];
  if (value < 0x100) return [top | 24, value];
  if (value < 0x10000) return [top | 25, value >> 8, value & 0xff];
  return [top | 26, (value >>> 24) & 0xff, (value >>> 16) & 0xff, (value >>> 8) & 0xff, value & 0xff];
}

const text = (s) => {
  const bytes = new TextEncoder().encode(s);
  return [...head(3, bytes.length), ...bytes];
};

export function base64urlEncode(bytes) {
  let binary = "";
  for (const b of bytes) binary += String.fromCharCode(b);
  return btoa(binary).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}

/// The address of a host: "tc" and base64url of `{ "p": key, "i": region }`.
///
/// The disco key ("k") is left out. Go needs it and this protocol never reads
/// it — the .NET port carries it only so that Go's addresses round-trip — and
/// a field left out is a shorter code to scan.
export function encodeAddress({ publicKey, regionId }) {
  if (!(publicKey instanceof Uint8Array) || publicKey.length !== 32) {
    throw new Error("an address needs the host's 32-byte public key");
  }
  if (!Number.isInteger(regionId) || regionId <= 0) {
    throw new Error(`an address needs the region the host listens in; ${regionId} is not one`);
  }
  const cbor = Uint8Array.from([
    ...head(5, 2),
    ...text("p"),
    ...head(2, 32),
    ...publicKey,
    ...text("i"),
    ...head(0, regionId),
  ]);
  return PREFIX + base64urlEncode(cbor);
}

/// The address plus the secret that buys one pairing with it.
export function formatInvitationCode(address, pairingToken) {
  if (!pairingToken || pairingToken.includes(TOKEN_SEPARATOR)) {
    throw new Error(`a pairing token must be non-empty and may not contain "${TOKEN_SEPARATOR}"`);
  }
  return address + TOKEN_SEPARATOR + pairingToken;
}

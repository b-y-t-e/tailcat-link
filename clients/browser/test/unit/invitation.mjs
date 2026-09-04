// An address and an invitation code, written the way the .NET side writes
// them, so more than one test can start from a code a user would paste.

const base64url = (bytes) => Buffer.from(bytes).toString("base64url");

/// A ConnBlob: a CBOR map of "p" (the host's key) and "i" (its region),
/// base64url after the "tc" prefix.
export const connBlob = ({ key = new Uint8Array(32).fill(7), regionId = 22 } = {}) =>
  "tc" +
  base64url(
    Uint8Array.from([
      0xa2, // a two-entry map
      0x61,
      0x70, // "p"
      0x58,
      0x20,
      ...key, // 32 bytes
      0x61,
      0x69, // "i"
      0x18,
      regionId,
    ]),
  );

/// The address plus the secret that buys one pairing with it.
export const invitationCode = ({ pairingToken = "token", ...address } = {}) =>
  `${connBlob(address)}.${pairingToken}`;

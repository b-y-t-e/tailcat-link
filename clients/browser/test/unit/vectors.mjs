// The shared vectors, and the hex helpers the unit tests read them with.
//
// Both files are compiled into the .NET test run as well — see
// Tailcat.Net.Tests/Relay1VectorTests.cs and Tailcat.Link.Tests/LinkVectorTests.cs.
// They are the only thing holding two implementations of these protocols to the
// same bytes without a live relay: relay1-records.json covers the transport, and
// link-frames.json what Tailcat.Link itself writes on top of it.

import { readFile } from "node:fs/promises";

export const hex = (bytes) => [...bytes].map((b) => b.toString(16).padStart(2, "0")).join("");

export const unhex = (text) => new Uint8Array((text.match(/../g) ?? []).map((pair) => parseInt(pair, 16)));

export const recordVectors = JSON.parse(
  await readFile(new URL("../vectors/relay1-records.json", import.meta.url), "utf8"),
);

export const linkVectors = JSON.parse(
  await readFile(new URL("../vectors/link-frames.json", import.meta.url), "utf8"),
);

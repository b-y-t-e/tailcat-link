// The shared relay1 vectors, and the hex helpers the unit tests read them with.
//
// The same file is compiled into the .NET test run (see
// Tailcat.Net.Tests/Relay1VectorTests.cs). It is the only thing that holds the
// two implementations of this protocol to the same bytes without a live relay.

import { readFile } from "node:fs/promises";

export const hex = (bytes) => [...bytes].map((b) => b.toString(16).padStart(2, "0")).join("");

export const unhex = (text) => new Uint8Array((text.match(/../g) ?? []).map((pair) => parseInt(pair, 16)));

export const recordVectors = JSON.parse(
  await readFile(new URL("../vectors/relay1-records.json", import.meta.url), "utf8"),
);

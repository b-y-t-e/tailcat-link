// One line per client report, from a host's log or a --reports file.
//
//   node summary.mjs <file> [name-prefix]

import { readFile } from "node:fs/promises";

const [file, prefix = ""] = process.argv.slice(2);
const text = await readFile(file, "utf8");

for (const line of text.split(/\r?\n/)) {
  const json = line.includes("REPORT ") ? line.slice(line.indexOf("REPORT ") + 7) : line.trim();
  if (!json.startsWith("{")) continue;
  let r;
  try {
    r = JSON.parse(json);
  } catch {
    continue;
  }
  if (!r.name?.startsWith(prefix)) continue;
  const path = r.path ? `${r.path.local} -> ${r.path.remote}` : "-";
  console.log(
    [
      r.name.padEnd(30),
      `host=${r.host ?? "?"}`.padEnd(16),
      `route=${r.finalRoute}/${r.hostSaw ?? "?"}`.padEnd(22),
      `connect=${r.connectMs ?? "?"}ms`.padEnd(15),
      `relayed=${r.relayedMedianMs ?? "-"}ms`.padEnd(16),
      `direct=${r.directMedianMs ?? "-"}ms`.padEnd(15),
      `samples=${r.relayedSamples}+${r.directSamples}`.padEnd(13),
      `failed=${r.failures}`.padEnd(9),
      path,
      r.publicIp ? `ip=${r.publicIp}` : "",
    ].join(" "),
  );
}

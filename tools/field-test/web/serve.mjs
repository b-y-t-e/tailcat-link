// Serves the field page on localhost: the page itself, the client's modules
// straight from clients/browser/src, tweetnacl, and the DERP map.
//
// localhost, not the machine's address, on purpose: WebCrypto and WebRTC need
// a secure context, and http://localhost is one where http://192.168.x.y is
// not. A phone reaches this through `adb reverse tcp:8777 tcp:8777`, which is
// also what keeps it reachable after the phone leaves Wi-Fi.
//
// The map is proxied because tailcat.dev serves it with no CORS header, and it
// must be the same map every end uses: a host in region 303 of one map is in
// no region at all of Tailscale's, and a .NET client says so — "the DERP map
// has no region 303" — while a page only fails to connect.

import { createServer } from "node:http";
import { readFile } from "node:fs/promises";
import { extname, join, normalize, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const HERE = fileURLToPath(new URL(".", import.meta.url));
const CLIENT = resolve(HERE, "../../../clients/browser");
const PORT = Number(process.env.PORT ?? 8777);
const DERP_MAP = process.env.DERP_MAP ?? "https://tailcat.dev/derpmap.json";
const TYPES = { ".html": "text/html; charset=utf-8", ".js": "text/javascript; charset=utf-8", ".json": "application/json" };

const ROUTES = [
  ["/src/", join(CLIENT, "src")],
  ["/nacl.min.js", join(CLIENT, "node_modules", "tweetnacl", "nacl.min.js")],
  ["/", HERE],
];

function fileFor(pathname) {
  if (pathname === "/") return join(HERE, "field.html");
  for (const [prefix, root] of ROUTES) {
    if (!pathname.startsWith(prefix)) continue;
    if (!prefix.endsWith("/")) return root;
    const within = normalize(pathname.slice(prefix.length));
    return within.startsWith("..") ? null : join(root, within);
  }
  return null;
}

createServer(async (req, res) => {
  const { pathname } = new URL(req.url, "http://localhost");
  if (pathname === "/derpmap.json") {
    try {
      const up = await fetch(DERP_MAP);
      res.writeHead(up.status, { "content-type": TYPES[".json"] }).end(await up.text());
    } catch (e) {
      res.writeHead(502).end(e.message);
    }
    return;
  }
  const file = fileFor(pathname);
  if (!file) return res.writeHead(403).end();
  try {
    const body = await readFile(file);
    res.writeHead(200, { "content-type": TYPES[extname(file)] ?? "application/octet-stream", "cache-control": "no-store" }).end(body);
  } catch {
    res.writeHead(404).end(`no ${pathname}`);
  }
}).listen(PORT, "127.0.0.1", () => console.log(`serving the field page on http://localhost:${PORT}/ (DERP map ${DERP_MAP})`));

// A static server for the hosting example, and a proxy for the DERP map.
//
// The page needs an origin: a browser will not open a WebSocket from file://.
// The proxy is not a convenience — the control plane serves the map without
// the CORS header a cross-origin fetch needs — so every real deployment ships
// or proxies it the same way.
//
// The page imports this package from /host/ and the client it is built on
// from /link/, and an import map in the page ties the two together: that is
// what a bundler would otherwise do with "@tailcat/link/internal/...".

import { createServer } from "node:http";
import { readFile } from "node:fs/promises";
import { extname, join, normalize, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const HERE = fileURLToPath(new URL(".", import.meta.url));
const ROOTS = {
  "/host/": resolve(HERE, "../../src"),
  "/link/": resolve(HERE, "../../../browser/src"),
};
const PORT = Number(process.env.PORT ?? 8778);
const DERP_MAP = process.env.DERP_MAP ?? "https://tailcat.dev/derpmap.json";

const TYPES = {
  ".html": "text/html; charset=utf-8",
  ".js": "text/javascript; charset=utf-8",
  ".json": "application/json; charset=utf-8",
};

function resolveRequest(pathname) {
  if (pathname === "/") return join(HERE, "index.html");
  for (const [prefix, root] of Object.entries(ROOTS)) {
    if (!pathname.startsWith(prefix)) continue;
    const within = normalize(pathname.slice(prefix.length));
    return within.startsWith("..") ? null : join(root, within);
  }
  return null;
}

const server = createServer(async (request, response) => {
  const { pathname } = new URL(request.url, `http://localhost:${PORT}`);

  if (pathname === "/derpmap.json") {
    try {
      const upstream = await fetch(DERP_MAP);
      response.writeHead(upstream.status, { "content-type": TYPES[".json"] });
      response.end(await upstream.text());
    } catch (error) {
      response.writeHead(502, { "content-type": "text/plain" });
      response.end(`could not fetch ${DERP_MAP}: ${error.message}`);
    }
    return;
  }

  const file = resolveRequest(pathname);
  if (!file) {
    response.writeHead(404, { "content-type": "text/plain" }).end(`no such file: ${pathname}`);
    return;
  }
  try {
    const body = await readFile(file);
    response.writeHead(200, { "content-type": TYPES[extname(file)] ?? "application/octet-stream" });
    response.end(body);
  } catch {
    response.writeHead(404, { "content-type": "text/plain" }).end(`no such file: ${pathname}`);
  }
});

server.listen(PORT, "127.0.0.1", () => {
  console.log(`http://127.0.0.1:${PORT}/`);
  console.log(`proxying the DERP map from ${DERP_MAP}`);
});

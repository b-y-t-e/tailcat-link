// A static server for the example, and a proxy for the DERP map.
//
// The page needs an origin: a browser will not fetch from file:// and will
// not open a WebSocket from one either. The proxy is not a convenience — a
// page cannot fetch the DERP map from tailcat.dev, because that host serves
// it without the CORS header a cross-origin fetch needs. Every real
// deployment has to ship or proxy the map the same way, so the example does
// what it would have to do rather than keeping a copy that goes stale.

import { createServer } from "node:http";
import { readFile } from "node:fs/promises";
import { extname, join, normalize, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const HERE = fileURLToPath(new URL(".", import.meta.url));
const CLIENT = resolve(HERE, "../..");
const PORT = Number(process.env.PORT ?? 8777);
const DERP_MAP = process.env.DERP_MAP ?? "https://tailcat.dev/derpmap.json";

const TYPES = {
  ".html": "text/html; charset=utf-8",
  ".js": "text/javascript; charset=utf-8",
  ".mjs": "text/javascript; charset=utf-8",
  ".json": "application/json; charset=utf-8",
  ".css": "text/css; charset=utf-8",
};

// The example is served from this folder, and the client it imports lives two
// levels up, so both are reachable and nothing else is.
const ROOTS = { "/": HERE, "/src/": join(CLIENT, "src") };

function resolveRequest(pathname) {
  if (pathname === "/") return join(HERE, "index.html");
  if (pathname.startsWith("/src/")) {
    const within = normalize(pathname.slice("/src/".length));
    if (within.startsWith("..")) return null;
    return join(ROOTS["/src/"], within);
  }
  const within = normalize(pathname.slice(1));
  if (within.startsWith("..")) return null;
  return join(HERE, within);
}

const server = createServer(async (request, response) => {
  const { pathname } = new URL(request.url, `http://localhost:${PORT}`);

  if (pathname === "/derpmap.json") {
    try {
      const upstream = await fetch(DERP_MAP);
      const body = await upstream.text();
      response.writeHead(upstream.status, { "content-type": TYPES[".json"] });
      response.end(body);
    } catch (error) {
      response.writeHead(502, { "content-type": "text/plain" });
      response.end(`could not fetch ${DERP_MAP}: ${error.message}`);
    }
    return;
  }

  const file = resolveRequest(pathname);
  if (!file) {
    response.writeHead(403).end("no");
    return;
  }
  try {
    const body = await readFile(file);
    response.writeHead(200, { "content-type": TYPES[extname(file)] ?? "application/octet-stream" });
    response.end(body);
  } catch {
    response.writeHead(404, { "content-type": "text/plain" });
    response.end(`no such file: ${pathname}`);
  }
});

server.listen(PORT, "127.0.0.1", () => {
  console.log(`http://127.0.0.1:${PORT}/`);
  console.log(`serving the example from ${HERE}`);
  console.log(`proxying the DERP map from ${DERP_MAP}`);
});

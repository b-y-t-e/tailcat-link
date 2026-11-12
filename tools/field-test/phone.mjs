// Opens (or with "close", closes every field-test tab) in Chrome on the phone,
// through DevTools forwarded by adb to 127.0.0.1:9222.
const [action, url] = process.argv.slice(2);
const version = await fetch("http://127.0.0.1:9222/json/version").then((r) => r.json());
const ws = new WebSocket(version.webSocketDebuggerUrl);
await new Promise((r, j) => { ws.onopen = r; ws.onerror = j; });
let id = 0;
const call = (method, params = {}) => new Promise((resolve) => {
  const mine = ++id;
  ws.addEventListener("message", function on(m) {
    const msg = JSON.parse(m.data);
    if (msg.id === mine) { ws.removeEventListener("message", on); resolve(msg.result ?? msg.error); }
  });
  ws.send(JSON.stringify({ id: mine, method, params }));
});
if (action === "open") console.log(JSON.stringify(await call("Target.createTarget", { url })));
if (action === "close") {
  const { targetInfos } = await call("Target.getTargets");
  for (const t of targetInfos.filter((t) => t.type === "page" && t.url.includes("localhost:8777/field.html"))) {
    await call("Target.closeTarget", { targetId: t.targetId });
    console.log("closed", t.url.slice(0, 60));
  }
}
ws.close();

// Runs the field page in a real browser through Playwright and prints its log.
// node client.mjs <chrome|msedge> <url> [maxSeconds]
import { chromium } from "playwright-core";
const [channel, url, max = "120"] = process.argv.slice(2);
const browser = await chromium.launch({ channel, headless: true });
const page = await browser.newPage();
page.on("console", (m) => console.log(`[page] ${m.text()}`));
page.on("pageerror", (e) => console.log(`[pageerror] ${e.message}`));
await page.goto(url);
try {
  await page.waitForFunction(() => document.title === "done", null, { timeout: Number(max) * 1000 });
  console.log("CLIENT DONE");
} catch {
  console.log("CLIENT TIMEOUT");
  console.log(await page.evaluate(() => document.getElementById("log")?.textContent));
}
await browser.close();

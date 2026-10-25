// Where a host running under Node keeps what makes it the same machine
// tomorrow. A page has IndexedDB, which `@tailcat/link` already uses; Node
// has none, and a host that forgot its key on every start would have a new
// address every time.
//
// One JSON file per application. Bytes are written as base64 under a marker,
// because JSON has no other way to hold them. The file holds this machine's
// private key — its name and its password at once — so it is readable by its
// owner only: by mode on Unix, by its own access list on Windows, where a mode
// does nothing and the file would otherwise inherit whatever the directory
// grants — read for every user, under most drives' roots.

import { execFile } from "node:child_process";
import { randomBytes } from "node:crypto";
import { mkdir, open, readFile, rename, rm } from "node:fs/promises";
import { join } from "node:path";
import { promisify } from "node:util";

const BYTES = "$bytes";

const replacer = (_key, value) =>
  value instanceof Uint8Array ? { [BYTES]: Buffer.from(value).toString("base64") } : value;

const reviver = (_key, value) =>
  value && typeof value === "object" && typeof value[BYTES] === "string"
    ? new Uint8Array(Buffer.from(value[BYTES], "base64"))
    : value;

// Replaces the inherited entries with one for the user this process runs as.
// The SID rather than a name, which a domain account or a renamed one would
// get wrong. By full path, because a Unix toolbox on PATH (Git's) brings a
// `whoami` of its own.
async function ownerOnly(file) {
  const run = promisify(execFile);
  const system32 = join(process.env.SystemRoot ?? "C:\\Windows", "System32");
  const { stdout } = await run(join(system32, "whoami.exe"), ["/user", "/fo", "csv", "/nh"], { windowsHide: true });
  const sid = stdout.trim().split(",").at(-1).replaceAll('"', "");
  await run(join(system32, "icacls.exe"), [file, "/inheritance:r", "/grant:r", `*${sid}:F`], { windowsHide: true });
}

/// A store keeping each application's state in `<directory>/<appName>.json`.
export function fileStore(directory) {
  const fileFor = (appName) => {
    if (!/^[\w.-]+$/.test(appName)) {
      throw new Error(`"${appName}" cannot name a file; use letters, digits, ".", "-" and "_"`);
    }
    return join(directory, `${appName}.json`);
  };

  return {
    async load(appName) {
      try {
        return JSON.parse(await readFile(fileFor(appName), "utf8"), reviver);
      } catch (error) {
        if (error.code === "ENOENT") return null;
        throw error;
      }
    },
    async save(appName, state) {
      await mkdir(directory, { recursive: true, mode: 0o700 });
      const file = fileFor(appName);
      // Written beside and moved over, so a crash mid-write leaves the old
      // state rather than half of the new one — which would be no key at all.
      // A name nobody can guess, created here or not at all: a file somebody
      // else placed at the path, or a link there, would keep its own
      // permissions and receive the key. Written through the one handle, so
      // the file restricted is the file the key goes into.
      const partial = `${file}.${randomBytes(8).toString("hex")}.tmp`;
      const handle = await open(partial, "wx", 0o600);
      try {
        // On Windows the access list goes on before the key does: a file
        // written first and restricted after is readable in between.
        if (process.platform === "win32") await ownerOnly(partial);
        await handle.writeFile(JSON.stringify(state, replacer, 2));
        await handle.close();
        await rename(partial, file);
      } catch (error) {
        await handle.close().catch(() => {});
        await rm(partial, { force: true });
        throw error;
      }
    },
    async remove(appName) {
      await rm(fileFor(appName), { force: true });
    },
  };
}

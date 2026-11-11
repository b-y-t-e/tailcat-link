import { test } from "node:test";
import assert from "node:assert/strict";

import { Queue } from "../../src/bytes.js";

test("a wait that was given up on does not take the next item with it", async () => {
  const queue = new Queue();
  const gaveUp = new AbortController();

  const abandoned = queue.next(gaveUp.signal);
  gaveUp.abort(new Error("stopped waiting"));
  await assert.rejects(abandoned, /stopped waiting/);

  // The waiter used to stay queued: this push went to it, and to nobody.
  queue.push("kept");
  assert.equal(await queue.next(), "kept");
});

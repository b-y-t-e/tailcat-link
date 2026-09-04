// The ledger the host's retries land on: the same request twice is answered
// twice and run once.

import assert from "node:assert/strict";
import test from "node:test";

import { ExchangeLedger } from "../../src/exchange-ledger.js";
import { newExchange } from "../../src/link-frame.js";

/// A clock the test moves, so that retention is checked without waiting.
const stoppedClock = (start = 0) => {
  const clock = { at: start, now: () => clock.at };
  return clock;
};

test("a repeated exchange is answered from memory rather than run again", async () => {
  const ledger = new ExchangeLedger({ retention: 120_000 });
  const exchange = newExchange();
  let runs = 0;
  const produce = async () => ++runs;

  assert.equal(await ledger.answer(exchange, produce), 1);
  assert.equal(await ledger.answer(exchange, produce), 1);
  assert.equal(runs, 1);
});

test("a retry that arrives while the first is still running joins it", async () => {
  const ledger = new ExchangeLedger({ retention: 120_000 });
  const exchange = newExchange();
  let runs = 0;
  let release;
  const produce = () => {
    runs++;
    return new Promise((resolve) => {
      release = resolve;
    });
  };

  const first = ledger.answer(exchange, produce);
  const second = ledger.answer(exchange, produce);
  release("done");

  assert.deepEqual(await Promise.all([first, second]), ["done", "done"]);
  assert.equal(runs, 1);
});

test("a different exchange is a different request", async () => {
  const ledger = new ExchangeLedger({ retention: 120_000 });
  let runs = 0;
  const produce = async () => ++runs;

  assert.equal(await ledger.answer(newExchange(), produce), 1);
  assert.equal(await ledger.answer(newExchange(), produce), 2);
});

test("a handler that threw is not remembered, so a retry may try again", async () => {
  const ledger = new ExchangeLedger({ retention: 120_000 });
  const exchange = newExchange();
  let runs = 0;
  const produce = async () => {
    if (++runs === 1) throw new Error("the first attempt broke");
    return "the second worked";
  };

  await assert.rejects(ledger.answer(exchange, produce), /the first attempt broke/);
  assert.equal(await ledger.answer(exchange, produce), "the second worked");
});

test("an answer is forgotten once the sender can no longer be retrying it", async () => {
  const clock = stoppedClock();
  const ledger = new ExchangeLedger({ retention: 120_000, now: clock.now });
  const exchange = newExchange();
  let runs = 0;
  const produce = async () => ++runs;

  assert.equal(await ledger.answer(exchange, produce), 1);
  clock.at += 120_001;
  assert.equal(await ledger.answer(exchange, produce), 2);
});

test("a peer that floods ids is refused rather than allowed to run one twice", async () => {
  const ledger = new ExchangeLedger({ retention: 120_000 });
  // Nothing ever finishes, so nothing can be dropped to make room.
  const pending = () => new Promise(() => {});
  for (let i = 0; i < 4096; i++) ledger.answer(newExchange(), pending);

  assert.throws(() => ledger.answer(newExchange(), pending), /4096 requests still running/);
});

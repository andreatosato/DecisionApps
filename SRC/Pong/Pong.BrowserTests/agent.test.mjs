import test from "node:test";
import assert from "node:assert/strict";
import { DecisionController } from "../Pong.Web/wwwroot/agent/decision-controller.mjs";
import { createGame } from "../Pong.Web/wwwroot/game/physics.mjs";

test("decision control works without a DOM and follows only the model's chosen action", async () => {
  assert.equal(typeof document, "undefined");
  assert.equal(typeof window, "undefined");
  const originalFetch = globalThis.fetch;
  const originalPerformance = Object.getOwnPropertyDescriptor(globalThis, "performance");
  const updates = [];
  const errors = [];
  const controller = new DecisionController(update => updates.push(update), message => errors.push(message));
  const game = createGame();
  game.ball.x = 600;
  game.ball.y = 50;
  game.modelY = 500;
  let now = 0;
  let action;
  Object.defineProperty(globalThis, "performance", { configurable: true, value: { now: () => now } });
  globalThis.fetch = async (_, options) => ({
    ok: true,
    json: async () => ({
      sequence: JSON.parse(options.body).sequence,
      action,
      probabilities: {
        up: action === "up" ? 0.9 : 0.05,
        down: action === "down" ? 0.9 : 0.05,
        stay: action === "stay" ? 0.9 : 0.05
      },
      elapsedMilliseconds: 10,
      usage: null
    })
  });
  try {
    for (const [choice, direction] of [["down", 1], ["up", -1], ["stay", 0]]) {
      action = choice;
      now += 120;
      const before = structuredClone(game);
      controller.update(game, now);
      for (let turn = 0; turn < 8; turn++) await Promise.resolve();
      assert.equal(controller.getDirection(now), direction);
      assert.deepEqual(game, before, "the controller must not advance physics or move a paddle itself");
      assert.equal(updates.filter(update => update.type === "decision").at(-1).result.action, choice);
    }
    assert.equal(updates.filter(update => update.type === "received").length, 3);
    assert.deepEqual(errors, []);
    controller.cancel();
    assert.equal(controller.getDirection(now), 0);
  } finally {
    globalThis.fetch = originalFetch;
    Object.defineProperty(globalThis, "performance", originalPerformance);
  }
});

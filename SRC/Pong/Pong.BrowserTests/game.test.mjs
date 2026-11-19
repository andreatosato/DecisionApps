import test from "node:test";
import assert from "node:assert/strict";
import { RULES, advanceGame, clamp, createGame, isModelTurn, movePaddle, snapshot } from "../Pong.Web/wwwroot/game/physics.mjs";

function activeGame() {
  return createGame();
}

test("initial state and API snapshot include numeric positions, velocities and centered right paddle", () => {
  const game = createGame();
  const state = snapshot(game, 12);
  assert.equal(state.sequence, 12);
  assert.deepEqual(state.arena, { width: 960, height: 600 });
  assert.equal(state.ball.x, 480);
  assert.equal(state.ball.y, 300);
  assert.equal(state.ball.radius, 8);
  assert.ok(state.ball.velocityX > 0);
  assert.ok(state.ball.velocityY > 0);
  assert.deepEqual(state.paddle, { x: 926, y: 300, width: 14, height: 88, speed: 320 });
  assert.equal(state.decisionIntervalSeconds, 0.1);
  state.ball.x = 0;
  assert.equal(game.ball.x, 480);
});

test("paddles move up/down and remain within bounds", () => {
  assert.equal(movePaddle(300, -1, 0.25), 220);
  assert.equal(movePaddle(300, 1, 0.25), 380);
  assert.equal(movePaddle(44, -1, 1), 44);
  assert.equal(movePaddle(556, 1, 1), 556);
  assert.equal(movePaddle(300, 99, 0.25), 380);
  assert.equal(clamp(-4, 0, 1), 0);
});

test("right paddle is stationary without a model decision, regardless of ball position", () => {
  const game = activeGame();
  game.ball.y = 50;
  game.ball.velocityY = -80;
  advanceGame(game, 0.05, 0, 0);
  assert.equal(game.modelY, 300);
});

test("right paddle follows only supplied up or down action, independently of ball position", () => {
  const game = activeGame();
  game.ball.y = 50;
  advanceGame(game, 0.05, -1, 1);
  assert.ok(Math.abs(game.playerY - 284) < 0.0001);
  assert.ok(Math.abs(game.modelY - 316) < 0.0001);
  advanceGame(game, 0.05, 1, -1);
  assert.ok(Math.abs(game.modelY - 300) < 0.0001);
});

test("top and bottom walls reflect ball velocity without letting it leave arena", () => {
  for (const bottom of [false, true]) {
    const game = activeGame();
    game.ball.y = bottom ? 591 : 9;
    game.ball.velocityY = bottom ? 200 : -200;
    advanceGame(game, 0.02);
    assert.ok(game.ball.y >= 8 && game.ball.y <= 592);
    assert.ok(bottom ? game.ball.velocityY < 0 : game.ball.velocityY > 0);
  }
});

test("paddle collisions reflect towards the opponent and respect the maximum ball speed", () => {
  for (const right of [false, true]) {
    const game = activeGame();
    game.ball.x = right ? 908 : 52;
    game.ball.y = 315;
    game.ball.velocityX = right ? 600 : -600;
    game.ball.velocityY = 0;
    advanceGame(game, 0.02);
    assert.ok(right ? game.ball.velocityX < 0 : game.ball.velocityX > 0);
    assert.ok(game.ball.velocityY > 0);
    assert.ok(Math.abs(Math.hypot(game.ball.velocityX, game.ball.velocityY) - 600) < 0.00001);
    assert.equal(game.playerScore + game.modelScore, 0);
  }
});

test("a missed right paddle awards one point and resets ball for a new serve", () => {
  const game = activeGame();
  game.ball.x = 964;
  game.ball.y = 70;
  game.ball.velocityX = 440;
  advanceGame(game, 0.05);
  assert.equal(game.playerScore, 1);
  assert.equal(game.modelScore, 0);
  assert.equal(game.rally, 1);
  assert.equal(game.ball.x, 480);
  assert.equal(game.ball.y, 300);
  const servedX = game.ball.x;
  advanceGame(game, 0.016);
  assert.notEqual(game.ball.x, servedX, "the next serve must launch without a countdown");
});

test("a missed left paddle awards the model a point", () => {
  const game = activeGame();
  game.ball.x = -6;
  game.ball.y = 70;
  game.ball.velocityX = -440;
  advanceGame(game, 0.05);
  assert.equal(game.modelScore, 1);
  assert.equal(game.playerScore, 0);
});

test("seven points ends the game and subsequent steps do not change the result", () => {
  const game = activeGame();
  game.playerScore = 6;
  game.ball.x = 964;
  game.ball.y = 70;
  game.ball.velocityX = 440;
  advanceGame(game, 0.05);
  assert.equal(game.winner, "player");
  assert.equal(game.playerScore, 7);
  const end = structuredClone(game);
  advanceGame(game, 0.05, 1, -1);
  assert.deepEqual(game, end);
});

test("the initial serve moves immediately without a countdown", () => {
  const game = createGame();
  advanceGame(game, 0.05, 1, -1);
  assert.ok(game.ball.x > 480);
  assert.ok(game.ball.y > 300);
  assert.ok(game.playerY > 300);
  assert.ok(game.modelY < 300);
});

test("model turn requires an incoming ball in its own half, before its paddle face", () => {
  const game = activeGame();
  for (const [x, vx, expected] of [
    [479.9, 220, false], [480, 220, true], [700, 220, true],
    [700, -220, false], [700, 0, false], [911, 220, true], [912, 220, false]
  ]) {
    game.ball.x = x;
    game.ball.velocityX = vx;
    assert.equal(isModelTurn(game), expected, `x=${x}, vx=${vx}`);
  }
  game.ball.x = 700;
  game.winner = "player";
  assert.equal(isModelTurn(game), false);
});

test("even an unexpired model action cannot move the right paddle while awaiting the opponent", () => {
  for (const [x, vx] of [[700, -220], [300, -220], [300, 220]]) {
    const game = activeGame();
    game.modelY = 450;
    game.ball.x = x;
    game.ball.velocityX = vx;
    advanceGame(game, 0.05, 0, 1);
    assert.equal(game.modelY, 450, `x=${x}, vx=${vx}`);
  }
});

test("after its hit the right paddle returns toward center rather than following the old action", () => {
  const game = activeGame();
  game.modelY = 450;
  game.ball.x = 910.9;
  game.ball.y = 450;
  game.ball.velocityX = 220;
  game.ball.velocityY = 0;
  advanceGame(game, 0.05, 0, 1);
  assert.ok(game.ball.velocityX < 0);
  assert.ok(game.modelY < 450 && game.modelY > 300);
  assert.equal(game.modelReturning, true);
  const returningY = game.modelY;
  advanceGame(game, 0.05, 0, 1);
  assert.ok(game.modelY < returningY);
});

test("returning after a hit converges from either side at paddle speed without overshoot", () => {
  for (const startingY of [120, 480]) {
    const game = activeGame();
    game.modelY = startingY;
    game.ball.x = 910.9;
    game.ball.y = startingY;
    game.ball.velocityX = 220;
    game.ball.velocityY = 0;
    advanceGame(game, 0.01);
    assert.equal(game.modelReturning, true);
    for (let frame = 0; frame < 60; frame++) {
      const previousY = game.modelY;
      advanceGame(game, 0.016, 0, startingY < 300 ? -1 : 1);
      assert.ok(Math.abs(game.modelY - previousY) <= RULES.paddleSpeed * 0.016 + 0.00001);
      assert.ok(Math.abs(game.modelY - 300) <= Math.abs(previousY - 300));
      assert.ok(startingY < 300 ? game.modelY <= 300 : game.modelY >= 300);
    }
    assert.equal(game.modelY, 300);
    assert.equal(game.modelReturning, false);
  }
});

test("an incoming model turn takes over from an unfinished return to center", () => {
  const game = activeGame();
  game.modelY = 400;
  game.modelReturning = true;
  game.ball.x = 600;
  advanceGame(game, 0.05, 0, 1);
  assert.equal(game.modelReturning, false);
  assert.ok(Math.abs(game.modelY - 416) < 0.00001);
});

test("each alternating paddle hit adds exactly ten percent speed until the 600 px/s cap", () => {
  const game = activeGame();
  game.ball.velocityX = RULES.ballSpeed;
  game.ball.velocityY = 0;
  let speed = RULES.ballSpeed;
  for (let hit = 0; hit < 35; hit++) {
    const right = hit % 2 === 0;
    game.ball.x = right ? 910 : 50;
    game.ball.y = 300;
    game.ball.velocityX = right ? speed : -speed;
    game.ball.velocityY = 0;
    advanceGame(game, 0.01);
    speed = Math.min(600, speed * 1.1);
    assert.ok(Math.abs(Math.hypot(game.ball.velocityX, game.ball.velocityY) - speed) < 0.00001);
    assert.ok(right ? game.ball.velocityX < 0 : game.ball.velocityX > 0);
  }
  assert.equal(speed, 600);
});

test("angled hits accelerate the total speed even when the horizontal component decreases", () => {
  for (const right of [false, true]) {
    const game = activeGame();
    game.ball.x = right ? 910 : 50;
    game.ball.y = 335;
    game.ball.velocityX = right ? RULES.ballSpeed : -RULES.ballSpeed;
    game.ball.velocityY = 0;
    advanceGame(game, 0.01);
    assert.ok(Math.abs(Math.hypot(game.ball.velocityX, game.ball.velocityY) - 242) < 0.00001);
    assert.ok(Math.abs(game.ball.velocityX) < RULES.ballSpeed);
    assert.ok(right ? game.ball.velocityX < 0 : game.ball.velocityX > 0);
  }
});

test("wall bounces do not increase speed and a new point resets to the base speed", () => {
  const game = activeGame();
  game.ball.y = 9;
  game.ball.velocityX = 300;
  game.ball.velocityY = -200;
  const speed = Math.hypot(300, 200);
  advanceGame(game, 0.02);
  assert.ok(Math.abs(Math.hypot(game.ball.velocityX, game.ball.velocityY) - speed) < 0.00001);
  game.ball.x = 964;
  advanceGame(game, 0.05);
  assert.ok(Math.abs(Math.hypot(game.ball.velocityX, game.ball.velocityY) - RULES.ballSpeed) < 0.00001);
});

test("large or invalid frame deltas cannot cause teleportation or corrupt the game", () => {
  const game = activeGame();
  advanceGame(game, 20, 1, 1);
  assert.ok(game.ball.x - 480 <= 22);
  assert.ok(game.modelY - 300 <= 16.001);
  const state = structuredClone(game);
  for (const delta of [0, -1, NaN, Infinity]) advanceGame(game, delta, 1, 1);
  assert.deepEqual(game, state);
});

test("a ball already behind the paddle is not reflected back into play", () => {
  const game = activeGame();
  game.ball.x = 930;
  game.ball.y = game.modelY;
  game.ball.velocityX = 220;
  game.ball.velocityY = 0;
  advanceGame(game, 0.05);
  assert.ok(game.ball.velocityX > 0);
});

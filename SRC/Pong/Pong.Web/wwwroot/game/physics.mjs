export const RULES = Object.freeze({
  width: 960, height: 600, ballRadius: 8, paddleWidth: 14, paddleHeight: 88,
  playerX: 34, modelX: 926, paddleSpeed: 320, ballSpeed: 220,
  maxBallSpeed: 600, hitSpeedMultiplier: 1.1,
  winningScore: 7, decisionSeconds: 0.1
});

export function clamp(value, min, max) {
  return Math.max(min, Math.min(max, value));
}

export function createGame() {
  const game = {
    ball: { x: RULES.width / 2, y: RULES.height / 2, velocityX: 0, velocityY: 0 },
    playerY: RULES.height / 2, modelY: RULES.height / 2, modelReturning: false,
    playerScore: 0, modelScore: 0, rally: 0, winner: null
  };
  serve(game, 1);
  return game;
}

function serve(game, direction) {
  game.ball.x = RULES.width / 2;
  game.ball.y = RULES.height / 2;
  game.ball.velocityX = direction * RULES.ballSpeed * Math.cos(Math.PI / 7);
  game.ball.velocityY = (game.rally % 2 === 0 ? 1 : -1) * RULES.ballSpeed * Math.sin(Math.PI / 7);
  game.modelReturning = false;
}

export function movePaddle(y, direction, seconds) {
  return clamp(y + clamp(direction, -1, 1) * RULES.paddleSpeed * seconds,
    RULES.paddleHeight / 2, RULES.height - RULES.paddleHeight / 2);
}

export function isModelTurn(game) {
  return !game.winner && game.ball.velocityX > 0 && game.ball.x >= RULES.width / 2 &&
    game.ball.x <= RULES.modelX - RULES.paddleWidth / 2 - RULES.ballRadius;
}

export function snapshot(game, sequence) {
  return {
    sequence,
    arena: { width: RULES.width, height: RULES.height },
    ball: { ...game.ball, radius: RULES.ballRadius },
    paddle: {
      x: RULES.modelX, y: game.modelY, width: RULES.paddleWidth,
      height: RULES.paddleHeight, speed: RULES.paddleSpeed
    },
    decisionIntervalSeconds: RULES.decisionSeconds
  };
}

function bouncePaddle(game, paddleY, direction) {
  const offset = clamp((game.ball.y - paddleY) / (RULES.paddleHeight / 2), -1, 1);
  const angle = offset * Math.PI / 3;
  const speed = Math.min(RULES.maxBallSpeed,
    Math.hypot(game.ball.velocityX, game.ball.velocityY) * RULES.hitSpeedMultiplier);
  game.ball.velocityX = direction * speed * Math.cos(angle);
  game.ball.velocityY = speed * Math.sin(angle);
}

function score(game, playerWon) {
  if (playerWon) game.playerScore++;
  else game.modelScore++;
  game.rally++;
  if (game.playerScore >= RULES.winningScore || game.modelScore >= RULES.winningScore) {
    game.winner = playerWon ? "player" : "model";
    return;
  }
  serve(game, playerWon ? 1 : -1);
}

export function advanceGame(game, seconds, playerDirection = 0, modelDirection = 0) {
  if (game.winner || !Number.isFinite(seconds) || seconds <= 0) return;
  // Small bounded steps prevent tunnelling and jumps after a stalled animation frame.
  let remaining = Math.min(seconds, 0.05);
  while (remaining > 0) {
    const dt = Math.min(remaining, 1 / 240);
    remaining -= dt;
    game.playerY = movePaddle(game.playerY, playerDirection, dt);
    if (isModelTurn(game)) {
      game.modelReturning = false;
      game.modelY = movePaddle(game.modelY, modelDirection, dt);
    } else if (game.modelReturning) {
      const distance = RULES.height / 2 - game.modelY;
      game.modelY += clamp(distance, -RULES.paddleSpeed * dt, RULES.paddleSpeed * dt);
      if (game.modelY === RULES.height / 2) game.modelReturning = false;
    }
    const ball = game.ball;
    const previousX = ball.x;
    ball.x += ball.velocityX * dt;
    ball.y += ball.velocityY * dt;
    if (ball.y < RULES.ballRadius) {
      ball.y = 2 * RULES.ballRadius - ball.y;
      ball.velocityY = Math.abs(ball.velocityY);
    } else if (ball.y > RULES.height - RULES.ballRadius) {
      ball.y = 2 * (RULES.height - RULES.ballRadius) - ball.y;
      ball.velocityY = -Math.abs(ball.velocityY);
    }

    const leftFace = RULES.playerX + RULES.paddleWidth / 2 + RULES.ballRadius;
    const rightFace = RULES.modelX - RULES.paddleWidth / 2 - RULES.ballRadius;
    const reachesPaddle = y => Math.abs(ball.y - y) <= RULES.paddleHeight / 2 + RULES.ballRadius;
    if (ball.velocityX < 0 && previousX >= leftFace && ball.x <= leftFace && reachesPaddle(game.playerY)) {
      ball.x = leftFace;
      bouncePaddle(game, game.playerY, 1);
    } else if (ball.velocityX > 0 && previousX <= rightFace && ball.x >= rightFace && reachesPaddle(game.modelY)) {
      ball.x = rightFace;
      bouncePaddle(game, game.modelY, -1);
      game.modelReturning = true;
    }
    if (ball.x < -RULES.ballRadius || ball.x > RULES.width + RULES.ballRadius) {
      score(game, ball.x > RULES.width);
      return;
    }
  }
}

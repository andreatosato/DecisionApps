import test from "node:test";
import assert from "node:assert/strict";

let moduleId = 0;

async function withBrowser(run, { ready = true, gptAvailable = true } = {}) {
  const elements = new Map();
  const frames = [];
  let now = 0;
  let inference;
  let failure;
  let requests = 0;
  let pendingSignal;
  const signals = [];
  const paddlePositions = [];
  const observedFrames = [];
  const states = [];
  const deadlines = [];
  const originalTimeout = Object.getOwnPropertyDescriptor(AbortSignal, "timeout");
  let ball;
  const context = {
    fillRect(x, y, width, height) {
      if (x === 919 && width === 14 && height === 88) paddlePositions.push(y);
    },
    strokeRect() {}, beginPath() {}, arc(x, y) {
      ball = { x, y };
      observedFrames.push({ timestampMilliseconds: now, ballX: x, ballY: y,
        paddleY: paddlePositions.at(-1) + 44 });
    }, fill() {}
  };
  const element = id => {
    if (!elements.has(id)) elements.set(id, {
      textContent: "", hidden: false, disabled: false, style: {},
      classList: { add() {}, remove() {} },
      handlers: {}, addEventListener(name, callback) { this.handlers[name] = callback; },
      children: [], replaceChildren(...children) { this.children = children; },
      focus() {}, getContext() { return context; }
    });
    return elements.get(id);
  };
  const originals = new Map(["document", "window", "requestAnimationFrame", "fetch", "performance"]
    .map(name => [name, Object.getOwnPropertyDescriptor(globalThis, name)]));
  Object.defineProperty(AbortSignal, "timeout", { configurable: true, value: milliseconds => {
    const controller = new AbortController();
    deadlines.push({ controller, at: now + milliseconds });
    return controller.signal;
  } });
  Object.defineProperties(globalThis, {
    document: { configurable: true, value: {
      getElementById: element, addEventListener() {}, hidden: false,
      createTextNode: text => ({ textContent: text }), createElement: () => ({})
    } },
    window: { configurable: true, value: { addEventListener() {} } },
    performance: { configurable: true, value: { now: () => now } },
    requestAnimationFrame: { configurable: true, value: callback => frames.push(callback) },
    fetch: { configurable: true, value: async (url, options) => {
      if (url === "/api/configuration") return {
        ok: true, json: async () => ({
          deployment: "Decision-1.1", authentication: "test",
          models: [
            { id: "decision", label: "Decision 1.1", deployment: "Decision-1.1", available: true, protocol: "NATIVE SYSTEMONE", authentication: "test" },
            { id: "gpt", label: "GPT", deployment: gptAvailable ? "gpt-6-luna" : "", available: gptAvailable, protocol: "CHAT COMPLETIONS", authentication: "test" }
          ]
        })
      };
      requests++;
      pendingSignal = options.signal;
      signals.push(options.signal);
      const state = JSON.parse(options.body);
      states.push(state);
      return new Promise((resolve, reject) => {
        failure = reject;
        inference = (action = "down", override = {}) => resolve({
          ok: true, json: async () => ({
            sequence: state.sequence, action,
            probabilities: action === "stay" ? { up: 0.05, down: 0.05, stay: 0.9 } :
              action === "up" ? { up: 0.9, down: 0.1, stay: 0 } : { up: 0.1, down: 0.9, stay: 0 },
            elapsedMilliseconds: 150, usage: null
          }),
          ...override
        });
      });
    } }
  });
  const flush = async () => { for (let i = 0; i < 8; i++) await Promise.resolve(); };
  const tick = milliseconds => {
    now += milliseconds;
    for (const deadline of deadlines) {
      if (now >= deadline.at && !deadline.controller.signal.aborted) {
        deadline.controller.abort(new DOMException("Timed out", "TimeoutError"));
      }
    }
    const callback = frames.shift();
    assert.ok(callback, "animation frame must be scheduled");
    callback(now);
  };
  try {
    await import(`../Pong.Web/wwwroot/app.mjs?test=${++moduleId}`);
    await flush();
    if (ready) {
      const starting = element("start").handlers.click();
      inference("stay");
      await starting;
      await flush();
      element("reset").handlers.click();
      requests = 0;
      states.length = 0;
      signals.length = 0;
    }
    await run({
      element, tick, flush, signals, states, paddlePositions, observedFrames,
      reply: (action, override) => inference(action, override),
      fail: error => failure(error),
      get pendingReply() { return inference; },
      get requests() { return requests; },
      get pendingSignal() { return pendingSignal; },
      get ball() { return ball; }
    });
  } finally {
    Object.defineProperty(AbortSignal, "timeout", originalTimeout);
    for (const [name, descriptor] of originals) {
      if (descriptor) Object.defineProperty(globalThis, name, descriptor);
      else delete globalThis[name];
    }
  }
}

test("a request canceled before HTTP returns is visible immediately, without inventing a response", () =>
  withBrowser(async browser => {
    const { element, tick, flush } = browser;
    await element("start").handlers.click();
    tick(16);
    assert.match(element("request-list").children[0]?.textContent ?? "", /Decision 1.1.*INVIATA/s);
    tick(1000);
    assert.match(element("request-elapsed").textContent, /1000 ms/);
    element("pause").handlers.click();
    assert.equal(browser.pendingSignal.aborted, true);
    assert.match(element("request-list").children[0].textContent, /ANNULLATA: PAUSA/);
    assert.equal(element("response-count").textContent, "0 RICEVUTE");
    browser.fail(new DOMException("Aborted", "AbortError"));
    await flush();
    assert.equal(element("request-list").children.length, 1, "one terminal event per request");
    assert.equal(element("error").hidden, true, "an intentional cancellation is not a service error");
  }));

test("network failures and timeouts before HTTP returns leave a visible terminal request", async () => {
  for (const error of [new TypeError("Failed to fetch"), new DOMException("Timed out", "TimeoutError")]) {
    await withBrowser(async browser => {
      const { element, tick, flush } = browser;
      await element("start").handlers.click();
      tick(16);
      browser.fail(error);
      await flush();
      assert.match(element("request-list").children[0]?.textContent ?? "",
        error.name === "TimeoutError" ? /TIMEOUT/ : /Failed to fetch/);
      assert.equal(element("response-count").textContent, "0 RICEVUTE");
      assert.equal(element("game-status").textContent, "ERRORE / IN PAUSA");
    });
  }
});

test("step mode freezes the whole court during slow inference and applies only one simulated interval", () =>
  withBrowser(async browser => {
    const { element, tick, flush, paddlePositions } = browser;
    element("play-mode").value = "steps";
    element("play-mode").handlers.change();
    await element("start").handlers.click();
    tick(16);
    const sent = browser.states[0];
    assert.equal(sent.playMode, "steps");
    assert.equal(element("play-mode").disabled, true);
    for (let frame = 0; frame < 80; frame++) tick(50);
    assert.equal(browser.ball.x, sent.ball.x, "waiting wall time must not advance the ball");
    assert.equal(paddlePositions.at(-1), 256);
    assert.equal(browser.requests, 1);
    browser.reply("down");
    await flush();
    assert.equal(element("action-name").textContent, "SCENDI", "a four-second valid answer must be usable in step mode");
    assert.equal(element("response-time").textContent, "4000 ms", "latency must still be measured in real time");
    for (let frame = 0; frame < 3; frame++) tick(50);
    const expectedX = sent.ball.x + sent.ball.velocityX * sent.decisionIntervalSeconds;
    assert.ok(Math.abs(browser.ball.x - expectedX) < 0.00001);
    assert.ok(Math.abs(paddlePositions.at(-1) - 288) < 0.00001);
    assert.equal(browser.requests, 2);
    assert.ok(browser.states[1].observedAtMilliseconds - sent.observedAtMilliseconds <= 100.00001,
      "history uses simulation time, never the four-second inference wait");
    assert.equal(browser.states[1].estimatedResponseMilliseconds, 4000);
    const frozenX = browser.ball.x;
    for (let frame = 0; frame < 80; frame++) tick(50);
    assert.equal(browser.ball.x, frozenX);
    element("pause").handlers.click();
    element("play-mode").value = "realtime";
    element("play-mode").handlers.change();
    assert.equal(element("play-mode").value, "steps", "pause must not bypass the match mode lock");
    browser.reply("up");
    await flush();
    tick(50);
    assert.equal(browser.ball.x, frozenX, "late replies must not resume a paused step game");
    element("reset").handlers.click();
    assert.equal(element("play-mode").disabled, false);
  }));

test("a stationary GPT diagnostic exposes action, timing and tokens without starting a match", () =>
  withBrowser(async browser => {
    const { element, tick, flush } = browser;
    element("model-select").value = "gpt";
    element("model-select").handlers.change();
    const testing = element("test-model").handlers.click();
    assert.equal(element("start").disabled, true);
    assert.equal(element("model-select").disabled, true);
    assert.equal(browser.states[0].model, "gpt");
    for (let frame = 0; frame < 80; frame++) tick(50);
    assert.equal(browser.ball.x, 480);
    browser.reply("up", { json: async () => ({
      sequence: 0, deployment: "gpt-6-luna", action: "up",
      probabilities: { up: 1, down: 0, stay: 0 },
      elapsedMilliseconds: 3900, usage: { inputTokens: 653, outputTokens: 241, totalTokens: 894 }
    }) });
    await testing;
    await flush();
    assert.match(element("diagnostic-result").textContent, /gpt-6-luna/);
    assert.match(element("diagnostic-result").textContent, /894/);
    assert.match(element("diagnostic-result").textContent, /4000/);
    assert.equal(element("match-samples").textContent, "0");
    assert.equal(element("model-select").disabled, false);
    assert.equal(element("start").disabled, false);
    assert.equal(element("test-model").disabled, false);
    for (let frame = 0; frame < 10; frame++) tick(50);
    assert.equal(browser.requests, 1);
    assert.equal(browser.ball.x, 480);
  }, { ready: false }));

test("stationary preparation and diagnostics allow cold authentication beyond the live-play deadline", async () => {
  for (const diagnostic of [false, true]) {
    await withBrowser(async browser => {
      const { element, tick, flush } = browser;
      const pending = element(diagnostic ? "test-model" : "start").handlers.click();
      for (let frame = 0; frame < 240; frame++) tick(50);
      assert.equal(browser.pendingSignal.aborted, false, "cold authentication must not be aborted at ten seconds");
      assert.equal(browser.ball.x, 480);
      browser.reply("stay");
      await pending;
      await flush();
      assert.equal(element("error").hidden, true);
      if (!diagnostic) element("pause").handlers.click();
    }, { ready: false });
  }
});

test("cold start waits visibly for Decision without consuming the first rally", () =>
  withBrowser(async browser => {
    const { element, tick, flush, paddlePositions } = browser;
    const starting = element("start").handlers.click();
    await flush();
    assert.equal(element("game-status").textContent, "WAITING / DECISION");
    assert.equal(element("start").disabled, true);
    assert.equal(element("overlay-start").disabled, true);
    assert.equal(browser.requests, 1);
    await element("start").handlers.click();
    assert.equal(browser.requests, 1, "repeated start must not overlap preparation calls");
    assert.equal(browser.states[0].sequence, 0);
    for (let frame = 0; frame < 60; frame++) tick(50);
    assert.equal(browser.ball.x, 480, "the opening serve must wait for the cold inference");
    assert.equal(paddlePositions.at(-1), 256);
    assert.equal(browser.requests, 1);
    browser.reply("down");
    await starting;
    await flush();
    assert.equal(element("game-status").textContent, "IN GIOCO");
    assert.match(element("response-list").children[0].textContent, /PREPARAZIONE/);
    tick(16);
    assert.ok(browser.ball.x > 480, "the serve starts as soon as Decision is ready");
    assert.equal(paddlePositions.at(-1), 256, "the warm-up action must never drive the live paddle");
    assert.equal(browser.requests, 2, "live play must request a fresh state");
    assert.equal(browser.states[1].estimatedResponseMilliseconds, null,
      "cold-start latency must not skew the live trajectory context");
    element("pause").handlers.click();
  }, { ready: false }));

test("model selection is locked during play and pause; reset enables a fresh GPT preparation", () =>
  withBrowser(async browser => {
    const { element, flush } = browser;
    const select = element("model-select");
    assert.equal(select.disabled, false);
    assert.equal(select.children.length, 2);
    select.value = "gpt";
    select.handlers.change();
    assert.equal(element("model-name").textContent, "gpt-6-luna");
    assert.match(element("score-announcement").textContent, /gpt-6-luna/);
    const starting = element("start").handlers.click();
    assert.equal(select.disabled, true);
    assert.equal(browser.states[0].model, "gpt");
    browser.reply("stay");
    await starting;
    assert.equal(element("match-samples").textContent, "0", "preparation is excluded from match metrics");
    assert.equal(select.disabled, true);
    element("pause").handlers.click();
    assert.equal(select.disabled, true);
    select.value = "decision";
    select.handlers.change();
    assert.equal(select.value, "gpt", "a change event must not bypass the match lock");
    element("reset").handlers.click();
    assert.equal(select.disabled, false);
    select.value = "decision";
    select.handlers.change();
    assert.equal(element("model-name").textContent, "Decision 1.1");
    const second = element("start").handlers.click();
    assert.equal(browser.states.at(-1).model, "decision");
    browser.reply("stay");
    await second;
    await flush();
    element("pause").handlers.click();
  }, { ready: false }));

test("unconfigured model cannot be selected even through a synthetic change event", () =>
  withBrowser(async browser => {
    const { element } = browser;
    const select = element("model-select");
    assert.equal(select.children[1].disabled, true);
    select.value = "gpt";
    select.handlers.change();
    assert.equal(select.value, "decision");
    assert.equal(element("error").hidden, false);
    assert.equal(browser.requests, 0);
  }, { ready: false, gptAvailable: false }));

test("match timing and tokens exclude preparation and reset between games", () =>
  withBrowser(async browser => {
    const { element, tick, flush } = browser;
    await element("start").handlers.click();
    tick(16);
    for (let frame = 0; frame < 5; frame++) tick(20);
    const sequence = browser.states.at(-1).sequence;
    browser.reply("up", { json: async () => ({
      sequence, action: "up", probabilities: { up: 1, down: 0, stay: 0 },
      elapsedMilliseconds: 60, usage: { inputTokens: 120, outputTokens: 25, totalTokens: 145 }
    }) });
    await flush();
    assert.equal(element("match-samples").textContent, "1");
    assert.equal(element("match-tokens").textContent, "120 / 25 / 145");
    assert.equal(element("match-time").textContent, "60 / 100 ms");
    element("reset").handlers.click();
    assert.equal(element("match-samples").textContent, "0");
    assert.equal(element("match-tokens").textContent, "--");
    assert.equal(element("match-time").textContent, "--");
  }));

test("missing token usage is unavailable rather than a zero consumption total", () =>
  withBrowser(async browser => {
    const { element, tick, flush } = browser;
    await element("start").handlers.click();
    tick(16);
    browser.reply("stay");
    await flush();
    assert.equal(element("match-samples").textContent, "1");
    assert.equal(element("match-tokens").textContent, "non disponibili (1 senza usage)");
    element("pause").handlers.click();
  }));

test("pause and reset cancel preparation, and an old response cannot start a newer waiting game", async () => {
  for (const control of ["pause", "reset"]) {
    await withBrowser(async browser => {
      const { element, tick, flush } = browser;
      const firstStart = element("start").handlers.click();
      const oldReply = browser.pendingReply;
      const oldSignal = browser.pendingSignal;
      element(control).handlers.click();
      assert.equal(oldSignal.aborted, true);
      assert.equal(element("overlay-start").disabled, false);
      const secondStart = element("start").handlers.click();
      oldReply("down");
      await firstStart;
      await flush();
      tick(50);
      assert.equal(element("game-status").textContent, "WAITING / DECISION");
      assert.equal(browser.ball.x, 480);
      assert.equal(browser.requests, 2);
      browser.reply("stay");
      await secondStart;
      assert.equal(element("game-status").textContent, "IN GIOCO");
      element("pause").handlers.click();
    }, { ready: false });
  }
});

test("failed or invalid preparation stays stopped, shows the error and allows an explicit retry", async () => {
  for (const override of [
    { ok: false, status: 503, text: async () => JSON.stringify({ detail: "Decision non disponibile." }) },
    { json: async () => ({ sequence: 0, action: "down", probabilities: { up: 0, down: 1 } }) }
  ]) {
    await withBrowser(async browser => {
      const { element, tick } = browser;
      const starting = element("start").handlers.click();
      browser.reply("down", override);
      await starting;
      tick(50);
      assert.equal(element("game-status").textContent, "ERRORE / IN PAUSA");
      assert.equal(element("error").hidden, false);
      assert.equal(element("start").disabled, false);
      assert.equal(element("overlay-start").disabled, false);
      assert.equal(browser.ball.x, 480);
      const retrying = element("start").handlers.click();
      assert.equal(element("game-status").textContent, "WAITING / DECISION");
      browser.reply("stay");
      await retrying;
      assert.equal(element("game-status").textContent, "IN GIOCO");
      element("pause").handlers.click();
    }, { ready: false });
  }
});

test("ball advances during first inference and repeated decisions use fresh moving snapshots", () =>
  withBrowser(async browser => {
    const { element, tick, flush, paddlePositions } = browser;
    await element("start").handlers.click();
    tick(16);
    assert.equal(element("ball-speed").textContent, "220.0 px/s",
      "the readout must show the velocity magnitude, not one component");
    assert.equal(browser.requests, 1);
    const sentX = browser.states[0].ball.x;
    for (let i = 0; i < 12; i++) tick(16);
    assert.ok(browser.ball.x > sentX + 30, "first inference must not freeze the ball");
    assert.equal(browser.requests, 1, "there must never be overlapping requests");
    browser.reply();
    await flush();
    tick(16);
    assert.equal(element("decisions").textContent, "1");
    assert.equal(browser.requests, 2, "a fresh decision must start as soon as the previous one finishes");
    assert.ok(browser.states[1].ball.x > sentX + 30, "each decision must use the current ball state");
    const secondX = browser.states[1].ball.x;
    for (let i = 0; i < 8; i++) tick(16);
    assert.ok(paddlePositions.some(y => y > 256), "right paddle must follow the actual returned down action");
    assert.ok(browser.ball.x > secondX + 20, "subsequent inference must not freeze the ball");
    browser.reply("up");
    await flush();
    assert.equal(element("action-name").textContent, "SALI", "the next response must replace the old direction");
    assert.equal(element("error").hidden, true);
    tick(16);
    element("pause").handlers.click();
    assert.equal(browser.pendingSignal.aborted, true, "manual pause must still cancel pending inference");
    const stoppedAt = paddlePositions.at(-1);
    browser.reply();
    await flush();
    tick(16);
    assert.equal(paddlePositions.at(-1), stoppedAt, "a late response must not resume a paused paddle");
  }));

test("model movement stays continuous between 160 ms responses and reverses on the next choice", () =>
  withBrowser(async browser => {
    const { element, tick, flush, paddlePositions } = browser;
    await element("start").handlers.click();
    tick(16);
    for (let frame = 0; frame < 10; frame++) tick(16);
    browser.reply("down");
    await flush();
    for (let frame = 0; frame < 10; frame++) {
      const previousY = paddlePositions.at(-1);
      tick(16);
      assert.ok(Math.abs(paddlePositions.at(-1) - previousY - 320 * 0.016) < 0.00001,
        "the last fresh model choice must not stop after 100 ms while awaiting the next response");
    }
    assert.equal(browser.requests, 2, "only one fresh request may be in flight");
    browser.reply("up");
    await flush();
    const previousY = paddlePositions.at(-1);
    tick(16);
    assert.ok(Math.abs(paddlePositions.at(-1) - previousY + 320 * 0.016) < 0.00001,
      "a new choice must replace the held direction immediately");
    element("pause").handlers.click();
  }));

test("held movement expires 350 ms after its snapshot if the next response stalls", () =>
  withBrowser(async browser => {
    const { element, tick, flush, paddlePositions } = browser;
    await element("start").handlers.click();
    tick(16);
    for (let frame = 0; frame < 10; frame++) tick(16);
    browser.reply("down");
    await flush();
    for (let frame = 0; frame < 13; frame++) tick(16);
    const stoppedAt = paddlePositions.at(-1);
    assert.ok(stoppedAt > 256, "a fresh action must be applied before its expiry");
    const ballX = browser.ball.x;
    for (let frame = 0; frame < 12; frame++) tick(16);
    assert.equal(paddlePositions.at(-1), stoppedAt, "an old snapshot must not drive the paddle indefinitely");
    assert.equal(element("action-name").textContent, "SCELTA SCADUTA");
    assert.ok(browser.ball.x > ballX, "the ball must still move after action expiry");
    element("pause").handlers.click();
  }));

test("a stale replacement stops held motion instead of extending its freshness window", () =>
  withBrowser(async browser => {
    const { element, tick, flush, paddlePositions } = browser;
    await element("start").handlers.click();
    tick(16);
    browser.reply("down");
    await flush();
    for (let frame = 0; frame < 32; frame++) tick(16);
    const stoppedAt = paddlePositions.at(-1);
    browser.reply("down");
    await flush();
    assert.equal(element("action-name").textContent, "STATO SUPERATO");
    tick(16);
    assert.equal(paddlePositions.at(-1), stoppedAt);
    assert.equal(browser.requests, 3, "the next request must still use a fresh state");
    element("pause").handlers.click();
  }));

test("fast responses allow repeated fresh decisions every 100 ms without overlapping calls", () =>
  withBrowser(async browser => {
    const { element, tick, flush } = browser;
    await element("start").handlers.click();
    let answered = 0;
    for (let frame = 0; frame < 125; frame++) {
      tick(8);
      if (browser.requests > answered) {
        assert.equal(browser.requests, answered + 1);
        browser.reply(answered % 2 === 0 ? "down" : "up");
        answered++;
        await flush();
      }
    }
    assert.ok(answered >= 9 && answered <= 10, `expected up to ten decisions per second, got ${answered}`);
    assert.equal(element("response-count").textContent, `${answered} RICEVUTE`);
    assert.equal(element("response-list").children.length, 8, "response history must stay bounded");
    assert.ok(element("response-list").children[0].textContent.startsWith(`#${answered} / `));
    for (let i = 1; i < browser.states.length; i++) {
      assert.ok(browser.states[i].ball.x > browser.states[i - 1].ball.x);
      assert.equal(browser.states[i].decisionIntervalSeconds, 0.1);
    }
    element("pause").handlers.click();
  }));

test("Decision receives real bounded observations and measured latency, and stay stops a held action", () =>
  withBrowser(async browser => {
    const { element, tick, flush, paddlePositions } = browser;
    await element("start").handlers.click();
    tick(16);
    const first = browser.states[0];
    assert.equal(first.history.length, 1);
    assert.equal(first.observedAtMilliseconds, 16);
    assert.equal(first.estimatedResponseMilliseconds, null, "latency cannot be invented before a response");
    for (let frame = 0; frame < 10; frame++) tick(16);
    browser.reply("down");
    await flush();
    tick(16);
    const state = browser.states[1];
    assert.equal(state.history.length, 5);
    assert.equal(state.estimatedResponseMilliseconds, 160);
    assert.equal(state.observedAtMilliseconds, 192);
    for (let i = 0; i < state.history.length; i++) {
      const point = state.history[i];
      assert.ok(point.timestampMilliseconds <= state.observedAtMilliseconds);
      if (i) assert.ok(point.timestampMilliseconds > state.history[i - 1].timestampMilliseconds);
      assert.equal(point.velocityX, first.ball.velocityX);
      assert.equal(point.velocityY, first.ball.velocityY);
      const observed = browser.observedFrames.find(frame => frame.timestampMilliseconds === point.timestampMilliseconds);
      assert.ok(observed, "every historical sample must correspond to an actual rendered frame");
      assert.equal(point.ballX, observed.ballX);
      assert.equal(point.ballY, observed.ballY);
      assert.equal(point.paddleY, observed.paddleY);
    }
    const latest = state.history.at(-1);
    assert.equal(latest.ballX, state.ball.x);
    assert.equal(latest.ballY, state.ball.y);
    assert.equal(latest.paddleY, state.paddle.y);
    assert.equal(latest.timestampMilliseconds, state.observedAtMilliseconds);
    assert.ok(latest.paddleY > first.paddle.y, "history must include the actual paddle movement");
    assert.ok(!JSON.stringify(state).match(/target|intercept|predicted/i), "no local prediction may be sent");
    assert.equal(first.history.length, 1, "an older request's history must not be mutated");
    browser.reply("stay", {
      json: async () => ({ sequence: state.sequence, action: "stay",
        probabilities: { up: 0.05, down: 0.05, stay: 0.9 }, elapsedMilliseconds: 10, usage: null })
    });
    await flush();
    assert.equal(element("action-name").textContent, "FERMO");
    assert.equal(element("stay-value").textContent, "90.0%");
    assert.match(element("response-list").children[0].textContent, /FERMO \/ APPLICATA/);
    assert.match(element("decision-context").textContent, /5\/5/);
    const stoppedAt = paddlePositions.at(-1);
    const ballX = browser.ball.x;
    for (let frame = 0; frame < 5; frame++) tick(16);
    assert.equal(paddlePositions.at(-1), stoppedAt, "stay must cancel the old down action immediately");
    assert.ok(browser.ball.x > ballX, "stay must never pause the ball");
    assert.equal(element("error").hidden, true);
    element("pause").handlers.click();
  }));

test("an obsolete response is surfaced and replaced without pausing or applying its direction", () =>
  withBrowser(async browser => {
    const { element, tick, flush, paddlePositions } = browser;
    await element("start").handlers.click();
    tick(16);
    const sentX = browser.states[0].ball.x;
    for (let frame = 0; frame < 30; frame++) tick(16);
    assert.ok(browser.ball.x > sentX + 80);
    assert.equal(browser.requests, 1);
    browser.reply("down");
    await flush();
    assert.equal(element("request-state").textContent, "RISPOSTA SUPERATA");
    assert.equal(element("game-status").textContent, "IN GIOCO");
    assert.equal(element("action-name").textContent, "STATO SUPERATO");
    assert.equal(element("response-count").textContent, "1 RICEVUTE");
    assert.match(element("response-list").children[0].textContent, /GIU \/ SCARTATA: SUPERATA/);
    assert.equal(paddlePositions.at(-1), 256);
    tick(16);
    assert.equal(browser.requests, 2);
    assert.ok(browser.states[1].ball.x > sentX + 80);
    assert.equal(element("error").hidden, true);
    element("pause").handlers.click();
  }));

test("pause clears trajectory history, reset also clears measured latency, and late responses cannot restore it", () =>
  withBrowser(async browser => {
    const { element, tick, flush } = browser;
    await element("start").handlers.click();
    tick(16);
    for (let frame = 0; frame < 10; frame++) tick(16);
    browser.reply("down");
    await flush();
    tick(16);
    assert.equal(browser.states[1].history.length, 5);
    assert.equal(browser.states[1].estimatedResponseMilliseconds, 160);
    element("pause").handlers.click();
    browser.reply("stay");
    await flush();
    await element("start").handlers.click();
    tick(16);
    assert.equal(browser.states[2].history.length, 1);
    assert.equal(browser.states[2].estimatedResponseMilliseconds, 160);
    element("reset").handlers.click();
    browser.reply("down");
    await flush();
    await element("start").handlers.click();
    tick(16);
    assert.equal(browser.states[3].history.length, 1);
    assert.equal(browser.states[3].estimatedResponseMilliseconds, null);
    assert.equal(element("stay-value").textContent, "--");
    element("pause").handlers.click();
  }));

test("invalid stay probabilities cannot be applied or silently replaced by another action", async () => {
  for (const probabilities of [
    { up: 0.1, down: 0.1 }, { up: 0.1, down: 0.1, stay: 0.1 },
    { up: 0.8, down: 0.1, stay: 0.1 }, { up: 0, down: 0, stay: 1.1 },
    { up: 0, down: 0, stay: "1" }
  ]) {
    await withBrowser(async browser => {
      const { element, tick, flush, paddlePositions } = browser;
      await element("start").handlers.click();
      tick(16);
      browser.reply("stay", { json: async () => ({
        sequence: browser.states[0].sequence, action: "stay", probabilities,
        elapsedMilliseconds: 10, usage: null
      }) });
      await flush();
      assert.equal(element("game-status").textContent, "ERRORE / IN PAUSA");
      assert.equal(paddlePositions.at(-1), 256);
      assert.match(element("response-list").children[0].textContent, /RISPOSTA NON VALIDA/);
    });
  }
});

test("each identical response gets a timestamped UI entry and reset clears the history", () =>
  withBrowser(async browser => {
    const { element, tick, flush } = browser;
    await element("start").handlers.click();
    tick(16);
    browser.reply("down");
    await flush();
    assert.equal(element("response-count").textContent, "1 RICEVUTE");
    assert.match(element("last-update").textContent, /^#1 \/ \d{2}:\d{2}:\d{2}[.,]\d{3}$/);
    for (let frame = 0; frame < 8; frame++) tick(16);
    assert.equal(browser.requests, 2);
    browser.reply("down");
    await flush();
    assert.equal(element("response-count").textContent, "2 RICEVUTE");
    const rows = element("response-list").children;
    assert.equal(rows.length, 2);
    assert.ok(rows[0].textContent.startsWith("#2 / "));
    assert.ok(rows[1].textContent.startsWith("#1 / "));
    assert.ok(rows.every(row => row.textContent.includes("GIU / APPLICATA")));
    assert.ok(rows.every(row => row.className === "response-event response-applied"));
    const lastUpdate = element("last-update").textContent;
    tick(16);
    assert.equal(element("last-update").textContent, lastUpdate, "sending a request is not receiving a response");
    element("pause").handlers.click();
    assert.equal(element("response-list").children.length, 2, "history must remain readable in pause");
    element("reset").handlers.click();
    assert.equal(element("response-count").textContent, "0 RICEVUTE");
    assert.equal(element("last-update").textContent, "Nessuna risposta ricevuta");
    assert.equal(element("response-list").children.length, 0);
  }));

test("HTTP errors and invalid responses are visible in the update history", async () => {
  for (const invalid of [false, true]) {
    await withBrowser(async browser => {
      const { element, tick, flush } = browser;
      await element("start").handlers.click();
      tick(16);
      browser.reply("down", invalid
        ? { json: async () => ({}) }
        : { ok: false, status: 429, text: async () => JSON.stringify({ detail: "Offline quota error." }) });
      await flush();
      assert.equal(element("response-count").textContent, "1 RICEVUTE");
      const row = element("response-list").children[0];
      assert.equal(row.className, "response-event response-error");
      assert.ok(row.textContent.includes(invalid ? "ERRORE: RISPOSTA NON VALIDA" : "ERRORE HTTP 429"));
      assert.equal(element("game-status").textContent, "ERRORE / IN PAUSA");
    });
  }
});

test("a late response from a reset game cannot repopulate the new game history", () =>
  withBrowser(async browser => {
    const { element, tick, flush } = browser;
    await element("start").handlers.click();
    tick(16);
    assert.equal(browser.requests, 1);
    element("reset").handlers.click();
    browser.reply("down");
    await flush();
    assert.equal(element("response-count").textContent, "0 RICEVUTE");
    assert.equal(element("response-list").children.length, 0);
    assert.equal(element("last-update").textContent, "Nessuna risposta ricevuta");
  }));

test("browser returns the paddle to center after its hit without calls and ignores a pending late reply", () =>
  withBrowser(async browser => {
    const { element, tick, flush, paddlePositions } = browser;
    await element("start").handlers.click();
    let answered = 0;
    let previousX = 0;
    let hit = false;
    for (let frame = 0; frame < 300; frame++) {
      tick(16);
      if (browser.requests > answered && paddlePositions.at(-1) < 450) {
        browser.reply("down");
        answered = browser.requests;
        await flush();
      }
      if (previousX > browser.ball.x && browser.ball.x > 800) {
        hit = true;
        break;
      }
      previousX = browser.ball.x;
    }
    assert.equal(hit, true, "the test must reach a real right-paddle collision");
    assert.ok(browser.requests > answered, "a response must still be pending at the hit");
    assert.equal(browser.pendingSignal.aborted, true, "the hit must cancel the old turn");
    const stoppedAt = paddlePositions.at(-1);
    const callsAtHit = browser.requests;
    const decisionsAtHit = element("decisions").textContent;
    browser.reply("up");
    await flush();
    // Follow the ball all the way through the center into the human's half.
    for (let frame = 0; frame < 240 && browser.ball.x >= 400; frame++) tick(16);
    assert.ok(browser.ball.x < 480, "the ball must reach the opponent's half");
    assert.equal(element("ball-speed").textContent, "242.0 px/s",
      "the live readout must reflect the actual ten percent increase after a hit");
    assert.equal(browser.requests, callsAtHit, "no model calls between its hit and the opponent's turn");
    assert.ok(stoppedAt > 256, "the hit must take place away from the center");
    assert.equal(paddlePositions.at(-1), 256, "the paddle must return exactly to center without inference");
    assert.equal(element("decisions").textContent, decisionsAtHit, "a cancelled response must be ignored");
    assert.ok(element("response-list").children[0].textContent.includes("SCARTATA: FUORI TURNO"));
    assert.equal(element("request-state").textContent, "ATTESA PALLA");
    for (const state of browser.states) {
      assert.ok(state.ball.x >= 480 && state.ball.x <= 911);
      assert.ok(state.ball.velocityX > 0);
    }
    element("pause").handlers.click();
  }));

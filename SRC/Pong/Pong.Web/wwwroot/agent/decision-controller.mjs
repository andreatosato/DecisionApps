import { RULES, isModelTurn, snapshot } from "../game/physics.mjs";

const MAX_DECISION_AGE_MS = 350;

export class DecisionController {
  request = null;
  generation = 0;
  session = 0;
  sequence = 0;
  nextDecision = 0;
  direction = 0;
  directionExpires = 0;
  decisions = 0;
  observations = [];
  estimatedResponseMilliseconds = null;
  model = "decision";
  models = [];
  playMode = "realtime";
  stepRemaining = 0;
  requestId = 0;
  finishRequest = null;

  constructor(onUpdate, onError) {
    this.onUpdate = onUpdate;
    this.onError = onError;
  }

  async loadConfiguration() {
    const response = await fetch("/api/configuration", { signal: AbortSignal.timeout(10000) });
    if (!response.ok) throw new Error(await errorMessage(response));
    const configuration = await response.json();
    if (typeof configuration.deployment !== "string" || !configuration.deployment.trim()) {
      throw new Error("Configurazione del deployment non valida.");
    }
    if (!Array.isArray(configuration.models) || !configuration.models.some(model => model.id === "decision" && model.available)) {
      throw new Error("Configurazione dei modelli non valida.");
    }
    this.models = configuration.models;
    return configuration;
  }

  selectModel(id) {
    if (!this.models.some(model => model.id === id && model.available)) {
      throw new Error("Modello non configurato o non disponibile.");
    }
    this.model = id;
  }

  cancel(reason = "FINE TURNO") {
    this.finishRequest?.("cancelled", `ANNULLATA: ${reason}`);
    this.finishRequest = null;
    this.generation++;
    this.request?.abort();
    this.request = null;
    this.direction = 0;
    this.directionExpires = 0;
    this.stepRemaining = 0;
    this.nextDecision = 0;
    this.observations.length = 0;
    this.onUpdate({ type: "idle" });
  }

  reset() {
    this.cancel("NUOVA PARTITA");
    this.session++;
    this.decisions = 0;
    this.estimatedResponseMilliseconds = null;
  }

  getDirection(now) {
    if (this.directionExpires && now >= this.directionExpires) {
      this.direction = 0;
      this.directionExpires = 0;
      this.onUpdate({ type: "expired" });
    }
    return this.direction;
  }

  update(game, now) {
    if (!isModelTurn(game)) {
      if (this.request || this.directionExpires) this.cancel();
      this.observations.length = 0;
      this.onUpdate({ type: "waiting", returning: game.modelReturning });
      return;
    }
    this.captureObservation(game, now);
    void this.requestDecision(game, now);
  }

  prepare(game, diagnostic = false) {
    return this.requestDecision(game, performance.now(), true, diagnostic);
  }

  captureObservation(game, now, force = false) {
    const previous = this.observations.at(-1);
    if (previous && (previous.velocityX !== game.ball.velocityX || previous.velocityY !== game.ball.velocityY)) {
      this.observations.length = 0;
    }
    while (this.observations.length && this.observations[0].timestampMilliseconds < now - 1000) {
      this.observations.shift();
    }
    const latest = this.observations.at(-1);
    if (latest?.timestampMilliseconds === now || (!force && latest && now - latest.timestampMilliseconds < 40)) return;
    this.observations.push({
      timestampMilliseconds: now, ballX: game.ball.x, ballY: game.ball.y,
      velocityX: game.ball.velocityX, velocityY: game.ball.velocityY, paddleY: game.modelY
    });
    if (this.observations.length > 5) this.observations.shift();
  }

  async requestDecision(game, now, preparing = false, diagnostic = false) {
    if (!isModelTurn(game) || this.request) return;
    if (!preparing && (this.playMode === "steps" ? this.stepRemaining > 0 : now < this.nextDecision)) return;
    if (game.ball.x < RULES.ballRadius || game.ball.x > RULES.width - RULES.ballRadius) return;
    const controller = new AbortController();
    this.request = controller;
    const generation = this.generation;
    const rally = game.rally;
    const session = this.session;
    const sequence = preparing ? 0 : ++this.sequence;
    const started = performance.now();
    const requestId = ++this.requestId;
    this.captureObservation(game, now, true);
    const state = {
      ...snapshot(game, sequence),
      model: this.model,
      playMode: this.playMode,
      history: this.observations.map(point => ({ ...point })),
      observedAtMilliseconds: now,
      estimatedResponseMilliseconds: this.estimatedResponseMilliseconds
    };
    this.nextDecision = now + RULES.decisionSeconds * 1000;
    let finished = false;
    const finish = (status, outcome) => {
      if (finished) return;
      finished = true;
      if (this.session !== session) return;
      this.onUpdate({ type: "request-end", requestId, status, outcome, elapsed: performance.now() - started });
    };
    this.finishRequest = finish;
    this.onUpdate({ type: "request", state, requestId, started, diagnostic, preparing });
    let received = false;
    let recorded = false;
    const report = (action, state, outcome) => {
      recorded = true;
      finish(state, outcome);
      if (this.session !== session) return;
      this.onUpdate({ type: "received", sequence, action, state, outcome, elapsed: performance.now() - started });
    };
    try {
      const response = await fetch("/api/decisions", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(state),
        signal: AbortSignal.any([controller.signal, AbortSignal.timeout(preparing ? 45000 : 10000)])
      });
      received = true;
      if (!response.ok) {
        report(null, "error", `ERRORE HTTP ${response.status}`);
        throw new Error(await errorMessage(response));
      }
      const result = await response.json();
      if (this.generation !== generation || game.rally !== rally || !isModelTurn(game)) {
        report(result?.action, "discarded", "SCARTATA: FUORI TURNO");
        return;
      }
      validateDecision(result, sequence);
      this.decisions++;
      const age = performance.now() - started;
      if (!preparing) {
        this.estimatedResponseMilliseconds = this.estimatedResponseMilliseconds === null ? age :
          this.estimatedResponseMilliseconds * 0.75 + age * 0.25;
      }
      const applied = !preparing && (this.playMode === "steps" || age <= MAX_DECISION_AGE_MS);
      this.direction = applied ? (result.action === "up" ? -1 : result.action === "down" ? 1 : 0) : 0;
      this.directionExpires = applied && this.playMode === "realtime" ? started + MAX_DECISION_AGE_MS : 0;
      this.stepRemaining = applied && this.playMode === "steps" ? RULES.decisionSeconds : 0;
      this.onUpdate({ type: "decision", result, age, applied, preparing, diagnostic, count: this.decisions });
      report(result.action, preparing ? "prepared" : applied ? "applied" : "discarded",
        diagnostic ? "TEST: RISPOSTA RICEVUTA" :
          preparing ? "PRONTA: PREPARAZIONE" : applied ? "APPLICATA" : "SCARTATA: SUPERATA");
      return true;
    } catch (error) {
      finish(controller.signal.aborted ? "cancelled" : "error",
        controller.signal.aborted ? "ANNULLATA" : error?.name === "TimeoutError" ? "TIMEOUT" :
          `ERRORE: ${error instanceof Error ? error.message : "Chiamata non riuscita"}`);
      if (received && !recorded) {
        report(null, controller.signal.aborted ? "discarded" : "error",
          controller.signal.aborted ? "SCARTATA: ANNULLATA" : "ERRORE: RISPOSTA NON VALIDA");
      }
      if (controller.signal.aborted || this.generation !== generation) return;
      this.onError(error?.name === "TimeoutError" ? "Tempo scaduto in attesa del modello. Riprova manualmente." :
        error instanceof Error ? error.message : "Errore durante la chiamata al modello.");
    } finally {
      if (this.request === controller) {
        this.request = null;
        this.finishRequest = null;
        this.onUpdate({ type: "settled" });
      }
    }
  }
}

function validateDecision(result, sequence) {
  if (result?.sequence !== sequence || !["up", "down", "stay"].includes(result.action) ||
      !Number.isFinite(result.probabilities?.up) || !Number.isFinite(result.probabilities?.down) ||
      !Number.isFinite(result.probabilities?.stay) ||
      result.probabilities.up < 0 || result.probabilities.up > 1 ||
      result.probabilities.down < 0 || result.probabilities.down > 1 ||
      result.probabilities.stay < 0 || result.probabilities.stay > 1 ||
      Object.keys(result.probabilities).length !== 3 ||
      Math.abs(result.probabilities.up + result.probabilities.down + result.probabilities.stay - 1) > 0.001 ||
      result.probabilities[result.action] < Math.max(...Object.values(result.probabilities)) ||
      !Number.isFinite(result.elapsedMilliseconds) || result.elapsedMilliseconds < 0) {
    throw new Error("Risposta Decision non valida. La racchetta non e stata mossa.");
  }
}

async function errorMessage(response) {
  const content = await response.text();
  try {
    const problem = JSON.parse(content);
    if (typeof problem.detail === "string" && problem.detail) return problem.detail;
    if (typeof problem.title === "string" && problem.title) return problem.title;
  } catch {
    // A proxy can return HTML rather than Problem Details; never render its body.
  }
  return `Il servizio ha restituito HTTP ${response.status}.`;
}

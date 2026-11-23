import { RULES } from "../game/physics.mjs";

const digits = [
  ["111", "101", "101", "101", "111"], ["010", "110", "010", "010", "111"],
  ["111", "001", "111", "100", "111"], ["111", "001", "111", "001", "111"],
  ["101", "101", "111", "001", "001"], ["111", "100", "111", "001", "111"],
  ["111", "100", "111", "101", "111"], ["111", "001", "001", "001", "001"],
  ["111", "101", "111", "101", "111"], ["111", "101", "111", "001", "111"]
];

export class PongView {
  lastReadout = 0;
  responses = 0;
  responseHistory = [];
  modelLabel = "Decision 1.1";
  matchSamples = 0;
  httpMilliseconds = 0;
  totalMilliseconds = 0;
  inputTokens = 0;
  outputTokens = 0;
  missingUsage = 0;
  requests = 0;
  requestHistory = [];
  playMode = "realtime";

  constructor() {
    this.canvas = document.getElementById("court");
    this.context = this.canvas.getContext("2d");
    this.elements = Object.fromEntries([
      "start", "pause", "reset", "game-overlay", "overlay-start", "overlay-kicker",
      "overlay-title", "overlay-copy", "game-status", "error", "configuration",
      "action-arrow", "action-name", "request-state", "live-dot", "up-value",
      "down-value", "stay-value", "up-bar", "down-bar", "stay-bar", "latency", "response-time", "decisions", "tokens",
      "ball-x", "ball-y", "ball-vx", "ball-vy", "ball-speed", "score-announcement",
      "response-count", "last-update", "response-list", "decision-context", "observation-history",
      "model-select", "model-name", "model-protocol", "intro-model", "opponent-label", "probability-help",
      "match-tokens", "match-time", "match-samples", "play-mode", "mode-help",
      "test-model", "diagnostic-result", "diagnostic-panel", "request-list", "request-count", "request-elapsed"
    ].map(id => [id, document.getElementById(id)]));
  }

  reset(game, configured) {
    const elements = this.elements;
    this.responses = 0;
    this.matchSamples = this.httpMilliseconds = this.totalMilliseconds = 0;
    this.inputTokens = this.outputTokens = this.missingUsage = 0;
    elements["match-tokens"].textContent = "--";
    elements["match-time"].textContent = "--";
    elements["match-samples"].textContent = "0";
    this.setSelectionLocked(!configured);
    this.requests = 0;
    this.requestHistory.length = 0;
    elements["request-list"].replaceChildren();
    elements["request-count"].textContent = "0 INVIATE";
    elements["request-elapsed"].textContent = "--";
    elements["diagnostic-result"].textContent = "Nessun test eseguito.";
    this.responseHistory.length = 0;
    elements.decisions.textContent = "0";
    elements["response-count"].textContent = "0 RICEVUTE";
    elements["last-update"].textContent = "Nessuna risposta ricevuta";
    elements["response-list"].replaceChildren();
    elements.latency.textContent = "--";
    elements["response-time"].textContent = "--";
    elements.tokens.textContent = "--";
    elements["action-arrow"].textContent = "\u2014";
    elements["action-name"].textContent = "IN ATTESA";
    for (const action of ["up", "down", "stay"]) {
      elements[`${action}-value`].textContent = "--";
      elements[`${action}-bar`].style.width = "0%";
    }
    elements["decision-context"].textContent = "Nessuna osservazione inviata.";
    elements["observation-history"].textContent = "";
    elements.error.hidden = true;
    elements.pause.disabled = true;
    elements.start.disabled = !configured;
    elements.reset.disabled = !configured;
    elements.start.textContent = "INIZIA PARTITA";
    elements["game-status"].textContent = configured ? "PRONTO" : "NON CONFIGURATO";
    this.overlay("UMANO VS MODELLO", "PRONTO AL SERVIZIO?",
      `Il primo a 7 vince. Al primo avvio preparo ${this.modelLabel} prima del servizio.`, "INIZIA PARTITA");
    this.announceScore(game);
    this.updateReadout(game);
  }

  setLoading(loading) {
    this.elements.start.disabled = loading;
    this.elements["overlay-start"].disabled = loading;
    if (loading) this.setSelectionLocked(true);
  }

  setSelectionLocked(locked) {
    for (const id of ["model-select", "play-mode", "test-model"]) this.elements[id].disabled = locked;
  }

  showPlayMode(mode) {
    this.playMode = mode;
    this.elements["play-mode"].value = mode;
    this.elements["mode-help"].textContent = mode === "steps" ?
      "A PASSI: campo fermo durante l'inferenza, poi 100 ms simulati per scelta. Latenza reale misurata. Consigliato per Luna." :
      "TEMPO REALE: il campo continua durante l'inferenza; risposte oltre 350 ms non applicate. Per Luna prova A PASSI.";
  }

  showTesting() {
    this.showPreparing();
    this.elements["diagnostic-panel"].open = true;
    this.elements["game-status"].textContent = "TEST MODELLO";
    this.elements["diagnostic-result"].textContent = "Test in corso: campo fermo, attendo la risposta...";
    this.overlay("DIAGNOSTICA", "TEST MODELLO...",
      "Una chiamata reale senza avviare la partita. Risposta, token e tempi compariranno nel pannello.", "ATTENDI MODELLO...");
    this.elements["overlay-start"].disabled = true;
  }

  showTestCompleted() {
    this.setSelectionLocked(false);
    this.elements.start.disabled = false;
    this.elements.start.textContent = "INIZIA PARTITA";
    this.elements.pause.disabled = true;
    this.elements["game-status"].textContent = "TEST COMPLETATO";
    this.overlay("DIAGNOSTICA", "MODELLO RAGGIUNGIBILE",
      "Test riuscito. Risposta e misure nel pannello; nessuna partita avviata.", "INIZIA PARTITA");
  }

  showConfiguration(configuration) {
    this.elements["model-select"].replaceChildren(...configuration.models.map(model => {
      const option = document.createElement("option");
      option.value = model.id;
      option.textContent = model.available ? `${model.label} / ${model.deployment}` : `${model.label} / NON CONFIGURATO`;
      option.disabled = !model.available;
      return option;
    }));
    this.elements["model-select"].value = "decision";
    this.showModel(configuration.models.find(model => model.id === "decision"));
  }

  showModel(model) {
    this.modelLabel = model.id === "gpt" ? model.deployment : model.label;
    this.elements["model-name"].textContent = model.id === "gpt" ? model.deployment : model.label;
    this.elements["model-protocol"].textContent = model.protocol;
    this.elements["intro-model"].textContent = `${model.id === "gpt" ? model.deployment : model.label}.`;
    this.elements["opponent-label"].textContent = model.id === "gpt" ? model.deployment : model.label;
    this.elements.configuration.textContent = `Deployment: ${model.deployment} / ${model.authentication}`;
    this.elements["probability-help"].textContent = model.id === "gpt" ?
      "Stime dichiarate da GPT, non probabilita calibrate o logprobs." : "Probabilita restituite da Decision.";
    this.elements["overlay-copy"].textContent = `Il primo a 7 vince. Al primo avvio preparo ${this.modelLabel} prima del servizio.`;
  }

  showConfigurationUnavailable() {
    this.elements.configuration.textContent = "Servizio non raggiungibile. Verifica l'avvio di Pong.Web.";
    this.overlay("SERVIZIO NON DISPONIBILE", "CONNESSIONE INTERROTTA", "Verifica il servizio e riprova.", "RIPROVA CONNESSIONE");
    this.elements.start.textContent = "RIPROVA CONNESSIONE";
  }

  showStarted() {
    this.setSelectionLocked(true);
    this.elements.error.hidden = true;
    this.elements["game-overlay"].hidden = true;
    this.elements.start.disabled = true;
    this.elements.pause.disabled = false;
    this.elements.reset.disabled = false;
    this.elements["game-status"].textContent = "IN GIOCO";
    this.canvas.focus({ preventScroll: true });
  }

  showPreparing() {
    this.setSelectionLocked(true);
    this.elements.error.hidden = true;
    this.elements.start.disabled = true;
    this.elements.pause.disabled = false;
    this.elements.reset.disabled = false;
    this.elements["game-status"].textContent = `WAITING / ${this.modelLabel.toUpperCase().replace(" 1.1", "")}`;
    this.overlay("PREPARAZIONE AI", "WAITING...",
      `Prima chiamata a ${this.modelLabel}: preparazione di autenticazione e modello. Il servizio parte appena l'AI e pronta.`,
      "ATTENDI MODELLO...");
    this.elements["overlay-start"].disabled = true;
  }

  showPaused(reason) {
    this.elements.pause.disabled = true;
    this.elements.start.disabled = false;
    this.elements.start.textContent = "RIPRENDI";
    this.elements["game-status"].textContent = "IN PAUSA";
    this.overlay("TIME OUT", "PARTITA IN PAUSA", reason, "RIPRENDI PARTITA");
  }

  showError(message) {
    this.elements.error.textContent = message;
    this.elements.error.hidden = false;
    this.elements["game-status"].textContent = "ERRORE / IN PAUSA";
  }

  showFinished(game) {
    this.setSelectionLocked(false);
    this.elements.pause.disabled = true;
    this.elements.start.disabled = false;
    this.elements.start.textContent = "RIVINCITA";
    this.elements["game-status"].textContent = "PARTITA TERMINATA";
    const humanWins = game.winner === "player";
    this.overlay("GAME OVER", humanWins ? "HAI VINTO." : `VINCE ${this.modelLabel.toUpperCase()}.`,
      `${game.playerScore} a ${game.modelScore}. Un'altra partita?`, "RIVINCITA");
    this.elements["score-announcement"].textContent += humanWins ? " Hai vinto." : ` Vince ${this.modelLabel}.`;
  }

  overlay(kicker, title, copy, button) {
    this.elements["overlay-kicker"].textContent = kicker;
    this.elements["overlay-title"].textContent = title;
    this.elements["overlay-copy"].textContent = copy;
    this.elements["overlay-start"].textContent = button;
    this.elements["overlay-start"].disabled = false;
    this.elements["game-overlay"].hidden = false;
  }

  announceScore(game) {
    this.elements["score-announcement"].textContent = `Punteggio: tu ${game.playerScore}, ${this.modelLabel} ${game.modelScore}.`;
  }

  updateAgent(update) {
    const elements = this.elements;
    switch (update.type) {
      case "idle":
        elements["request-state"].textContent = "IN ATTESA";
        elements["live-dot"].classList.remove("active");
        break;
      case "waiting":
        elements["request-state"].textContent = update.returning ? "RIENTRO AL CENTRO" : "ATTESA PALLA";
        elements["game-status"].textContent = "IN GIOCO";
        break;
      case "request":
        this.recordRequest(update);
        this.showRequest(update.state);
        if (update.state.playMode === "steps" && !update.preparing) {
          elements["game-status"].textContent = "A PASSI / ATTESA MODELLO";
        }
        break;
      case "request-end":
        this.finishRequest(update);
        break;
      case "received":
        this.recordResponse(update);
        break;
      case "decision":
        this.showDecision(update);
        break;
      case "expired":
        elements["action-arrow"].textContent = "\u2014";
        elements["action-name"].textContent = "SCELTA SCADUTA";
        break;
      case "settled":
        elements["live-dot"].classList.remove("active");
        break;
    }
  }

  showRequest(state) {
    const latency = state.estimatedResponseMilliseconds === null ? "RTT non ancora misurato" :
      `RTT stimato ${Math.round(state.estimatedResponseMilliseconds)} ms`;
    this.elements["decision-context"].textContent = `${state.history.length}/5 osservazioni reali / ${latency}`;
    this.elements["observation-history"].textContent = state.history.map(point =>
      `t=${Math.round(point.timestampMilliseconds)} ms\n` +
      `palla (${point.ballX.toFixed(1)}, ${point.ballY.toFixed(1)})\n` +
      `v=(${Math.round(point.velocityX)}, ${Math.round(point.velocityY)}) px/s / racchetta Y=${point.paddleY.toFixed(1)}`
    ).join("\n\n");
    this.elements["request-state"].textContent = "DECIDE...";
    this.elements["live-dot"].classList.add("active");
  }

  showDecision({ result, age, applied, preparing, diagnostic, count }) {
    const elements = this.elements;
    elements.decisions.textContent = String(count);
    elements.latency.textContent = `${Math.round(result.elapsedMilliseconds)} ms`;
    elements["response-time"].textContent = `${Math.round(age)} ms`;
    elements.tokens.textContent = result.usage?.totalTokens == null ? "non disponibili" :
      result.usage.totalTokens.toLocaleString("it-IT");
    if (!preparing) {
      this.matchSamples++;
      this.httpMilliseconds += result.elapsedMilliseconds;
      this.totalMilliseconds += age;
      if (result.usage) {
        this.inputTokens += result.usage.inputTokens;
        this.outputTokens += result.usage.outputTokens;
      } else {
        this.missingUsage++;
      }
      elements["match-samples"].textContent = String(this.matchSamples);
      elements["match-time"].textContent =
        `${Math.round(this.httpMilliseconds / this.matchSamples)} / ${Math.round(this.totalMilliseconds / this.matchSamples)} ms`;
      elements["match-tokens"].textContent =
        this.missingUsage === this.matchSamples ? `non disponibili (${this.missingUsage} senza usage)` :
          `${this.inputTokens} / ${this.outputTokens} / ${this.inputTokens + this.outputTokens}` +
          (this.missingUsage ? ` (parziale: ${this.missingUsage} senza usage)` : "");
    }
    for (const action of ["up", "down", "stay"]) {
      const probability = result.probabilities[action] * 100;
      elements[`${action}-value`].textContent = `${probability.toFixed(1)}%`;
      elements[`${action}-bar`].style.width = `${probability}%`;
    }
    if (preparing) {
      if (diagnostic) {
        elements["diagnostic-result"].textContent = JSON.stringify({
          ...result, totalMilliseconds: Math.round(age)
        }, null, 2);
      }
      elements["action-arrow"].textContent = "\u2014";
      elements["action-name"].textContent = "AI PRONTA";
      elements["request-state"].textContent = "PREPARAZIONE COMPLETATA";
      return;
    }
    if (!applied) {
      elements["action-arrow"].textContent = "\u2014";
      elements["action-name"].textContent = "STATO SUPERATO";
      elements["request-state"].textContent = "RISPOSTA SUPERATA";
      return;
    }
    elements["game-status"].textContent = this.playMode === "steps" ? "IN GIOCO / A PASSI" : "IN GIOCO";
    elements["action-arrow"].textContent = result.action === "up" ? "\u2191" : result.action === "down" ? "\u2193" : "=";
    elements["action-name"].textContent = result.action === "up" ? "SALI" : result.action === "down" ? "SCENDI" : "FERMO";
    elements["request-state"].textContent = "RICEVUTA";
  }

  recordResponse({ sequence, action, state, outcome, elapsed }) {
    const timestamp = new Date().toLocaleTimeString("it-IT", {
      hour: "2-digit", minute: "2-digit", second: "2-digit", fractionalSecondDigits: 3
    });
    this.responses++;
    this.elements["response-count"].textContent = `${this.responses} RICEVUTE`;
    this.elements["last-update"].textContent = `#${sequence} / ${timestamp}`;
    const direction = action === "up" ? "SU" : action === "down" ? "GIU" : action === "stay" ? "FERMO" : "--";
    this.responseHistory.unshift({ sequence, timestamp, direction, state, outcome, elapsed });
    this.responseHistory.length = Math.min(this.responseHistory.length, 8);
    this.elements["response-list"].replaceChildren(...this.responseHistory.map(entry => {
      const row = document.createElement("li");
      row.className = `response-event response-${entry.state}`;
      row.textContent = `#${entry.sequence} / ${entry.timestamp}\n${entry.direction} / ${entry.outcome} / ${Math.round(entry.elapsed)} ms`;
      return row;
    }));
  }

  recordRequest({ requestId, started, state, diagnostic }) {
    this.requests++;
    this.elements["request-count"].textContent = `${this.requests} INVIATE`;
    this.requestHistory.unshift({
      requestId, started, sequence: state.sequence, model: this.modelLabel,
      outcome: diagnostic ? "INVIATA: TEST" : "INVIATA", status: "pending", elapsed: 0, diagnostic
    });
    this.requestHistory.length = Math.min(this.requestHistory.length, 8);
    this.renderRequests();
  }

  finishRequest({ requestId, status, outcome, elapsed }) {
    const request = this.requestHistory.find(entry => entry.requestId === requestId);
    if (!request) return;
    Object.assign(request, { status, outcome, elapsed });
    if (request.diagnostic && (status === "error" || status === "cancelled")) {
      this.elements["diagnostic-result"].textContent = `${outcome} / ${Math.round(elapsed)} ms`;
    }
    this.renderRequests();
  }

  renderRequests() {
    this.elements["request-list"].replaceChildren(...this.requestHistory.map(entry => {
      const row = document.createElement("li");
      row.className = `response-event response-${entry.status}`;
      row.textContent = `#${entry.sequence} / ${entry.model}\n${entry.outcome} / ${Math.round(entry.elapsed)} ms`;
      return row;
    }));
    const latest = this.requestHistory[0];
    this.elements["request-elapsed"].textContent = latest ? `${Math.round(latest.elapsed)} ms` : "--";
  }

  render(game, now) {
    this.draw(game);
    if (now - this.lastReadout > 100) {
      const pending = this.requestHistory.find(entry => entry.status === "pending");
      if (pending) {
        pending.elapsed = now - pending.started;
        this.renderRequests();
      }
      this.updateReadout(game);
      this.lastReadout = now;
    }
  }

  updateReadout(game) {
    this.elements["ball-speed"].textContent = `${Math.hypot(game.ball.velocityX, game.ball.velocityY).toFixed(1)} px/s`;
    for (const [id, value, units] of [
      ["ball-x", game.ball.x, "px"], ["ball-y", game.ball.y, "px"],
      ["ball-vx", game.ball.velocityX, "px/s"], ["ball-vy", game.ball.velocityY, "px/s"]
    ]) {
      this.elements[id].replaceChildren(document.createTextNode(`${Math.round(value)} `),
        Object.assign(document.createElement("small"), { textContent: units }));
    }
  }

  drawDigit(number, x, y) {
    const context = this.context;
    context.fillStyle = "#e3edac";
    digits[number].forEach((row, rowIndex) => {
      [...row].forEach((value, column) => {
        if (value === "1") context.fillRect(x + column * 10, y + rowIndex * 10, 8, 8);
      });
    });
  }

  draw(game) {
    const context = this.context;
    context.fillStyle = "#050704";
    context.fillRect(0, 0, RULES.width, RULES.height);
    context.strokeStyle = "#eff1e4";
    context.lineWidth = 6;
    context.strokeRect(12, 12, RULES.width - 24, RULES.height - 24);
    context.fillStyle = "#d9dece";
    for (let y = 18; y < RULES.height - 18; y += 25) context.fillRect(RULES.width / 2 - 3, y, 6, 12);
    this.drawDigit(game.playerScore, RULES.width / 2 - 80, 46);
    this.drawDigit(game.modelScore, RULES.width / 2 + 50, 46);
    context.fillStyle = "#f4f4e9";
    context.fillRect(RULES.playerX - RULES.paddleWidth / 2, game.playerY - RULES.paddleHeight / 2,
      RULES.paddleWidth, RULES.paddleHeight);
    context.fillStyle = "#e3edac";
    context.fillRect(RULES.modelX - RULES.paddleWidth / 2, game.modelY - RULES.paddleHeight / 2,
      RULES.paddleWidth, RULES.paddleHeight);
    context.fillStyle = "#f4f4e9";
    context.beginPath();
    context.arc(game.ball.x, game.ball.y, RULES.ballRadius, 0, Math.PI * 2);
    context.fill();
  }
}

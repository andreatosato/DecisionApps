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

  constructor() {
    this.canvas = document.getElementById("court");
    this.context = this.canvas.getContext("2d");
    this.elements = Object.fromEntries([
      "start", "pause", "reset", "game-overlay", "overlay-start", "overlay-kicker",
      "overlay-title", "overlay-copy", "game-status", "error", "configuration",
      "action-arrow", "action-name", "request-state", "live-dot", "up-value",
      "down-value", "stay-value", "up-bar", "down-bar", "stay-bar", "latency", "response-time", "decisions", "tokens",
      "ball-x", "ball-y", "ball-vx", "ball-vy", "ball-speed", "score-announcement",
      "response-count", "last-update", "response-list", "decision-context", "observation-history"
    ].map(id => [id, document.getElementById(id)]));
  }

  reset(game, configured) {
    const elements = this.elements;
    this.responses = 0;
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
      "Il primo a 7 vince. Al primo avvio preparo Decision prima del servizio.", "INIZIA PARTITA");
    this.announceScore(game);
    this.updateReadout(game);
  }

  setLoading(loading) {
    this.elements.start.disabled = loading;
    this.elements["overlay-start"].disabled = loading;
  }

  showConfiguration(configuration) {
    this.elements.configuration.textContent = `Deployment: ${configuration.deployment} / ${configuration.authentication}`;
  }

  showConfigurationUnavailable() {
    this.elements.configuration.textContent = "Servizio non raggiungibile. Verifica l'avvio di Pong.Web.";
    this.overlay("SERVIZIO NON DISPONIBILE", "CONNESSIONE INTERROTTA", "Verifica il servizio e riprova.", "RIPROVA CONNESSIONE");
    this.elements.start.textContent = "RIPROVA CONNESSIONE";
  }

  showStarted() {
    this.elements.error.hidden = true;
    this.elements["game-overlay"].hidden = true;
    this.elements.start.disabled = true;
    this.elements.pause.disabled = false;
    this.elements.reset.disabled = false;
    this.elements["game-status"].textContent = "IN GIOCO";
    this.canvas.focus({ preventScroll: true });
  }

  showPreparing() {
    this.elements.error.hidden = true;
    this.elements.start.disabled = true;
    this.elements.pause.disabled = false;
    this.elements.reset.disabled = false;
    this.elements["game-status"].textContent = "WAITING / DECISION";
    this.overlay("PREPARAZIONE AI", "WAITING...",
      "Prima chiamata a Decision 1.1: preparazione di autenticazione e modello. Il servizio parte appena l'AI e pronta.",
      "ATTENDI DECISION...");
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
    this.elements.pause.disabled = true;
    this.elements.start.disabled = false;
    this.elements.start.textContent = "RIVINCITA";
    this.elements["game-status"].textContent = "PARTITA TERMINATA";
    const humanWins = game.winner === "player";
    this.overlay("GAME OVER", humanWins ? "HAI VINTO." : "VINCE DECISION.",
      `${game.playerScore} a ${game.modelScore}. Un'altra partita?`, "RIVINCITA");
    this.elements["score-announcement"].textContent += humanWins ? " Hai vinto." : " Vince Decision.";
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
    this.elements["score-announcement"].textContent = `Punteggio: tu ${game.playerScore}, Decision ${game.modelScore}.`;
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
        this.showRequest(update.state);
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

  showDecision({ result, age, applied, preparing, count }) {
    const elements = this.elements;
    elements.decisions.textContent = String(count);
    elements.latency.textContent = `${Math.round(result.elapsedMilliseconds)} ms`;
    elements["response-time"].textContent = `${Math.round(age)} ms`;
    elements.tokens.textContent = result.usage?.totalTokens == null ? "non disponibili" :
      result.usage.totalTokens.toLocaleString("it-IT");
    for (const action of ["up", "down", "stay"]) {
      const probability = result.probabilities[action] * 100;
      elements[`${action}-value`].textContent = `${probability.toFixed(1)}%`;
      elements[`${action}-bar`].style.width = `${probability}%`;
    }
    if (preparing) {
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
    elements["game-status"].textContent = "IN GIOCO";
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

  render(game, now) {
    this.draw(game);
    if (now - this.lastReadout > 100) {
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

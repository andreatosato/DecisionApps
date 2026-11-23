import { advanceGame, createGame, isModelTurn } from "./game/physics.mjs";
import { DecisionController } from "./agent/decision-controller.mjs";
import { PongInput } from "./ui/pong-input.mjs";
import { PongView } from "./ui/pong-view.mjs";

class PongApp {
  game = createGame();
  running = false;
  configured = false;
  configLoading = false;
  ready = false;
  matchStarted = false;
  preparation = null;
  previousFrame = performance.now();
  simulationMilliseconds = 0;

  constructor() {
    this.view = new PongView();
    this.decision = new DecisionController(update => this.view.updateAgent(update), message => this.showError(message));
    this.input = new PongInput(this.view.canvas, {
      start: () => this.start(),
      pause: reason => this.pause(reason),
      reset: () => this.reset(),
      toggle: () => this.toggle(),
      isRunning: () => this.running
    });
    this.view.elements["model-select"].addEventListener("change", () => this.selectModel());
    this.view.elements["play-mode"].addEventListener("change", () => this.selectPlayMode());
    this.view.elements["test-model"].addEventListener("click", () => this.testModel());
    if (this.view.context) {
      requestAnimationFrame(this.frame);
      void this.loadConfiguration();
    } else {
      this.showError("Canvas 2D non disponibile in questo browser.");
      this.view.elements["overlay-start"].disabled = true;
    }
  }

  reset() {
    this.running = false;
    this.matchStarted = false;
    this.preparation = null;
    this.input.clear();
    this.decision.reset();
    this.game = createGame();
    this.simulationMilliseconds = 0;
    this.view.reset(this.game, this.configured);
    this.view.showPlayMode(this.decision.playMode);
  }

  selectModel() {
    if (this.preparation || (this.matchStarted && !this.game.winner)) {
      this.view.elements["model-select"].value = this.decision.model;
      return;
    }
    try {
      this.decision.selectModel(this.view.elements["model-select"].value);
      this.ready = false;
      this.view.showModel(this.decision.models.find(model => model.id === this.decision.model));
      this.reset();
    } catch (error) {
      this.view.elements["model-select"].value = this.decision.model;
      this.showError(error instanceof Error ? error.message : "Selezione del modello non valida.");
    }
  }

  selectPlayMode() {
    const select = this.view.elements["play-mode"];
    if (this.preparation || (this.matchStarted && !this.game.winner)) {
      select.value = this.decision.playMode;
      return;
    }
    if (!["realtime", "steps"].includes(select.value)) {
      select.value = this.decision.playMode;
      this.showError("Modalita di gioco non valida.");
      return;
    }
    this.decision.playMode = select.value;
    this.reset();
  }

  async testModel() {
    if (!this.configured || this.preparation || (this.matchStarted && !this.game.winner)) return;
    if (this.game.winner) this.reset();
    this.decision.cancel();
    const preparation = this.preparation = {};
    this.view.showTesting();
    const tested = await this.decision.prepare(this.game, true);
    if (!tested || this.preparation !== preparation) return;
    this.preparation = null;
    this.decision.cancel("TEST COMPLETATO");
    this.view.showTestCompleted();
  }

  async start() {
    if (this.configLoading || this.running || this.preparation) return;
    if (!this.configured) {
      await this.loadConfiguration();
      return;
    }
    if (this.game.winner) this.reset();
    this.decision.cancel();
    this.input.clear();
    this.matchStarted = true;
    if (!this.ready) {
      const preparation = this.preparation = {};
      this.view.showPreparing();
      const prepared = await this.decision.prepare(this.game);
      if (!prepared || this.preparation !== preparation) return;
      this.preparation = null;
      this.ready = true;
      this.decision.cancel();
    }
    this.view.showStarted();
    this.previousFrame = performance.now();
    this.running = true;
  }

  pause(reason = "Il campo ti aspetta. Nessuna chiamata al modello durante la pausa.") {
    if (!this.running && !this.preparation) return;
    this.running = false;
    this.preparation = null;
    this.input.clear();
    this.decision.cancel("PAUSA");
    if (!this.matchStarted) this.view.setSelectionLocked(false);
    this.view.showPaused(reason);
  }

  toggle() {
    if (this.running || this.preparation) this.pause();
    else if (this.configured && !document.hidden) void this.start();
  }

  finish() {
    this.running = false;
    this.input.clear();
    this.decision.cancel();
    this.view.showFinished(this.game);
  }

  showError(message) {
    this.pause("Risolvi l'errore e riprendi manualmente. Nessuna azione sostitutiva.");
    this.view.showError(message);
  }

  async loadConfiguration() {
    this.configLoading = true;
    this.view.setLoading(true);
    try {
      const configuration = await this.decision.loadConfiguration();
      this.view.showConfiguration(configuration);
      this.configured = true;
      this.reset();
    } catch (error) {
      this.configured = false;
      this.showError(error instanceof Error ? error.message : "Configurazione non disponibile.");
      this.view.showConfigurationUnavailable();
    } finally {
      this.configLoading = false;
      this.view.setLoading(false);
    }
  }

  frame = now => {
    const dt = Math.max(0, Math.min((now - this.previousFrame) / 1000, 0.05));
    this.previousFrame = now;
    if (this.running) {
      const rally = this.game.rally;
      const steps = this.decision.playMode === "steps";
      let seconds = dt;
      if (steps && isModelTurn(this.game)) {
        this.decision.update(this.game, this.simulationMilliseconds);
        seconds = Math.min(seconds, this.decision.stepRemaining);
        this.decision.stepRemaining = Math.max(0, this.decision.stepRemaining - seconds);
        if (this.decision.stepRemaining < 1e-9) this.decision.stepRemaining = 0;
      }
      const playerDirection = seconds > 0 ? this.input.getDirection(this.game.playerY, seconds) : 0;
      advanceGame(this.game, seconds, playerDirection, this.decision.getDirection(now));
      this.simulationMilliseconds += seconds * 1000;
      if (this.game.rally !== rally) {
        this.decision.cancel();
        this.view.announceScore(this.game);
      }
      if (this.game.winner) this.finish();
      else this.decision.update(this.game, steps ? this.simulationMilliseconds : now);
    }
    this.view.render(this.game, now);
    requestAnimationFrame(this.frame);
  };
}

new PongApp();

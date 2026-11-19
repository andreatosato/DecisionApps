import { RULES, clamp } from "../game/physics.mjs";

export class PongInput {
  keys = new Set();
  pointerY = null;

  constructor(canvas, controls) {
    this.canvas = canvas;
    document.getElementById("start").addEventListener("click", controls.start);
    document.getElementById("overlay-start").addEventListener("click", controls.start);
    document.getElementById("pause").addEventListener("click", () => controls.pause());
    document.getElementById("reset").addEventListener("click", controls.reset);
    document.addEventListener("keydown", event => {
      if (event.target instanceof HTMLElement && event.target.closest("button,a,input,select,textarea,summary")) return;
      const key = event.key.toLowerCase();
      if (!["w", "s", "arrowup", "arrowdown", " "].includes(key)) return;
      event.preventDefault();
      if (key === " ") {
        if (!event.repeat) controls.toggle();
      } else if (controls.isRunning()) {
        this.keys.add(key);
        this.pointerY = null;
      }
    });
    document.addEventListener("keyup", event => this.keys.delete(event.key.toLowerCase()));
    canvas.addEventListener("pointerdown", event => {
      if (!controls.isRunning()) return;
      canvas.setPointerCapture(event.pointerId);
      canvas.focus({ preventScroll: true });
      this.readPointer(event);
    });
    canvas.addEventListener("pointermove", event => {
      if (controls.isRunning() && canvas.hasPointerCapture(event.pointerId)) this.readPointer(event);
    });
    canvas.addEventListener("pointerup", () => { this.pointerY = null; });
    canvas.addEventListener("pointercancel", () => { this.pointerY = null; });
    window.addEventListener("blur", () => {
      this.clear();
      controls.pause("La finestra ha perso il focus. Riprendi quando vuoi.");
    });
    document.addEventListener("visibilitychange", () => {
      if (document.hidden) controls.pause("Hai cambiato scheda: simulazione e richieste al modello sono in pausa.");
    });
  }

  clear() {
    this.keys.clear();
    this.pointerY = null;
  }

  getDirection(paddleY, dt) {
    const up = this.keys.has("w") || this.keys.has("arrowup");
    const down = this.keys.has("s") || this.keys.has("arrowdown");
    if (!up && !down && this.pointerY !== null && dt > 0) {
      return clamp((this.pointerY - paddleY) / (RULES.paddleSpeed * dt), -1, 1);
    }
    return Number(down) - Number(up);
  }

  readPointer(event) {
    const bounds = this.canvas.getBoundingClientRect();
    this.pointerY = clamp((event.clientY - bounds.top) / bounds.height * RULES.height,
      RULES.paddleHeight / 2, RULES.height - RULES.paddleHeight / 2);
  }
}

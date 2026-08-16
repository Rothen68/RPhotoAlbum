import { Directive, ElementRef, EventEmitter, OnDestroy, Output, inject } from '@angular/core';

const PRESS_DURATION_MS = 500;
const MOVE_THRESHOLD_PX = 10;

// Détection d'appui long (souris + tactile via Pointer Events) — voir ARCHITECTURE.md
// (V2, Gallery/Album Detail : entrée en mode sélection/réorganisation avec présélection).
// Supprime le clic de relâchement qui suit un appui long, pour éviter un double déclenchement
// (ex. ouverture de la visionneuse en plus de l'entrée en mode sélection).
@Directive({
  selector: '[appLongPress]',
  standalone: true,
})
export class LongPressDirective implements OnDestroy {
  private readonly el = inject(ElementRef<HTMLElement>);

  @Output() longPress = new EventEmitter<void>();

  private timer: ReturnType<typeof setTimeout> | null = null;
  private startX = 0;
  private startY = 0;
  private fired = false;

  constructor() {
    const element = this.el.nativeElement;
    element.addEventListener('pointerdown', this.onPointerDown);
    element.addEventListener('pointermove', this.onPointerMove);
    element.addEventListener('pointerup', this.cancel);
    element.addEventListener('pointercancel', this.cancel);
    element.addEventListener('click', this.onClick, true);
  }

  ngOnDestroy(): void {
    const element = this.el.nativeElement;
    element.removeEventListener('pointerdown', this.onPointerDown);
    element.removeEventListener('pointermove', this.onPointerMove);
    element.removeEventListener('pointerup', this.cancel);
    element.removeEventListener('pointercancel', this.cancel);
    element.removeEventListener('click', this.onClick, true);
    this.clearTimer();
  }

  private onPointerDown = (event: PointerEvent): void => {
    if (event.pointerType === 'mouse' && event.button !== 0) {
      return;
    }

    this.startX = event.clientX;
    this.startY = event.clientY;
    this.clearTimer();
    this.timer = setTimeout(() => {
      this.timer = null;
      this.fired = true;
      this.longPress.emit();
    }, PRESS_DURATION_MS);
  };

  private onPointerMove = (event: PointerEvent): void => {
    if (this.timer === null) {
      return;
    }

    const dx = event.clientX - this.startX;
    const dy = event.clientY - this.startY;
    if (Math.hypot(dx, dy) > MOVE_THRESHOLD_PX) {
      this.clearTimer();
    }
  };

  private cancel = (): void => {
    this.clearTimer();
  };

  // Capture-phase : intercepte le clic de relâchement qui suit un appui long déjà traité.
  private onClick = (event: MouseEvent): void => {
    if (this.fired) {
      event.preventDefault();
      event.stopImmediatePropagation();
      this.fired = false;
    }
  };

  private clearTimer(): void {
    if (this.timer !== null) {
      clearTimeout(this.timer);
      this.timer = null;
    }
  }
}

import { Component, EventEmitter, Input, Output, computed, signal } from '@angular/core';
import { VirtualRow, formatDateLabel } from '../../features/gallery/gallery-virtual';

// Barre de défilement rapide par date, façon Google Photos/iOS Photos : une poignée
// déplaçable sur le bord droit, un libellé de date flottant pendant le drag, et un index de
// rangée émis pour que le parent appelle scrollToIndex sur le viewport CDK — voir
// ARCHITECTURE.md (V2 étape 4).
@Component({
  selector: 'app-date-scrubber',
  standalone: true,
  templateUrl: './date-scrubber.component.html',
  styleUrl: './date-scrubber.component.scss',
})
export class DateScrubberComponent {
  @Input({ required: true }) rows!: VirtualRow[];
  // Index de la rangée actuellement en haut du viewport, mis à jour par le parent quel que
  // soit le mécanisme de scroll (molette, barre native, ou la poignée elle-même) — sans ça,
  // la poignée ne reflétait que ses propres interactions de drag, pas le scroll réel (bug
  // signalé : la poignée violette ne correspondait pas à la position de la barre native).
  @Input() currentIndex = 0;
  @Output() indexChange = new EventEmitter<number>();

  protected readonly dragging = signal(false);
  private readonly dragRatio = signal(0);
  protected readonly thumbRatio = computed(() =>
    this.dragging() ? this.dragRatio() : this.rows.length > 0 ? Math.min(1, this.currentIndex / this.rows.length) : 0,
  );
  protected readonly label = signal('');

  onPointerDown(event: PointerEvent): void {
    const target = event.currentTarget as HTMLElement;
    target.setPointerCapture(event.pointerId);
    this.dragging.set(true);
    this.updateFromPointer(event.clientY, target);
  }

  onPointerMove(event: PointerEvent): void {
    if (!this.dragging()) {
      return;
    }
    this.updateFromPointer(event.clientY, event.currentTarget as HTMLElement);
  }

  onPointerUp(): void {
    this.dragging.set(false);
  }

  // Mesure la poignée elle-même (event.currentTarget), pas le hôte du composant : le hôte
  // <app-date-scrubber> reste un élément inline non stylé de taille nulle, seul l'élément
  // visuel .scrubber-bar a la bonne géométrie.
  private updateFromPointer(clientY: number, barEl: HTMLElement): void {
    if (this.rows.length === 0) {
      return;
    }

    const rect = barEl.getBoundingClientRect();
    const ratio = Math.min(1, Math.max(0, (clientY - rect.top) / rect.height));
    const index = Math.min(this.rows.length - 1, Math.floor(ratio * this.rows.length));
    const row = this.rows[index];

    this.dragRatio.set(ratio);
    this.label.set(formatDateLabel(row.date));
    this.indexChange.emit(index);
  }
}

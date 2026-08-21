import { Directive, ElementRef, EventEmitter, NgZone, OnDestroy, OnInit, Output, inject } from '@angular/core';

// Émet la hauteur réelle rendue de l'hôte à chaque changement (ResizeObserver) — utilisé pour
// corriger après-coup les hauteurs de rangée précalculées de la vue album virtualisée quand le
// contenu (bloc texte Markdown) n'a pas de hauteur connaissable à l'avance (issue #30). Les
// callbacks ResizeObserver s'exécutent hors zone Angular — ngZone.run() nécessaire pour que la
// mise à jour du signal consommateur déclenche bien une détection de changement (même pattern
// que le ResizeObserver déjà en place dans AlbumDetailComponent pour containerWidth).
@Directive({
  selector: '[appMeasureHeight]',
  standalone: true,
})
export class MeasureHeightDirective implements OnInit, OnDestroy {
  private readonly el = inject(ElementRef<HTMLElement>);
  private readonly ngZone = inject(NgZone);
  @Output() readonly heightChange = new EventEmitter<number>();

  private observer?: ResizeObserver;

  ngOnInit(): void {
    // offsetHeight (boîte de bordure, padding inclus) plutôt que ResizeObserverEntry.contentRect
    // (boîte de contenu, padding EXCLU) — l'espace à réserver dans la rangée doit correspondre à
    // l'empreinte visuelle totale de l'élément, padding compris (constaté : un écart exact de la
    // valeur du padding entre les deux, la rangée restait plus courte que son propre contenu).
    const element = this.el.nativeElement;
    this.observer = new ResizeObserver(() => {
      this.ngZone.run(() => this.heightChange.emit(element.offsetHeight));
    });
    this.observer.observe(element);
  }

  ngOnDestroy(): void {
    this.observer?.disconnect();
  }
}

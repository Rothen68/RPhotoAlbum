import { Directive, ElementRef, EventEmitter, NgZone, OnDestroy, OnInit, Output, inject } from '@angular/core';

// Emits the host's actual rendered height on every change (ResizeObserver) — used to correct,
// after the fact, the precomputed row heights of the virtualized album view when the content
// (a Markdown text block) has no height knowable in advance (issue #30). ResizeObserver
// callbacks run outside the Angular zone — ngZone.run() is needed so that updating the
// consuming signal actually triggers change detection (same pattern as the ResizeObserver
// already in place in AlbumDetailComponent for containerWidth).
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
    // offsetHeight (border box, padding included) rather than ResizeObserverEntry.contentRect
    // (content box, padding EXCLUDED) — the space reserved in the row must match the element's
    // total visual footprint, padding included (observed: a discrepancy exactly equal to the
    // padding value between the two, the row stayed shorter than its own content).
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

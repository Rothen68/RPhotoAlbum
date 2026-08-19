import { CdkVirtualScrollViewport, VirtualScrollStrategy } from '@angular/cdk/scrolling';
import { Observable, Subject } from 'rxjs';
import { distinctUntilChanged } from 'rxjs/operators';

// Stratégie de virtualisation générique à hauteurs de rangée précalculées à l'avance (connues
// par le composant appelant via updateRowHeights, PAS mesurées après rendu) — extraite de
// l'implémentation Gallery (V2 étape 4) pour être réutilisée telle quelle par Album Detail
// (issue #20), qui a le même besoin (rangées de hauteur variable connue par avance) avec un
// calcul de hauteur différent (grille photo vs bloc texte plutôt que en-tête de date vs photos).
// Inspirée de CdkFixedSizeVirtualScroll, généralisée aux tailles variables.
export class PrecomputedVirtualScrollStrategy implements VirtualScrollStrategy {
  private readonly scrolledIndexChangeSubject = new Subject<number>();
  readonly scrolledIndexChange: Observable<number> = this.scrolledIndexChangeSubject.pipe(distinctUntilChanged());

  private viewport: CdkVirtualScrollViewport | null = null;
  // cumulativeOffsets[i] = offset en px où commence la rangée i ; longueur = rowCount + 1
  // (le dernier élément est la taille totale du contenu).
  private cumulativeOffsets: number[] = [0];
  private static readonly BUFFER_PX = 400;

  updateRowHeights(heights: number[]): void {
    const cumulative: number[] = [0];
    for (const h of heights) {
      cumulative.push(cumulative[cumulative.length - 1] + h);
    }
    this.cumulativeOffsets = cumulative;
    this.updateTotalContentSize();
    this.updateRenderedRange();
  }

  attach(viewport: CdkVirtualScrollViewport): void {
    this.viewport = viewport;
    this.updateTotalContentSize();
    this.updateRenderedRange();
  }

  detach(): void {
    this.scrolledIndexChangeSubject.complete();
    this.viewport = null;
  }

  onContentScrolled(): void {
    this.updateRenderedRange();
  }

  onDataLengthChanged(): void {
    this.updateRenderedRange();
  }

  onContentRendered(): void {
    // Rien à faire : les hauteurs sont connues à l'avance, pas de mesure post-rendu.
  }

  onRenderedOffsetChanged(): void {
    // Rien à faire : géré par setRenderedContentOffset dans updateRenderedRange.
  }

  scrollToIndex(index: number, behavior: ScrollBehavior): void {
    const clamped = Math.max(0, Math.min(index, this.cumulativeOffsets.length - 1));
    this.viewport?.scrollToOffset(this.cumulativeOffsets[clamped], behavior);
    // Ne dépend pas du round-trip asynchrone de l'événement 'scroll' natif : mise à jour
    // immédiate de la plage rendue, pour un saut instantané plutôt qu'un écran vide en
    // attendant que le navigateur émette l'événement.
    this.updateRenderedRange();
  }

  private updateTotalContentSize(): void {
    this.viewport?.setTotalContentSize(this.cumulativeOffsets[this.cumulativeOffsets.length - 1] ?? 0);
  }

  private updateRenderedRange(): void {
    if (!this.viewport) {
      return;
    }

    const rowCount = this.cumulativeOffsets.length - 1;
    if (rowCount <= 0) {
      this.viewport.setRenderedRange({ start: 0, end: 0 });
      this.viewport.setRenderedContentOffset(0);
      return;
    }

    const scrollOffset = this.viewport.measureScrollOffset();
    const viewportSize = this.viewport.getViewportSize();

    const startOffset = Math.max(0, scrollOffset - PrecomputedVirtualScrollStrategy.BUFFER_PX);
    const endOffset = scrollOffset + viewportSize + PrecomputedVirtualScrollStrategy.BUFFER_PX;

    const start = this.findRowAtOffset(startOffset);
    const end = Math.min(rowCount, this.findRowAtOffset(endOffset) + 1);

    this.viewport.setRenderedRange({ start, end });
    this.viewport.setRenderedContentOffset(this.cumulativeOffsets[start]);
    this.scrolledIndexChangeSubject.next(this.findRowAtOffset(scrollOffset));
  }

  // Recherche binaire : dernière rangée dont l'offset de départ est <= offset.
  private findRowAtOffset(offset: number): number {
    const rowCount = this.cumulativeOffsets.length - 1;
    let lo = 0;
    let hi = rowCount - 1;
    if (hi < 0) {
      return 0;
    }
    while (lo < hi) {
      const mid = Math.ceil((lo + hi) / 2);
      if (this.cumulativeOffsets[mid] <= offset) {
        lo = mid;
      } else {
        hi = mid - 1;
      }
    }
    return lo;
  }
}

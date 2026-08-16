import { CollectionViewer, DataSource } from '@angular/cdk/collections';
import { CdkVirtualScrollViewport, VIRTUAL_SCROLL_STRATEGY, VirtualScrollStrategy } from '@angular/cdk/scrolling';
import { Directive, OnDestroy } from '@angular/core';
import { BehaviorSubject, Observable, Subject, Subscription } from 'rxjs';
import { distinctUntilChanged } from 'rxjs/operators';
import { DateGroup, MediaItem, MediaSourcePage } from '../../core/media/media.service';

// Une rangée virtualisée : soit un en-tête de date, soit une rangée de `itemCount` photos
// consécutives dans la séquence plate (`startOffset`), au plus `columns` par rangée.
// Construite une fois à partir de date-groups seul (pas besoin des médias eux-mêmes).
export interface VirtualRow {
  type: 'header' | 'photos';
  date: string;
  startOffset: number;
  itemCount: number;
  indices: number[];
}

export function formatDateLabel(isoDate: string): string {
  const date = new Date(`${isoDate}T00:00:00`);
  return date.toLocaleDateString('fr-FR', { day: 'numeric', month: 'short', year: 'numeric' });
}

export function buildRows(dateGroups: DateGroup[], columns: number): VirtualRow[] {
  const rows: VirtualRow[] = [];
  let flatOffset = 0;

  for (const group of dateGroups) {
    rows.push({ type: 'header', date: group.date, startOffset: flatOffset, itemCount: 0, indices: [] });

    let remaining = group.count;
    while (remaining > 0) {
      const take = Math.min(columns, remaining);
      rows.push({
        type: 'photos',
        date: group.date,
        startOffset: flatOffset,
        itemCount: take,
        indices: Array.from({ length: take }, (_, i) => i),
      });
      flatOffset += take;
      remaining -= take;
    }
  }

  return rows;
}

// Pont entre les rangées (connues intégralement à l'avance) et les médias (chargés par
// pages, à la demande, selon la plage visible du viewport CDK). Le cache de pages sert
// uniquement à dédupliquer les requêtes ; les médias résolus sont exposés au composant via
// le callback onPageLoaded (source unique pour le template et la visionneuse, sous forme de
// signal côté composant). Indépendant du nombre de colonnes — pas invalidé par un changement
// de layout, seulement par une action mutante (rejet) ou un rechargement complet.
export class GalleryDataSource extends DataSource<VirtualRow> {
  private readonly rowsSubject: BehaviorSubject<VirtualRow[]>;
  private readonly pageCache = new Set<number>();
  private readonly pendingPages = new Set<number>();
  private rangeSub?: Subscription;

  constructor(
    private rows: VirtualRow[],
    private readonly pageSize: number,
    private readonly fetchPage: (page: number) => Observable<MediaSourcePage>,
    private readonly onPageLoaded: (page: number, items: MediaItem[]) => void,
  ) {
    super();
    this.rowsSubject = new BehaviorSubject(rows);
  }

  connect(collectionViewer: CollectionViewer): Observable<VirtualRow[]> {
    this.rangeSub = collectionViewer.viewChange.subscribe((range) => {
      this.ensurePagesForRange(range.start, range.end);
    });
    return this.rowsSubject.asObservable();
  }

  override disconnect(): void {
    this.rangeSub?.unsubscribe();
  }

  setRows(rows: VirtualRow[]): void {
    this.rows = rows;
    this.rowsSubject.next(rows);
  }

  invalidateMediaCache(): void {
    this.pageCache.clear();
    this.pendingPages.clear();
  }

  private ensurePagesForRange(start: number, end: number): void {
    const startRow = this.rows[start];
    const endRow = this.rows[Math.max(0, Math.min(end, this.rows.length) - 1)];
    if (!startRow || !endRow) {
      return;
    }

    const firstOffset = startRow.startOffset;
    const lastOffset = endRow.startOffset + Math.max(0, endRow.itemCount - 1);
    const firstPage = Math.floor(firstOffset / this.pageSize) + 1;
    const lastPage = Math.floor(lastOffset / this.pageSize) + 1;

    for (let page = firstPage; page <= lastPage; page++) {
      this.loadPage(page);
    }
  }

  private loadPage(page: number): void {
    if (this.pageCache.has(page) || this.pendingPages.has(page)) {
      return;
    }
    this.pendingPages.add(page);
    this.fetchPage(page).subscribe({
      next: (result) => {
        this.pageCache.add(page);
        this.pendingPages.delete(page);
        this.onPageLoaded(page, result.items);
      },
      error: () => this.pendingPages.delete(page),
    });
  }
}

// Stratégie de virtualisation sur-mesure : hauteurs de rangée variables (en-tête vs photos),
// connues à l'avance via une table d'offsets cumulés (recalculée par updateRowHeights à chaque
// changement de date-groups/colonnes/largeur de conteneur). Ne dépend jamais des pages
// effectivement chargées — voir ARCHITECTURE.md (V2 étape 4) : c'est ce qui garantit que
// scrollToIndex (barre de défilement par date) reste juste même pour des rangées pas encore
// chargées. Inspirée de CdkFixedSizeVirtualScroll, généralisée aux tailles variables.
export class GalleryVirtualScrollStrategy implements VirtualScrollStrategy {
  private readonly scrolledIndexChangeSubject = new Subject<number>();
  readonly scrolledIndexChange: Observable<number> = this.scrolledIndexChangeSubject.pipe(distinctUntilChanged());

  private viewport: CdkVirtualScrollViewport | null = null;
  // cumulativeOffsets[i] = offset en px où commence la rangée i ; longueur = rows.length + 1
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
    // immédiate de la plage rendue, pour un saut instantané (barre de date) plutôt qu'un
    // écran vide en attendant que le navigateur émette l'événement.
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

    const startOffset = Math.max(0, scrollOffset - GalleryVirtualScrollStrategy.BUFFER_PX);
    const endOffset = scrollOffset + viewportSize + GalleryVirtualScrollStrategy.BUFFER_PX;

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

// Fournit la stratégie sur-mesure au viewport CDK via le token VIRTUAL_SCROLL_STRATEGY —
// même mécanisme que CdkFixedSizeVirtualScroll, appliqué en attribut sur
// <cdk-virtual-scroll-viewport appGalleryVirtualScroll>. Le composant Gallery récupère cette
// directive via @ViewChild pour appeler updateRowHeights/scrollToIndex et s'abonner à
// scrolledIndexChange.
@Directive({
  selector: 'cdk-virtual-scroll-viewport[appGalleryVirtualScroll]',
  standalone: true,
  providers: [{ provide: VIRTUAL_SCROLL_STRATEGY, useExisting: GalleryVirtualScrollDirective }],
})
export class GalleryVirtualScrollDirective extends GalleryVirtualScrollStrategy implements OnDestroy {
  ngOnDestroy(): void {
    this.detach();
  }
}

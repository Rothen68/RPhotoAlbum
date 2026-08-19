import { CollectionViewer, DataSource } from '@angular/cdk/collections';
import { VIRTUAL_SCROLL_STRATEGY } from '@angular/cdk/scrolling';
import { Directive, OnDestroy } from '@angular/core';
import { BehaviorSubject, Observable, Subscription } from 'rxjs';
import { DateGroup, MediaItem, MediaSourcePage } from '../../core/media/media.service';
import { PrecomputedVirtualScrollStrategy } from '../../shared/virtual-scroll/precomputed-virtual-scroll-strategy';

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

// Fournit la stratégie sur-mesure (hauteurs connues à l'avance, voir PrecomputedVirtualScrollStrategy)
// au viewport CDK via le token VIRTUAL_SCROLL_STRATEGY — même mécanisme que
// CdkFixedSizeVirtualScroll, appliqué en attribut sur <cdk-virtual-scroll-viewport
// appGalleryVirtualScroll>. Le composant Gallery récupère cette directive via @ViewChild pour
// appeler updateRowHeights/scrollToIndex et s'abonner à scrolledIndexChange.
@Directive({
  selector: 'cdk-virtual-scroll-viewport[appGalleryVirtualScroll]',
  standalone: true,
  providers: [{ provide: VIRTUAL_SCROLL_STRATEGY, useExisting: GalleryVirtualScrollDirective }],
})
export class GalleryVirtualScrollDirective extends PrecomputedVirtualScrollStrategy implements OnDestroy {
  ngOnDestroy(): void {
    this.detach();
  }
}

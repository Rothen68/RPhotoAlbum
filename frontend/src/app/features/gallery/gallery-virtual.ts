import { CollectionViewer, DataSource } from '@angular/cdk/collections';
import { VIRTUAL_SCROLL_STRATEGY } from '@angular/cdk/scrolling';
import { Directive, OnDestroy } from '@angular/core';
import { BehaviorSubject, Observable, Subscription } from 'rxjs';
import { DateGroup, MediaItem, MediaSourcePage } from '../../core/media/media.service';
import { PrecomputedVirtualScrollStrategy } from '../../shared/virtual-scroll/precomputed-virtual-scroll-strategy';

// A virtualized row: either a date header, or a row of `itemCount` consecutive
// photos in the flat sequence (`startOffset`), at most `columns` per row.
// Built once from date-groups alone (no need for the media items themselves).
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

// Bridge between the rows (known entirely in advance) and the media items (loaded by
// page, on demand, according to the CDK viewport's visible range). The page cache is
// only used to deduplicate requests; resolved media items are exposed to the component via
// the onPageLoaded callback (single source for the template and the viewer, as a
// signal on the component side). Independent of the column count — not invalidated by a
// layout change, only by a mutating action (reject) or a full reload.
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

// Provides the custom strategy (heights known in advance, see PrecomputedVirtualScrollStrategy)
// to the CDK viewport via the VIRTUAL_SCROLL_STRATEGY token — same mechanism as
// CdkFixedSizeVirtualScroll, applied as an attribute on <cdk-virtual-scroll-viewport
// appGalleryVirtualScroll>. The Gallery component retrieves this directive via @ViewChild to
// call updateRowHeights/scrollToIndex and subscribe to scrolledIndexChange.
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

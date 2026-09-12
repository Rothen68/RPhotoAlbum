import { CdkVirtualScrollViewport, VirtualScrollStrategy } from '@angular/cdk/scrolling';
import { Observable, Subject } from 'rxjs';
import { distinctUntilChanged } from 'rxjs/operators';

// Generic virtualization strategy with row heights precomputed in advance (known by the
// calling component via updateRowHeights, NOT measured after render) — extracted from the
// Gallery implementation (V2 step 4) to be reused as-is by Album Detail (issue #20), which has
// the same need (rows of variable height known in advance) with a different height calculation
// (photo grid vs. text block rather than date header vs. photos).
// Inspired by CdkFixedSizeVirtualScroll, generalized to variable sizes.
export class PrecomputedVirtualScrollStrategy implements VirtualScrollStrategy {
  private readonly scrolledIndexChangeSubject = new Subject<number>();
  readonly scrolledIndexChange: Observable<number> = this.scrolledIndexChangeSubject.pipe(distinctUntilChanged());

  private viewport: CdkVirtualScrollViewport | null = null;
  // cumulativeOffsets[i] = offset in px where row i starts; length = rowCount + 1
  // (the last element is the total content size).
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
    // Nothing to do: heights are known in advance, no post-render measurement.
  }

  onRenderedOffsetChanged(): void {
    // Nothing to do: handled by setRenderedContentOffset in updateRenderedRange.
  }

  scrollToIndex(index: number, behavior: ScrollBehavior): void {
    const clamped = Math.max(0, Math.min(index, this.cumulativeOffsets.length - 1));
    this.viewport?.scrollToOffset(this.cumulativeOffsets[clamped], behavior);
    // Doesn't depend on the asynchronous round trip of the native 'scroll' event: updates the
    // rendered range immediately, for an instant jump rather than a blank screen while waiting
    // for the browser to fire the event.
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

  // Binary search: last row whose starting offset is <= offset.
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

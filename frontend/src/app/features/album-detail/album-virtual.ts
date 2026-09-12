import { CdkVirtualScrollViewport, VIRTUAL_SCROLL_STRATEGY } from '@angular/cdk/scrolling';
import { Directive, EventEmitter, OnDestroy, Output } from '@angular/core';
import { AlbumItem } from '../../core/albums/album.service';
import { PrecomputedVirtualScrollStrategy } from '../../shared/virtual-scroll/precomputed-virtual-scroll-strategy';
import { AlbumRow } from './album-layout';

// Estimate used for a text block as long as its actual height hasn't been measured yet
// (issue #30) — Markdown content has no height knowable in advance, unlike
// an image (EXIF ratio). AlbumDetailComponent measures the actual height once each block
// is rendered (MeasureHeightDirective, ResizeObserver) and corrects the corresponding entry — this
// estimate therefore only serves the very first render of a given text row, before
// correction (a slight visual readjustment possible at that point, imperceptible during normal
// scrolling thanks to the virtualization buffer zone that renders rows ahead of time).
export const TEXT_BLOCK_HEIGHT_ESTIMATE_PX = 160;

// Width/height ratio used as long as the image's actual dimensions aren't yet
// known (media not yet processed by the EXIF job, see issue #20/MediaExifService) — purely
// a layout estimate; corrects itself as soon as EXIF is available (fresh
// album load). 4:3 is a neutral compromise, neither portrait nor very wide.
const FALLBACK_ASPECT_RATIO = 4 / 3;

const ROW_GAP_PX = 8; // .row { gap: 0.5rem }

// Height of a row in the virtualized view (read-only, no overlaid editing
// controls):
// - Text block: actual height if already measured (issue #30, see measuredTextHeight), otherwise
//   TEXT_BLOCK_HEIGHT_ESTIMATE_PX while waiting for the first render.
// - Grouped row (2-3 media): forced 1:1 aspect-ratio (see .block.grouped .media-block in
//   CSS) — the height therefore only depends on column width, never on content.
// - Single media (RowSpan=1): keeps its natural ratio (existing behavior) — uses the
//   actual dimensions (Width/Height) if known, otherwise the FALLBACK_ASPECT_RATIO estimate.
export function computeRowHeight(row: AlbumRow, containerWidth: number, measuredTextHeight?: number): number {
  const first = row.items[0];
  if (first.type === 'text') {
    return measuredTextHeight ?? TEXT_BLOCK_HEIGHT_ESTIMATE_PX;
  }

  const cols = row.items.length;
  if (cols > 1) {
    return (containerWidth - ROW_GAP_PX * (cols - 1)) / cols;
  }

  const ratio = aspectRatioOf(first);
  return containerWidth / ratio;
}

function aspectRatioOf(item: AlbumItem): number {
  if (item.width && item.height) {
    return item.width / item.height;
  }
  return FALLBACK_ASPECT_RATIO;
}

@Directive({
  selector: 'cdk-virtual-scroll-viewport[appAlbumVirtualScroll]',
  standalone: true,
  providers: [{ provide: VIRTUAL_SCROLL_STRATEGY, useExisting: AlbumVirtualScrollDirective }],
})
export class AlbumVirtualScrollDirective extends PrecomputedVirtualScrollStrategy implements OnDestroy {
  // CDK attaches this strategy to its viewport during the <cdk-virtual-scroll-viewport>
  // component's own initialization — an order not guaranteed relative to the parent
  // component's effect() that pushes the heights (see AlbumDetailComponent): observed in
  // practice, when returning to the base view from Edit mode (viewport recreated), the effect
  // could fire BEFORE this attach(), pushing heights into the void (viewport still
  // internally null on the strategy side) without ever reapplying them afterward — blank
  // screen despite correct data.
  // This signal lets the component reliably push the heights JUST AFTER the actual
  // attach, rather than depending on an implicit execution order.
  @Output() readonly attached = new EventEmitter<void>();

  override attach(viewport: CdkVirtualScrollViewport): void {
    super.attach(viewport);
    this.attached.emit();
  }

  ngOnDestroy(): void {
    this.detach();
  }
}

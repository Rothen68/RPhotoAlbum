import {
  AfterViewInit,
  Component,
  ElementRef,
  NgZone,
  OnDestroy,
  OnInit,
  ViewChild,
  computed,
  effect,
  inject,
  signal,
} from '@angular/core';
import { CdkDragDrop, CdkDragMove, DragDropModule, moveItemInArray } from '@angular/cdk/drag-drop';
import { ScrollingModule } from '@angular/cdk/scrolling';
import { ActivatedRoute, Router } from '@angular/router';
import { AlbumDetail, AlbumItem, AlbumService } from '../../core/albums/album.service';
import { ConnectivityService } from '../../core/offline/connectivity.service';
import { OfflineAlbumService } from '../../core/offline/offline-album.service';
import { OfflineModeService } from '../../core/offline/offline-mode.service';
import { MarkdownEditorComponent } from '../../shared/markdown-editor/markdown-editor.component';
import { MarkdownPipe } from '../../shared/markdown.pipe';
import { MeasureHeightDirective } from '../../shared/measure-height/measure-height.directive';
import { MediaViewerComponent } from '../../shared/media-viewer/media-viewer.component';
import { isRawFileName } from '../../shared/raw-format';
import { AlbumRow, groupIntoRows } from './album-layout';
import { AlbumVirtualScrollDirective, computeRowHeight } from './album-virtual';

// CDK only auto-scrolls containers that are explicitly scrollable
// (overflow: auto/scroll) reliably — not the natural page/window scroll used here,
// observed in real testing (PC and mobile): impossible to drag an item out of the
// visible area while dragging. Manual implementation of auto-scroll near the edges.
const AUTO_SCROLL_EDGE_PX = 80;
const AUTO_SCROLL_MAX_SPEED = 18;

// navigator.onLine can be wrong or slow to update (observed in real usage: a
// request left stuck pending indefinitely after switching to airplane mode rather
// than failing cleanly) — an explicit timeout guarantees a fallback to the offline cache (#29)
// even if connectivity detection doesn't help.
const REQUEST_TIMEOUT_MS = 6000;

@Component({
  selector: 'app-album-detail',
  standalone: true,
  imports: [DragDropModule, MarkdownEditorComponent, MarkdownPipe, MeasureHeightDirective, MediaViewerComponent, ScrollingModule, AlbumVirtualScrollDirective],
  templateUrl: './album-detail.component.html',
  styleUrl: './album-detail.component.scss',
  host: { class: 'page' },
})
export class AlbumDetailComponent implements OnInit, AfterViewInit, OnDestroy {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly albumService = inject(AlbumService);
  private readonly offlineAlbumService = inject(OfflineAlbumService);
  protected readonly connectivity = inject(ConnectivityService);
  private readonly offlineMode = inject(OfflineModeService);
  private readonly hostEl = inject(ElementRef<HTMLElement>);
  private readonly ngZone = inject(NgZone);

  // Virtualized base view (issue #20): row heights precomputed from the container
  // width alone (see album-virtual.ts). Edit mode, on the other hand, stays in full,
  // non-virtualized rendering — combining virtual-scroll and per-row drag-and-drop
  // (DOM recycling during drag-triggered auto-scroll) is one of the most fragile CDK
  // combinations in practice, and Edit mode remains a deliberate, bounded action
  // session (unlike plain viewing scroll, far more frequent) — a deliberate trade-off, not a
  // fallback after failure like the Gallery date bar.
  @ViewChild(AlbumVirtualScrollDirective) private scrollStrategy?: AlbumVirtualScrollDirective;
  // Scrollable container for Edit mode (full render, no CDK viewport) — auto-scroll
  // during a drag must scroll THIS container rather than window/document now that the
  // two modes share the same height-bounded flex layout (see SCSS).
  @ViewChild('editScroll') private editScrollEl?: ElementRef<HTMLElement>;
  protected readonly containerWidth = signal(0);
  // Actual text block heights, measured after render (issue #30) — key = id of the first
  // (only) item of the text row, same key as trackRow. As long as a text row hasn't
  // been measured yet, computeRowHeight falls back to TEXT_BLOCK_HEIGHT_ESTIMATE_PX.
  protected readonly measuredTextHeights = signal<Map<string, number>>(new Map());
  protected readonly rowHeights = computed(() =>
    this.rows().map((row) =>
      computeRowHeight(row, this.containerWidth(), row.items[0].type === 'text' ? this.measuredTextHeights().get(row.items[0].id) : undefined),
    ),
  );
  private resizeObserver?: ResizeObserver;

  private albumId!: string;

  protected readonly album = signal<AlbumDetail | null>(null);
  protected readonly loading = signal(true);
  // Single mode combining text editing, add/remove and reordering of
  // media — the base view stays purely dedicated to viewing (see user
  // feedback: having a separate "Reorder" mode from permanently editable text
  // in the normal view was a source of confusion).
  protected readonly editMode = signal(false);

  protected readonly insertingAt = signal<string | null | undefined>(undefined);
  protected readonly editingItemId = signal<string | null>(null);
  protected draftText = '';

  protected readonly rows = computed(() => groupIntoRows(this.album()?.items ?? []));
  protected trackRow = (_index: number, row: AlbumRow): string => row.items[0].id;

  protected readonly viewerIndex = signal<number | null>(null);

  // --- Offline viewing (issue #29) ---
  protected readonly offlineAvailable = computed(() => this.offlineAlbumService.isOffline(this.albumId));
  protected readonly offlineMeta = computed(() => this.offlineAlbumService.metaFor(this.albumId));
  protected readonly offlineDownloading = computed(() => this.offlineAlbumService.isDownloading(this.albumId));
  protected readonly offlineProgress = computed(() => this.offlineAlbumService.progressFor(this.albumId));
  protected readonly offlinePendingRemoval = signal(false);
  protected readonly offlineError = signal<string | null>(null);
  private objectUrlMap = new Map<number, string>();
  private objectUrlGeneration = 0;

  protected readonly mediaItems = computed(() => (this.album()?.items ?? []).filter((i) => i.type === 'media'));
  protected readonly viewerItems = computed(() =>
    this.mediaItems().map((i) => ({
      fileId: i.albumCopy!.fileId,
      mediaType: i.mediaType as 'image' | 'video',
      name: i.albumCopy!.name,
      dateTaken: i.dateTaken,
      country: i.country,
      region: i.region,
      city: i.city,
    })),
  );

  protected isRaw(item: AlbumItem): boolean {
    return isRawFileName(item.albumCopy?.name ?? item.source?.name);
  }

  posterUrlFn = (fileId: number): string => this.albumService.thumbnailUrl(fileId, 400);
  imageUrlFn = (fileId: number): string => this.albumService.thumbnailUrl(fileId, 1600, false);
  streamUrlFn = (fileId: number): string => this.albumService.streamUrl(fileId);
  downloadUrlFn = (fileId: number): string => this.albumService.downloadUrl(fileId);

  private autoScrollSpeed = 0;
  private autoScrollFrame: number | null = null;

  constructor() {
    // Covers the case "heights change while the viewport is already attached" (result
    // of an album mutation). The case "the viewport was just (re)created" is covered separately
    // by pushRowHeights(), called on the directive's (attached) event — see
    // AlbumVirtualScrollDirective for the reason (the effect-vs-attach() order isn't guaranteed).
    effect(() => this.pushRowHeights());

    // Switches the displayed thumbnails to the offline cache (Object URL) as soon as
    // connectivity drops, for an album made available offline — see OfflineAlbumService
    // (issue #29). The online path (thumbnailUrl() falling back to the network URL) is unchanged.
    effect(() => {
      const offline = this.offlineMode.manualOfflineMode() || !this.connectivity.online();
      const album = this.album();
      if (offline && album && this.offlineAlbumService.isOffline(this.albumId)) {
        this.rebuildObjectUrlMap(album);
      } else {
        this.revokeObjectUrls();
      }
    });
  }

  ngOnInit(): void {
    this.albumId = this.route.snapshot.paramMap.get('id')!;
    this.load();
  }

  ngAfterViewInit(): void {
    const initialWidth = this.hostEl.nativeElement.getBoundingClientRect().width;
    if (initialWidth > 0) {
      this.containerWidth.set(initialWidth);
    }

    this.resizeObserver = new ResizeObserver((entries) => {
      const width = entries[0]?.contentRect.width ?? 0;
      if (width > 0 && width !== this.containerWidth()) {
        this.ngZone.run(() => this.containerWidth.set(width));
      }
    });
    this.resizeObserver.observe(this.hostEl.nativeElement);
  }

  ngOnDestroy(): void {
    this.resizeObserver?.disconnect();
    this.revokeObjectUrls();
  }

  // The scroll strategy (cumulative offsets) needs the TOTAL footprint of each row
  // (content + visual margin), whereas rowHeights() — used to size .row/
  // .media-block themselves — must stay at the exact content size, or the image would be
  // stretched too much. The margin (ROW_GAP_PX) must match exactly the margin-bottom of .row-wrapper
  // in the virtualized view (see SCSS): regression spotted by the user (V2.24 shipped with
  // no visual spacing between rows at all in the base view — the height calculation only
  // accounted for content until now, never spacing between rows).
  private static readonly ROW_GAP_PX = 8;

  protected pushRowHeights(): void {
    const heights = this.rowHeights().map((h) => h + AlbumDetailComponent.ROW_GAP_PX);
    this.scrollStrategy?.updateRowHeights(heights);
  }

  // Corrects a text row's height once its content is actually rendered (issue #30) —
  // updates measuredTextHeights, which recomputes rowHeights() and pushes the new
  // offsets to the virtualization strategy via the effect() already in place (constructor). Ignores
  // unchanged measurements to avoid an endless signal → change detection → resize cycle
  // when ResizeObserver returns an identical value.
  protected onTextHeightMeasured(itemId: string, height: number): void {
    if (this.measuredTextHeights().get(itemId) === height) {
      return;
    }
    const next = new Map(this.measuredTextHeights());
    next.set(itemId, height);
    this.measuredTextHeights.set(next);
  }

  private load(): void {
    this.loading.set(true);

    // Offline mode forced by the user, or already known to be offline: no point waiting for
    // the failure (sometimes slow, several seconds depending on device/network) of a network
    // request bound to fail — fall back directly to the cache if available (issue #29, user
    // feedback: the page stayed visibly stuck on "Loading…" longer than
    // necessary).
    if ((this.offlineMode.manualOfflineMode() || !this.connectivity.online()) && this.offlineAlbumService.isOffline(this.albumId)) {
      this.loadFromCache();
      return;
    }

    // Plain JS timer rather than the RxJS timeout() operator: observed in real usage that a
    // request intercepted by the service worker (every /api/* request is, even without a
    // dedicated cache rule) can stay stuck well past the RxJS timeout — until the underlying
    // TCP connection naturally fails (net::ERR_CONNECTION_TIMED_OUT, observed after several MINUTES).
    // This fallback doesn't depend on any cancellation mechanism for the HTTP request itself: once the
    // timeout passes, we show the offline fallback and simply ignore any late response.
    let settled = false;
    const fallbackTimer = setTimeout(() => {
      if (settled) {
        return;
      }
      settled = true;
      this.offlineMode.markUnreachable();
      if (this.offlineAlbumService.isOffline(this.albumId)) {
        this.loadFromCache();
      } else {
        this.loading.set(false);
      }
    }, REQUEST_TIMEOUT_MS);

    this.albumService.get(this.albumId).subscribe({
      next: (album) => {
        if (settled) {
          return;
        }
        settled = true;
        clearTimeout(fallbackTimer);
        this.album.set(album);
        this.loading.set(false);
      },
      // Server unreachable despite navigator.onLine (frequent false positive: Wi-Fi connected
      // without actual access to the Internet/server): same fallback if this album is
      // available offline, rather than simply giving up (issue #29).
      error: () => {
        if (settled) {
          return;
        }
        settled = true;
        clearTimeout(fallbackTimer);
        this.offlineMode.markUnreachable();
        if (!this.offlineAlbumService.isOffline(this.albumId)) {
          this.loading.set(false);
          return;
        }
        this.loadFromCache();
      },
    });
  }

  private loadFromCache(): void {
    this.offlineAlbumService.getCachedAlbum(this.albumId).then((cached) => {
      if (cached) {
        this.album.set(cached);
      }
      this.loading.set(false);
    });
  }

  thumbnailUrl(fileId: number): string {
    return this.objectUrlMap.get(fileId) ?? this.albumService.thumbnailUrl(fileId, 800);
  }

  private async rebuildObjectUrlMap(album: AlbumDetail): Promise<void> {
    const generation = ++this.objectUrlGeneration;
    const mediaItems = album.items.filter((i) => i.type === 'media');
    const next = await this.offlineAlbumService.buildObjectUrlMap(this.albumId, mediaItems);
    if (generation !== this.objectUrlGeneration) {
      // A more recent rebuild has already started (or connectivity switched back online) — we
      // discard this stale result rather than overwriting a more up-to-date map.
      for (const url of next.values()) {
        URL.revokeObjectURL(url);
      }
      return;
    }
    this.revokeObjectUrls();
    this.objectUrlMap = next;
  }

  private revokeObjectUrls(): void {
    for (const url of this.objectUrlMap.values()) {
      URL.revokeObjectURL(url);
    }
    this.objectUrlMap.clear();
  }

  // --- Offline viewing: actions (issue #29) ---

  makeOfflineAvailable(): void {
    const album = this.album();
    if (!album) {
      return;
    }
    this.offlineError.set(null);
    this.offlineAlbumService.makeAvailable(album).catch((err) => this.reportOfflineError(err));
  }

  // Distinguishes the failure causes (issue #29, user feedback: the generic message didn't
  // allow figuring out which one applied):
  // - insecure context (accessed over plain HTTP, not HTTPS/localhost) — the Cache Storage API
  //   then simply doesn't exist, dedicated message rather than letting the user think it's a
  //   network or storage issue;
  // - quota exceeded — actual device figures via navigator.storage.estimate();
  // - generic failure (network, or no thumbnail retrievable even after retrying).
  private async reportOfflineError(err: unknown): Promise<void> {
    if (err instanceof DOMException && err.name === 'InsecureContextError') {
      this.offlineError.set(
        "La consultation hors-ligne nécessite une connexion sécurisée (HTTPS) — non disponible en accédant via une adresse HTTP simple.",
      );
      return;
    }

    const isQuotaError = err instanceof DOMException && err.name === 'QuotaExceededError';
    if (!isQuotaError || !navigator.storage?.estimate) {
      this.offlineError.set('Échec du téléchargement hors-ligne (réseau ou espace de stockage insuffisant).');
      return;
    }

    try {
      const { usage, quota } = await navigator.storage.estimate();
      const usedMb = Math.round((usage ?? 0) / (1024 * 1024));
      const quotaMb = Math.round((quota ?? 0) / (1024 * 1024));
      this.offlineError.set(`Espace de stockage insuffisant sur cet appareil (${usedMb} Mo utilisés sur ${quotaMb} Mo disponibles pour ce site).`);
    } catch {
      this.offlineError.set('Espace de stockage insuffisant sur cet appareil.');
    }
  }

  confirmRemoveOffline(): void {
    this.offlinePendingRemoval.set(true);
  }

  cancelRemoveOffline(): void {
    this.offlinePendingRemoval.set(false);
  }

  removeOfflineAvailable(): void {
    this.offlinePendingRemoval.set(false);
    this.offlineAlbumService.remove(this.albumId);
  }

  protected formatMb(bytes: number): number {
    return Math.round(bytes / (1024 * 1024));
  }

  streamUrl(fileId: number): string {
    return this.albumService.streamUrl(fileId);
  }

  toggleEdit(): void {
    this.editMode.update((v) => !v);
    this.insertingAt.set(undefined);
    this.editingItemId.set(null);
  }

  // --- Viewer (§11.8) ---

  onMediaClick(item: AlbumItem): void {
    // Videos keep their inline playback (<video controls>, existing behavior) — opening
    // the fullscreen viewer over them would be more of a hindrance than a help since they can
    // already be played directly in the feed. Mainly useful for photos, whose thumbnail is small.
    if (item.mediaType === 'video') {
      return;
    }

    const idx = this.mediaItems().findIndex((i) => i.id === item.id);
    if (idx >= 0) {
      this.viewerIndex.set(idx);
    }
  }

  // --- Inline text insertion (§11.5) ---

  startInsert(afterItemId: string | null): void {
    this.draftText = '';
    this.insertingAt.set(afterItemId);
  }

  commitInsert(): void {
    const afterItemId = this.insertingAt();
    if (afterItemId === undefined) {
      return;
    }
    const text = this.draftText.trim();
    this.insertingAt.set(undefined);
    if (!text) {
      return;
    }

    this.albumService.addText(this.albumId, afterItemId, text).subscribe((album) => this.album.set(album));
  }

  startEditText(item: AlbumItem): void {
    // The text block stays displayed in the base view (outside Edit mode), but only
    // for viewing — clicking it doesn't open editing there.
    if (!this.editMode()) {
      return;
    }
    this.draftText = item.markdown ?? '';
    this.editingItemId.set(item.id);
  }

  commitEditText(item: AlbumItem): void {
    if (this.editingItemId() !== item.id) {
      return;
    }
    const text = this.draftText.trim();
    this.editingItemId.set(null);

    if (!text) {
      this.removeItem(item.id);
      return;
    }

    this.albumService.updateText(this.albumId, item.id, text).subscribe((album) => this.album.set(album));
  }

  // --- Grid layout (§11.7) ---

  // "Group with next" and "Separate" are two independent actions, not the two states
  // of the same button: a row already grouped at 2 must be able to grow to 3 (canGrow) AND be
  // separated (back to 1) — showing them XOR each other prevented going beyond a group of 2.
  groupWithNext(row: AlbumRow): void {
    if (!row.canGrow) {
      return;
    }
    this.setRowSpan(row.items[0].id, row.items.length + 1);
  }

  splitGroup(row: AlbumRow): void {
    this.setRowSpan(row.items[0].id, 1);
  }

  private setRowSpan(itemId: string, span: number): void {
    const ids = (this.album()?.items ?? []).map((i) => i.id);
    this.albumService.reorder(this.albumId, ids, { [itemId]: span }).subscribe((album) => this.album.set(album));
  }

  // --- Reorder (§11.6) ---

  removeItem(itemId: string): void {
    this.albumService.removeItem(this.albumId, itemId).subscribe((album) => this.album.set(album));
  }

  // Reorder unit = the ROW (a text line, or a group of 1 to 3 photos),
  // not the individual item — a single button/handle per row, a group moves as an
  // atomic block with no dedicated repositioning logic (see user feedback: per-item
  // multi-selection turned out too complex for little benefit once the
  // row was already available as a natural unit since step 7).
  moveRowUp(rowIndex: number): void {
    const rows = this.rows();
    if (rowIndex <= 0) {
      return;
    }
    const reordered = [...rows];
    [reordered[rowIndex - 1], reordered[rowIndex]] = [reordered[rowIndex], reordered[rowIndex - 1]];
    this.reorderTo(reordered.flatMap((r) => r.items.map((i) => i.id)));
  }

  moveRowDown(rowIndex: number): void {
    const rows = this.rows();
    if (rowIndex >= rows.length - 1) {
      return;
    }
    const reordered = [...rows];
    [reordered[rowIndex], reordered[rowIndex + 1]] = [reordered[rowIndex + 1], reordered[rowIndex]];
    this.reorderTo(reordered.flatMap((r) => r.items.map((i) => i.id)));
  }

  onCdkDrop(event: CdkDragDrop<AlbumRow[]>): void {
    this.stopAutoScroll();
    if (event.previousIndex === event.currentIndex) {
      return;
    }

    const rows = [...this.rows()];
    moveItemInArray(rows, event.previousIndex, event.currentIndex);
    this.reorderTo(rows.flatMap((r) => r.items.map((i) => i.id)));
  }

  onDragMoved(event: CdkDragMove): void {
    const y = event.pointerPosition.y;
    const viewportHeight = window.innerHeight;

    if (y < AUTO_SCROLL_EDGE_PX) {
      this.autoScrollSpeed = -this.scrollSpeedFor(AUTO_SCROLL_EDGE_PX - y);
    } else if (y > viewportHeight - AUTO_SCROLL_EDGE_PX) {
      this.autoScrollSpeed = this.scrollSpeedFor(y - (viewportHeight - AUTO_SCROLL_EDGE_PX));
    } else {
      this.autoScrollSpeed = 0;
    }

    if (this.autoScrollSpeed !== 0 && this.autoScrollFrame === null) {
      this.runAutoScroll();
    }
  }

  onDragEnded(): void {
    this.stopAutoScroll();
  }

  private scrollSpeedFor(distanceIntoEdgeZone: number): number {
    const ratio = Math.min(distanceIntoEdgeZone / AUTO_SCROLL_EDGE_PX, 1);
    return ratio * AUTO_SCROLL_MAX_SPEED;
  }

  private runAutoScroll(): void {
    const step = (): void => {
      if (this.autoScrollSpeed === 0) {
        this.autoScrollFrame = null;
        return;
      }
      this.editScrollEl?.nativeElement.scrollBy(0, this.autoScrollSpeed);
      this.autoScrollFrame = requestAnimationFrame(step);
    };
    this.autoScrollFrame = requestAnimationFrame(step);
  }

  private stopAutoScroll(): void {
    this.autoScrollSpeed = 0;
    if (this.autoScrollFrame !== null) {
      cancelAnimationFrame(this.autoScrollFrame);
      this.autoScrollFrame = null;
    }
  }

  // Reorders local state IMMEDIATELY (even before the network call): CDK cancels its own
  // drag rendering (preview transform) right on drop, expecting the underlying
  // data to already reflect the new order in the same tick — without this, the item briefly
  // snaps back to its original position before jumping to its final position once the
  // server response arrives (observed by the user, PC and mobile). The server call follows
  // behind to persist it; its response re-syncs state in the (rare) case where the server had to
  // adjust something (e.g. RowSpan normalization).
  private reorderTo(ids: string[]): void {
    const current = this.album();
    if (current) {
      const byId = new Map(current.items.map((i) => [i.id, i]));
      const reordered = ids.map((id) => byId.get(id)).filter((i): i is AlbumItem => !!i);
      this.album.set({ ...current, items: reordered });
    }

    this.albumService.reorder(this.albumId, ids).subscribe((album) => this.album.set(album));
  }

  goBack(): void {
    this.router.navigateByUrl('/albums');
  }
}

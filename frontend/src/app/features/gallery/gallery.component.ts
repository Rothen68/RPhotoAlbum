import {
  AfterViewInit,
  Component,
  ElementRef,
  NgZone,
  OnDestroy,
  OnInit,
  ViewChild,
  computed,
  inject,
  signal,
} from '@angular/core';
import { ScrollingModule } from '@angular/cdk/scrolling';
import { Subscription } from 'rxjs';
import { DateGroup, MediaFilters, MediaItem, MediaService } from '../../core/media/media.service';
import { AddToAlbumSheetComponent } from '../../shared/add-to-album-sheet/add-to-album-sheet.component';
import { DateScrubberComponent } from '../../shared/date-scrubber/date-scrubber.component';
import { LongPressDirective } from '../../shared/long-press.directive';
import { MediaViewerComponent } from '../../shared/media-viewer/media-viewer.component';
import { GalleryDataSource, GalleryVirtualScrollDirective, VirtualRow, buildRows, formatDateLabel } from './gallery-virtual';

const PAGE_SIZE = 60;
const COLUMNS_STORAGE_KEY = 'rphotoalbum.gallery.columns';
const HEADER_ROW_HEIGHT_PX = 40;
const GRID_GAP_PX = 6;

@Component({
  selector: 'app-gallery',
  standalone: true,
  imports: [
    AddToAlbumSheetComponent,
    MediaViewerComponent,
    LongPressDirective,
    ScrollingModule,
    GalleryVirtualScrollDirective,
    DateScrubberComponent,
  ],
  templateUrl: './gallery.component.html',
  styleUrl: './gallery.component.scss',
})
export class GalleryComponent implements OnInit, AfterViewInit, OnDestroy {
  private readonly mediaService = inject(MediaService);
  private readonly hostEl = inject(ElementRef<HTMLElement>);
  private readonly ngZone = inject(NgZone);

  @ViewChild(GalleryVirtualScrollDirective) private scrollStrategy?: GalleryVirtualScrollDirective;

  protected readonly dateGroups = signal<DateGroup[]>([]);
  protected readonly rows = signal<VirtualRow[]>([]);
  protected readonly loadedItems = signal<Map<number, MediaItem>>(new Map());
  protected readonly columns = signal(this.loadStoredColumns());
  protected readonly containerWidth = signal(0);
  protected readonly rowHeightPx = computed(() => {
    const width = this.containerWidth();
    const cols = this.columns();
    if (width <= 0 || cols <= 0) {
      return 100;
    }
    return (width - GRID_GAP_PX * (cols - 1)) / cols + GRID_GAP_PX;
  });

  protected readonly headerHeightPx = HEADER_ROW_HEIGHT_PX;
  // Snapshot des hauteurs effectivement poussées à la stratégie de scroll — le template lit
  // CE tableau (pas rowHeightPx() recalculé en direct) pour rester en permanence synchronisé
  // avec les offsets cumulés de la stratégie, même quand containerWidth change entre-temps.
  protected readonly rowHeights = signal<number[]>([]);
  protected readonly columnOptions = [1, 2, 3, 4];

  protected readonly selectionMode = signal(false);
  protected readonly selectedIds = signal<Set<number>>(new Set());
  protected readonly showAddToAlbumSheet = signal(false);
  protected readonly rejecting = signal(false);
  protected readonly selectedFileIdsArray = computed(() => [...this.selectedIds()]);

  private readonly sortedLoadedEntries = computed(() =>
    [...this.loadedItems().entries()].sort((a, b) => a[0] - b[0]),
  );
  protected readonly viewerItems = computed(() =>
    this.sortedLoadedEntries().map(([, item]) => ({ fileId: item.pCloudFileId, mediaType: item.mediaType })),
  );
  protected readonly viewerIndex = signal<number | null>(null);

  protected readonly dataSource = new GalleryDataSource(
    [],
    PAGE_SIZE,
    (page) => this.mediaService.source(page, PAGE_SIZE, this.currentFilters()),
    (page, items) => this.onPageLoaded(page, items),
  );

  protected readonly currentRowIndex = signal(0);

  protected readonly searchText = signal('');
  protected readonly mediaTypeFilter = signal<'' | 'image' | 'video'>('');
  protected readonly minSizeFilter = signal<number | undefined>(undefined);

  private searchDebounceTimer?: ReturnType<typeof setTimeout>;

  private resizeObserver?: ResizeObserver;
  private scrolledIndexSub?: Subscription;
  private currentTopDate: string | null = null;

  ngOnInit(): void {
    this.mediaService.dateGroups(this.currentFilters()).subscribe((groups) => {
      this.dateGroups.set(groups);
      this.recomputeRows();
    });
  }

  ngAfterViewInit(): void {
    this.scrolledIndexSub = this.scrollStrategy?.scrolledIndexChange.subscribe((index) => {
      this.currentRowIndex.set(index);
      const row = this.rows()[index];
      if (row) {
        this.currentTopDate = row.date;
      }
    });

    // Mesure synchrone initiale : ne pas dépendre uniquement de ResizeObserver, dont le
    // premier déclenchement n'est pas garanti immédiat (et s'exécute hors zone Angular —
    // ngZone.run nécessaire pour que la vue reflète le nouveau signal).
    const initialWidth = this.hostEl.nativeElement.getBoundingClientRect().width;
    if (initialWidth > 0) {
      this.containerWidth.set(initialWidth);
      this.pushHeightsToStrategy();
    }

    this.resizeObserver = new ResizeObserver((entries) => {
      const width = entries[0]?.contentRect.width ?? 0;
      if (width > 0 && width !== this.containerWidth()) {
        this.ngZone.run(() => {
          this.containerWidth.set(width);
          this.pushHeightsToStrategy();
        });
      }
    });
    this.resizeObserver.observe(this.hostEl.nativeElement);
  }

  ngOnDestroy(): void {
    this.resizeObserver?.disconnect();
    this.scrolledIndexSub?.unsubscribe();
    clearTimeout(this.searchDebounceTimer);
  }

  setColumns(count: number): void {
    this.columns.set(count);
    localStorage.setItem(COLUMNS_STORAGE_KEY, String(count));
    this.recomputeRows();
  }

  private loadStoredColumns(): number {
    const stored = Number(localStorage.getItem(COLUMNS_STORAGE_KEY));
    return stored >= 1 && stored <= 4 ? stored : 3;
  }

  // Reconstruit entièrement la structure de rangées (date-groups et/ou colonnes ont changé) —
  // recale la table de tailles et resynchronise le scroll sur la date visible avant le changement.
  private recomputeRows(): void {
    const targetDate = this.currentTopDate;
    const newRows = buildRows(this.dateGroups(), this.columns());
    this.rows.set(newRows);
    this.dataSource.setRows(newRows);
    this.pushHeightsToStrategy();

    if (targetDate) {
      const index = newRows.findIndex((r) => r.date === targetDate);
      if (index >= 0) {
        setTimeout(() => this.scrollStrategy?.scrollToIndex(index, 'auto'));
      }
    }
  }

  // Recalcule uniquement les hauteurs (largeur de conteneur changée, structure inchangée).
  private pushHeightsToStrategy(): void {
    const heights = this.rows().map((r) => (r.type === 'header' ? this.headerHeightPx : this.rowHeightPx()));
    this.rowHeights.set(heights);
    this.scrollStrategy?.updateRowHeights(heights);
  }

  private onPageLoaded(page: number, items: MediaItem[]): void {
    this.loadedItems.update((current) => {
      const next = new Map(current);
      items.forEach((item, i) => next.set((page - 1) * PAGE_SIZE + i, item));
      return next;
    });
  }

  formatDateLabel(isoDate: string): string {
    return formatDateLabel(isoDate);
  }

  thumbnailUrl(fileId: number): string {
    return this.mediaService.thumbnailUrl(fileId);
  }

  onScrubberIndex(index: number): void {
    this.scrollStrategy?.scrollToIndex(index, 'auto');
  }

  onSearchInput(value: string): void {
    this.searchText.set(value);
    clearTimeout(this.searchDebounceTimer);
    this.searchDebounceTimer = setTimeout(() => this.reloadFromScratch(), 300);
  }

  setMediaTypeFilter(value: '' | 'image' | 'video'): void {
    this.mediaTypeFilter.set(value);
    this.reloadFromScratch();
  }

  setMinSizeFilter(value: string): void {
    this.minSizeFilter.set(value ? Number(value) : undefined);
    this.reloadFromScratch();
  }

  private currentFilters(): MediaFilters {
    const filters: MediaFilters = {};
    const search = this.searchText().trim();
    if (search) {
      filters.search = search;
    }
    if (this.mediaTypeFilter()) {
      filters.mediaType = this.mediaTypeFilter() as 'image' | 'video';
    }
    if (this.minSizeFilter() !== undefined) {
      filters.minSize = this.minSizeFilter();
    }
    return filters;
  }

  enterSelectionMode(): void {
    this.selectionMode.set(true);
  }

  cancelSelection(): void {
    this.selectionMode.set(false);
    this.selectedIds.set(new Set());
  }

  onTileClick(media: MediaItem, flatIndex: number): void {
    if (this.selectionMode()) {
      this.toggleSelect(media);
      return;
    }

    const viewerIndex = this.sortedLoadedEntries().findIndex(([key]) => key === flatIndex);
    if (viewerIndex >= 0) {
      this.viewerIndex.set(viewerIndex);
    }
  }

  posterUrlFn = (fileId: number): string => this.mediaService.thumbnailUrl(fileId);
  imageUrlFn = (fileId: number): string => this.mediaService.thumbnailUrl(fileId, 1600, false);
  streamUrlFn = (fileId: number): string => this.mediaService.streamUrl(fileId);

  onLongPress(media: MediaItem): void {
    if (!this.selectionMode()) {
      this.enterSelectionMode();
    }
    this.toggleSelect(media);
  }

  private toggleSelect(media: MediaItem): void {
    this.selectedIds.update((current) => {
      const next = new Set(current);
      if (next.has(media.pCloudFileId)) {
        next.delete(media.pCloudFileId);
      } else {
        next.add(media.pCloudFileId);
      }
      return next;
    });
  }

  isSelected(media: MediaItem): boolean {
    return this.selectedIds().has(media.pCloudFileId);
  }

  reject(): void {
    const fileIds = [...this.selectedIds()];
    if (fileIds.length === 0) {
      return;
    }

    this.rejecting.set(true);
    this.mediaService.reject(fileIds).subscribe({
      next: () => {
        this.rejecting.set(false);
        this.cancelSelection();
        this.reloadFromScratch();
      },
      error: () => this.rejecting.set(false),
    });
  }

  // Toute action changeant l'ensemble/l'ordre des médias (filtre, recherche, rejet en masse)
  // décale les index à plat sous-jacents — on ne peut pas corriger le cache en place sans
  // risquer un décalage silencieux : rechargement complet depuis date-groups.
  private reloadFromScratch(): void {
    this.dataSource.invalidateMediaCache();
    this.loadedItems.set(new Map());
    this.currentTopDate = null;
    this.mediaService.dateGroups(this.currentFilters()).subscribe((groups) => {
      this.dateGroups.set(groups);
      this.recomputeRows();
      this.scrollStrategy?.scrollToIndex(0, 'auto');
    });
  }

  openAddToAlbum(): void {
    if (this.selectedIds().size === 0) {
      return;
    }
    this.showAddToAlbumSheet.set(true);
  }

  onSheetClosed(): void {
    this.showAddToAlbumSheet.set(false);
    this.cancelSelection();
  }
}

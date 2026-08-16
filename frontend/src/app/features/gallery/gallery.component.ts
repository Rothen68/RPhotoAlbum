import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { MediaItem, MediaService } from '../../core/media/media.service';
import { AddToAlbumSheetComponent } from '../../shared/add-to-album-sheet/add-to-album-sheet.component';
import { LongPressDirective } from '../../shared/long-press.directive';
import { MediaViewerComponent } from '../../shared/media-viewer/media-viewer.component';

const PAGE_SIZE = 60;
const COLUMNS_STORAGE_KEY = 'rphotoalbum.gallery.columns';

@Component({
  selector: 'app-gallery',
  standalone: true,
  imports: [AddToAlbumSheetComponent, MediaViewerComponent, LongPressDirective],
  templateUrl: './gallery.component.html',
  styleUrl: './gallery.component.scss',
})
export class GalleryComponent implements OnInit {
  private readonly mediaService = inject(MediaService);

  protected readonly items = signal<MediaItem[]>([]);
  protected readonly loading = signal(false);
  protected readonly hasMore = signal(true);
  protected readonly columns = signal(this.loadStoredColumns());

  protected readonly selectionMode = signal(false);
  protected readonly selectedIds = signal<Set<number>>(new Set());
  protected readonly showAddToAlbumSheet = signal(false);
  protected readonly rejecting = signal(false);
  protected readonly selectedFileIdsArray = computed(() => [...this.selectedIds()]);
  protected readonly viewerItems = computed(() =>
    this.items().map((item) => ({ fileId: item.pCloudFileId, mediaType: item.mediaType })),
  );
  protected readonly viewerIndex = signal<number | null>(null);

  private page = 0;

  ngOnInit(): void {
    this.loadNextPage();
  }

  protected readonly columnOptions = [1, 2, 3, 4];

  setColumns(count: number): void {
    this.columns.set(count);
    localStorage.setItem(COLUMNS_STORAGE_KEY, String(count));
  }

  private loadStoredColumns(): number {
    const stored = Number(localStorage.getItem(COLUMNS_STORAGE_KEY));
    return stored >= 1 && stored <= 4 ? stored : 3;
  }

  loadNextPage(): void {
    if (this.loading() || !this.hasMore()) {
      return;
    }

    this.loading.set(true);
    const nextPage = this.page + 1;
    this.mediaService.source(nextPage, PAGE_SIZE).subscribe({
      next: (result) => {
        this.items.update((current) => [...current, ...result.items]);
        this.page = nextPage;
        this.hasMore.set(this.page * PAGE_SIZE < result.total);
        this.loading.set(false);
      },
      error: () => this.loading.set(false),
    });
  }

  thumbnailUrl(fileId: number): string {
    return this.mediaService.thumbnailUrl(fileId);
  }

  enterSelectionMode(): void {
    this.selectionMode.set(true);
  }

  cancelSelection(): void {
    this.selectionMode.set(false);
    this.selectedIds.set(new Set());
  }

  onTileClick(item: MediaItem): void {
    if (this.selectionMode()) {
      this.toggleSelect(item);
      return;
    }

    const index = this.items().findIndex((i) => i.pCloudFileId === item.pCloudFileId);
    if (index >= 0) {
      this.viewerIndex.set(index);
    }
  }

  posterUrlFn = (fileId: number): string => this.mediaService.thumbnailUrl(fileId);
  imageUrlFn = (fileId: number): string => this.mediaService.thumbnailUrl(fileId, 1600, false);
  streamUrlFn = (fileId: number): string => this.mediaService.streamUrl(fileId);

  onLongPress(item: MediaItem): void {
    if (!this.selectionMode()) {
      this.enterSelectionMode();
    }
    this.toggleSelect(item);
  }

  private toggleSelect(item: MediaItem): void {
    this.selectedIds.update((current) => {
      const next = new Set(current);
      if (next.has(item.pCloudFileId)) {
        next.delete(item.pCloudFileId);
      } else {
        next.add(item.pCloudFileId);
      }
      return next;
    });
  }

  isSelected(item: MediaItem): boolean {
    return this.selectedIds().has(item.pCloudFileId);
  }

  reject(): void {
    const fileIds = [...this.selectedIds()];
    if (fileIds.length === 0) {
      return;
    }

    this.rejecting.set(true);
    this.mediaService.reject(fileIds).subscribe({
      next: () => {
        this.items.update((current) => current.filter((i) => !fileIds.includes(i.pCloudFileId)));
        this.rejecting.set(false);
        this.cancelSelection();
      },
      error: () => this.rejecting.set(false),
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

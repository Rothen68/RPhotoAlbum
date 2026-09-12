import { CdkDragDrop, DragDropModule, moveItemInArray, transferArrayItem } from '@angular/cdk/drag-drop';
import { CommonModule } from '@angular/common';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { AlbumSection, AlbumService, AlbumSummary } from '../../core/albums/album.service';
import { ConnectivityService } from '../../core/offline/connectivity.service';
import { OfflineAlbumMeta, OfflineAlbumService } from '../../core/offline/offline-album.service';
import { OfflineModeService } from '../../core/offline/offline-mode.service';

// navigator.onLine can be wrong or slow to update (observed in real usage: a
// request left stuck pending indefinitely after switching to airplane mode rather
// than failing cleanly) — an explicit timeout guarantees a fallback to offline albums (#29)
// even if connectivity detection doesn't help.
const REQUEST_TIMEOUT_MS = 6000;

const COLLAPSED_STORAGE_KEY = 'rphotoalbum:collapsedSections';
const UNSECTIONED_ID = 'unsectioned';

// Local state for this device (not synced to pCloud) — see issue #6: collapsing a section
// is a purely visual fold, not business data.
function loadCollapsedSectionIds(): Set<string> {
  try {
    const raw = localStorage.getItem(COLLAPSED_STORAGE_KEY);
    return raw ? new Set(JSON.parse(raw)) : new Set();
  } catch {
    return new Set();
  }
}

function saveCollapsedSectionIds(ids: Set<string>): void {
  localStorage.setItem(COLLAPSED_STORAGE_KEY, JSON.stringify([...ids]));
}

@Component({
  selector: 'app-albums',
  standalone: true,
  imports: [RouterLink, FormsModule, CommonModule, DragDropModule],
  templateUrl: './albums.component.html',
  styleUrl: './albums.component.scss',
})
export class AlbumsComponent implements OnInit {
  private readonly albumService = inject(AlbumService);
  private readonly offlineAlbumService = inject(OfflineAlbumService);
  private readonly connectivity = inject(ConnectivityService);
  private readonly offlineMode = inject(OfflineModeService);

  protected readonly sections = signal<AlbumSection[]>([]);
  protected readonly unsectioned = signal<AlbumSummary[]>([]);
  protected readonly loading = signal(true);
  // Offline fallback (issue #29): /api/albums returns sections/order, never cached
  // (specific to server structure) — on network failure, we fall back to the plain list of
  // albums made available offline (see OfflineAlbumService.listOffline), so that an
  // already-downloaded album stays at least reachable and clickable.
  protected readonly offlineFallbackAlbums = signal<{ id: string; meta: OfflineAlbumMeta }[]>([]);
  protected readonly loadFailed = signal(false);

  protected readonly organizeMode = signal(false);
  protected readonly collapsedSectionIds = signal<Set<string>>(loadCollapsedSectionIds());
  protected readonly moveMenuForAlbumId = signal<string | null>(null);

  protected readonly allListIds = computed(() => [UNSECTIONED_ID, ...this.sections().map((s) => s.id)]);

  protected readonly showNewAlbumDialog = signal(false);
  protected readonly newAlbumName = signal('');
  protected readonly creating = signal(false);

  protected readonly albumPendingDelete = signal<AlbumSummary | null>(null);
  protected readonly deleting = signal(false);

  protected readonly showNewSectionDialog = signal(false);
  protected readonly newSectionName = signal('');

  protected readonly editingSectionId = signal<string | null>(null);
  protected readonly editingSectionName = signal('');

  protected readonly sectionPendingDelete = signal<AlbumSection | null>(null);

  ngOnInit(): void {
    this.load();
  }

  private load(): void {
    this.loading.set(true);
    this.loadFailed.set(false);

    // Offline mode forced by the user, or already known to be offline: no point waiting for
    // the failure (sometimes slow) of a network request bound to fail — fall back directly to
    // offline-available albums (issue #29, same reason as AlbumDetailComponent.load()).
    if (this.offlineMode.manualOfflineMode() || !this.connectivity.online()) {
      this.applyOfflineFallback();
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
      this.applyOfflineFallback();
    }, REQUEST_TIMEOUT_MS);

    this.albumService.list().subscribe({
      next: (result) => {
        if (settled) {
          return;
        }
        settled = true;
        clearTimeout(fallbackTimer);
        this.sections.set(result.sections);
        this.unsectioned.set(result.unsectioned);
        this.loading.set(false);
      },
      error: () => {
        if (settled) {
          return;
        }
        settled = true;
        clearTimeout(fallbackTimer);
        this.offlineMode.markUnreachable();
        this.applyOfflineFallback();
      },
    });
  }

  private applyOfflineFallback(): void {
    this.loadFailed.set(true);
    this.offlineFallbackAlbums.set(this.offlineAlbumService.listOffline());
    this.loading.set(false);
  }

  thumbnailUrl(fileId: number): string {
    return this.albumService.thumbnailUrl(fileId);
  }

  isOfflineAvailable(albumId: string): boolean {
    return this.offlineAlbumService.isOffline(albumId);
  }

  toggleOrganizeMode(): void {
    this.organizeMode.update((v) => !v);
    this.moveMenuForAlbumId.set(null);
  }

  // --- New album ---

  openNewAlbumDialog(): void {
    this.newAlbumName.set('');
    this.showNewAlbumDialog.set(true);
  }

  closeNewAlbumDialog(): void {
    this.showNewAlbumDialog.set(false);
  }

  createAlbum(): void {
    const name = this.newAlbumName().trim();
    if (!name || this.creating()) {
      return;
    }

    this.creating.set(true);
    this.albumService.create(name).subscribe({
      // A new album automatically appears in "unsectioned" on the server side (see
      // AlbumService.ListGroupedAsync) — a simple reload is enough.
      next: () => {
        this.creating.set(false);
        this.showNewAlbumDialog.set(false);
        this.load();
      },
      error: () => this.creating.set(false),
    });
  }

  confirmDelete(album: AlbumSummary, event: Event): void {
    event.stopPropagation();
    event.preventDefault();
    this.albumPendingDelete.set(album);
  }

  cancelDelete(): void {
    this.albumPendingDelete.set(null);
  }

  deleteAlbum(): void {
    const album = this.albumPendingDelete();
    if (!album || this.deleting()) {
      return;
    }

    this.deleting.set(true);
    this.albumService.delete(album.id).subscribe({
      next: () => {
        this.deleting.set(false);
        this.albumPendingDelete.set(null);
        this.removeAlbumLocally(album.id);
      },
      error: () => this.deleting.set(false),
    });
  }

  private removeAlbumLocally(albumId: string): void {
    this.unsectioned.update((list) => list.filter((a) => a.id !== albumId));
    this.sections.update((list) => list.map((s) => ({ ...s, albums: s.albums.filter((a) => a.id !== albumId) })));
  }

  // --- Sections: collapse/expand (local, not persisted) ---

  isCollapsed(sectionId: string): boolean {
    return this.collapsedSectionIds().has(sectionId);
  }

  toggleSection(sectionId: string): void {
    this.collapsedSectionIds.update((current) => {
      const next = new Set(current);
      if (next.has(sectionId)) {
        next.delete(sectionId);
      } else {
        next.add(sectionId);
      }
      saveCollapsedSectionIds(next);
      return next;
    });
  }

  // --- Sections: create / rename / delete ---

  openNewSectionDialog(): void {
    this.newSectionName.set('');
    this.showNewSectionDialog.set(true);
  }

  closeNewSectionDialog(): void {
    this.showNewSectionDialog.set(false);
  }

  createSection(): void {
    const name = this.newSectionName().trim();
    if (!name) {
      return;
    }

    this.showNewSectionDialog.set(false);
    // Temporary id, replaced by the definitive id returned by the server after persistStructure()
    // (see AlbumService.SaveStructureAsync on the backend side, which generates the real id).
    this.sections.update((list) => [...list, { id: `tmp_${Date.now()}`, name, albums: [] }]);
    this.persistStructure();
  }

  startRenameSection(section: AlbumSection, event: Event): void {
    event.stopPropagation();
    this.editingSectionId.set(section.id);
    this.editingSectionName.set(section.name);
  }

  commitRenameSection(section: AlbumSection): void {
    const name = this.editingSectionName().trim();
    this.editingSectionId.set(null);
    if (!name || name === section.name) {
      return;
    }

    this.sections.update((list) => list.map((s) => (s.id === section.id ? { ...s, name } : s)));
    this.persistStructure();
  }

  confirmDeleteSection(section: AlbumSection, event: Event): void {
    event.stopPropagation();
    this.sectionPendingDelete.set(section);
  }

  cancelDeleteSection(): void {
    this.sectionPendingDelete.set(null);
  }

  deleteSection(): void {
    const section = this.sectionPendingDelete();
    if (!section) {
      return;
    }

    this.sectionPendingDelete.set(null);
    this.unsectioned.update((list) => [...list, ...section.albums]);
    this.sections.update((list) => list.filter((s) => s.id !== section.id));
    this.collapsedSectionIds.update((current) => {
      const next = new Set(current);
      next.delete(section.id);
      saveCollapsedSectionIds(next);
      return next;
    });
    this.persistStructure();
  }

  onSectionsDropped(event: CdkDragDrop<AlbumSection[]>): void {
    if (event.previousIndex === event.currentIndex) {
      return;
    }

    const reordered = [...this.sections()];
    moveItemInArray(reordered, event.previousIndex, event.currentIndex);
    this.sections.set(reordered);
    this.persistStructure();
  }

  // --- Albums: moving / reordering ---

  albumsOf(containerId: string): AlbumSummary[] {
    return containerId === UNSECTIONED_ID
      ? this.unsectioned()
      : (this.sections().find((s) => s.id === containerId)?.albums ?? []);
  }

  private setAlbumsOf(containerId: string, albums: AlbumSummary[]): void {
    if (containerId === UNSECTIONED_ID) {
      this.unsectioned.set(albums);
    } else {
      this.sections.update((list) => list.map((s) => (s.id === containerId ? { ...s, albums } : s)));
    }
  }

  moveAlbumUp(containerId: string, index: number): void {
    this.reorderWithinContainer(containerId, index, index - 1);
  }

  moveAlbumDown(containerId: string, index: number): void {
    this.reorderWithinContainer(containerId, index, index + 1);
  }

  private reorderWithinContainer(containerId: string, from: number, to: number): void {
    const list = this.albumsOf(containerId);
    if (to < 0 || to >= list.length) {
      return;
    }

    const reordered = [...list];
    moveItemInArray(reordered, from, to);
    this.setAlbumsOf(containerId, reordered);
    this.persistStructure();
  }

  onAlbumDropped(event: CdkDragDrop<AlbumSummary[]>): void {
    const fromId = event.previousContainer.id;
    const toId = event.container.id;

    if (fromId === toId) {
      if (event.previousIndex === event.currentIndex) {
        return;
      }
      const list = [...this.albumsOf(fromId)];
      moveItemInArray(list, event.previousIndex, event.currentIndex);
      this.setAlbumsOf(fromId, list);
    } else {
      const fromList = [...this.albumsOf(fromId)];
      const toList = [...this.albumsOf(toId)];
      transferArrayItem(fromList, toList, event.previousIndex, event.currentIndex);
      this.setAlbumsOf(fromId, fromList);
      this.setAlbumsOf(toId, toList);
    }

    this.persistStructure();
  }

  // Do NOT call event.stopPropagation() here: the album card is an <a routerLink>, and it's
  // the (click) handler placed on .organize-controls (in the template) that prevents
  // navigation via preventDefault() — a stopPropagation() placed further down the tree (on this
  // button) would prevent the event from reaching that parent handler, letting the anchor's
  // native navigation fire anyway (observed bug: the click opened the album instead of
  // the "Move to…" menu).
  toggleMoveMenu(albumId: string): void {
    this.moveMenuForAlbumId.update((current) => (current === albumId ? null : albumId));
  }

  moveTargetsFor(containerId: string): { id: string; label: string }[] {
    const targets: { id: string; label: string }[] = [];
    if (containerId !== UNSECTIONED_ID) {
      targets.push({ id: UNSECTIONED_ID, label: 'Non rangés' });
    }
    for (const s of this.sections()) {
      if (s.id !== containerId) {
        targets.push({ id: s.id, label: s.name });
      }
    }
    return targets;
  }

  moveAlbumTo(albumId: string, fromContainerId: string, toContainerId: string): void {
    this.moveMenuForAlbumId.set(null);
    if (fromContainerId === toContainerId) {
      return;
    }

    const fromList = [...this.albumsOf(fromContainerId)];
    const idx = fromList.findIndex((a) => a.id === albumId);
    if (idx < 0) {
      return;
    }

    const [album] = fromList.splice(idx, 1);
    const toList = [...this.albumsOf(toContainerId), album];
    this.setAlbumsOf(fromContainerId, fromList);
    this.setAlbumsOf(toContainerId, toList);
    this.persistStructure();
  }

  // Replaces the entire structure server-side then adopts the response (definitive
  // section ids, unknown album ids already filtered out) — same philosophy as the rest of the app:
  // the document persisted on pCloud is authoritative, not the optimistic local state.
  private persistStructure(): void {
    const sectionsPayload = this.sections().map((s) => ({
      id: s.id.startsWith('tmp_') ? null : s.id,
      name: s.name,
      albumIds: s.albums.map((a) => a.id),
    }));
    const unsectionedIds = this.unsectioned().map((a) => a.id);

    this.albumService.saveStructure(sectionsPayload, unsectionedIds).subscribe({
      next: (result) => {
        this.sections.set(result.sections);
        this.unsectioned.set(result.unsectioned);
      },
      error: () => this.load(),
    });
  }
}

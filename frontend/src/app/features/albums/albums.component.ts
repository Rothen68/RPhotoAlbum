import { CdkDragDrop, DragDropModule, moveItemInArray, transferArrayItem } from '@angular/cdk/drag-drop';
import { CommonModule } from '@angular/common';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { timeout } from 'rxjs';
import { AlbumSection, AlbumService, AlbumSummary } from '../../core/albums/album.service';
import { ConnectivityService } from '../../core/offline/connectivity.service';
import { OfflineAlbumMeta, OfflineAlbumService } from '../../core/offline/offline-album.service';

// navigator.onLine peut se tromper ou tarder à se mettre à jour (constaté en usage réel : une
// requête restée bloquée en attente indéfiniment après le passage en mode avion plutôt que
// d'échouer proprement) — un délai explicite garantit un repli sur les albums hors-ligne (#29)
// même si la détection de connectivité n'aide pas.
const REQUEST_TIMEOUT_MS = 6000;

const COLLAPSED_STORAGE_KEY = 'rphotoalbum:collapsedSections';
const UNSECTIONED_ID = 'unsectioned';

// État local à cet appareil (pas synchronisé sur pCloud) — voir issue #6 : replier une section
// est un simple repli visuel, pas une donnée métier.
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

  protected readonly sections = signal<AlbumSection[]>([]);
  protected readonly unsectioned = signal<AlbumSummary[]>([]);
  protected readonly loading = signal(true);
  // Repli hors-ligne (issue #29) : /api/albums renvoie sections/ordre, jamais mis en cache
  // (propre à la structure serveur) — sur échec réseau, on retombe sur la simple liste des
  // albums rendus disponibles hors-ligne (voir OfflineAlbumService.listOffline), pour qu'un
  // album déjà téléchargé reste au moins atteignable et cliquable.
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

    // Déjà su hors-ligne : inutile d'attendre l'échec (parfois lent) d'une requête réseau vouée
    // à échouer — repli direct sur les albums disponibles hors-ligne (issue #29, même raison
    // que AlbumDetailComponent.load()).
    if (!this.connectivity.online()) {
      this.loadFailed.set(true);
      this.offlineFallbackAlbums.set(this.offlineAlbumService.listOffline());
      this.loading.set(false);
      return;
    }

    this.albumService.list().pipe(timeout(REQUEST_TIMEOUT_MS)).subscribe({
      next: (result) => {
        this.sections.set(result.sections);
        this.unsectioned.set(result.unsectioned);
        this.loading.set(false);
      },
      error: () => {
        this.loadFailed.set(true);
        this.offlineFallbackAlbums.set(this.offlineAlbumService.listOffline());
        this.loading.set(false);
      },
    });
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

  // --- Nouvel album ---

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
      // Un nouvel album apparaît automatiquement en "non rangés" côté serveur (voir
      // AlbumService.ListGroupedAsync) — un simple rechargement suffit.
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

  // --- Sections : repli/dépli (local, non persisté) ---

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

  // --- Sections : création / renommage / suppression ---

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
    // Id temporaire, remplacé par l'id définitif renvoyé par le serveur après persistStructure()
    // (voir AlbumService.SaveStructureAsync côté backend, qui génère l'id réel).
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

  // --- Albums : déplacement / réorganisation ---

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

  // Ne PAS appeler event.stopPropagation() ici : la carte album est un <a routerLink>, et c'est
  // le gestionnaire (click) posé sur .organize-controls (dans le template) qui empêche la
  // navigation via preventDefault() — un stopPropagation() posé plus bas dans l'arbre (sur ce
  // bouton) empêcherait l'événement d'atteindre ce gestionnaire parent, laissant la navigation
  // native de l'ancre se déclencher malgré tout (bug constaté : le clic ouvrait l'album au lieu
  // du menu "Déplacer vers…").
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

  // Remplace l'intégralité de la structure côté serveur puis adopte la réponse (ids de section
  // définitifs, ids d'album inconnus déjà filtrés) — même philosophie que le reste de l'app :
  // le document persisté sur pCloud fait autorité, pas l'état local optimiste.
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

import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { CdkDragDrop, CdkDragMove, DragDropModule, moveItemInArray } from '@angular/cdk/drag-drop';
import { ActivatedRoute, Router } from '@angular/router';
import { AlbumDetail, AlbumItem, AlbumService } from '../../core/albums/album.service';
import { LongPressDirective } from '../../shared/long-press.directive';
import { MarkdownEditorComponent } from '../../shared/markdown-editor/markdown-editor.component';
import { MarkdownPipe } from '../../shared/markdown.pipe';
import { MediaViewerComponent } from '../../shared/media-viewer/media-viewer.component';
import { AlbumRow, groupIntoRows } from './album-layout';

// CDK n'auto-scrolle de façon fiable que les conteneurs explicitement scrollables
// (overflow: auto/scroll) — pas le scroll naturel de la page/fenêtre utilisé ici,
// constaté en test réel (PC et mobile) : impossible de sortir un item de la zone
// visible pendant un glisser. Implémentation manuelle du scroll auto près des bords.
const AUTO_SCROLL_EDGE_PX = 80;
const AUTO_SCROLL_MAX_SPEED = 18;

@Component({
  selector: 'app-album-detail',
  standalone: true,
  imports: [DragDropModule, LongPressDirective, MarkdownEditorComponent, MarkdownPipe, MediaViewerComponent],
  templateUrl: './album-detail.component.html',
  styleUrl: './album-detail.component.scss',
  host: { class: 'page' },
})
export class AlbumDetailComponent implements OnInit {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly albumService = inject(AlbumService);

  private albumId!: string;

  protected readonly album = signal<AlbumDetail | null>(null);
  protected readonly loading = signal(true);
  // Mode unique regroupant édition de texte, ajout/suppression et réorganisation des
  // médias — la vue de base reste purement dédiée à la consultation (voir retour
  // utilisateur : avoir un mode "Reorder" séparé du texte éditable en permanence
  // en vue normale était source de confusion).
  protected readonly editMode = signal(false);

  protected readonly insertingAt = signal<string | null | undefined>(undefined);
  protected readonly editingItemId = signal<string | null>(null);
  protected draftText = '';

  protected readonly rows = computed(() => groupIntoRows(this.album()?.items ?? []));

  // Multi-sélection en mode Edit — mêmes signaux/pattern que Gallery. Seul usage pour
  // l'instant : déplacer tout un bloc sélectionné d'un coup au drag (voir onCdkDrop),
  // pas d'action groupée dédiée (suppression/etc. restent par item, via le bouton "x").
  protected readonly selectedIds = signal<Set<string>>(new Set());
  protected readonly viewerIndex = signal<number | null>(null);

  protected readonly mediaItems = computed(() => (this.album()?.items ?? []).filter((i) => i.type === 'media'));
  protected readonly viewerItems = computed(() =>
    this.mediaItems().map((i) => ({ fileId: i.albumCopy!.fileId, mediaType: i.mediaType as 'image' | 'video' })),
  );

  posterUrlFn = (fileId: number): string => this.albumService.thumbnailUrl(fileId, 400);
  imageUrlFn = (fileId: number): string => this.albumService.thumbnailUrl(fileId, 1600, false);
  streamUrlFn = (fileId: number): string => this.albumService.streamUrl(fileId);

  private autoScrollSpeed = 0;
  private autoScrollFrame: number | null = null;

  ngOnInit(): void {
    this.albumId = this.route.snapshot.paramMap.get('id')!;
    this.load();
  }

  private load(): void {
    this.loading.set(true);
    this.albumService.get(this.albumId).subscribe({
      next: (album) => {
        this.album.set(album);
        this.loading.set(false);
      },
      error: () => this.loading.set(false),
    });
  }

  thumbnailUrl(fileId: number): string {
    return this.albumService.thumbnailUrl(fileId, 800);
  }

  streamUrl(fileId: number): string {
    return this.albumService.streamUrl(fileId);
  }

  toggleEdit(): void {
    this.editMode.update((v) => !v);
    this.insertingAt.set(undefined);
    this.editingItemId.set(null);
    this.selectedIds.set(new Set());
  }

  // --- Visionneuse + sélection (§11.8) ---

  onMediaClick(item: AlbumItem): void {
    if (this.editMode()) {
      this.toggleSelect(item.id);
      return;
    }

    // Les vidéos gardent leur lecture inline (<video controls>, comportement existant) en vue
    // de base — ouvrir la visionneuse plein écran par-dessus gênerait plus qu'autre chose vu
    // qu'on peut déjà les lire directement dans le fil. Utile surtout pour les photos, dont
    // la miniature du fil est petite.
    if (item.mediaType === 'video') {
      return;
    }

    const idx = this.mediaItems().findIndex((i) => i.id === item.id);
    if (idx >= 0) {
      this.viewerIndex.set(idx);
    }
  }

  // Depuis la vue de base : bascule en mode Edit avec ce média présélectionné, pour enchaîner
  // directement sur une sélection multiple (puis un drag groupé) sans passer par "Edit" + un
  // premier tap séparé.
  onLongPress(item: AlbumItem): void {
    if (!this.editMode()) {
      this.editMode.set(true);
    }
    this.toggleSelect(item.id);
  }

  isSelected(item: AlbumItem): boolean {
    return this.selectedIds().has(item.id);
  }

  private toggleSelect(itemId: string): void {
    this.selectedIds.update((current) => {
      const next = new Set(current);
      if (next.has(itemId)) {
        next.delete(itemId);
      } else {
        next.add(itemId);
      }
      return next;
    });
  }

  clearSelection(): void {
    this.selectedIds.set(new Set());
  }

  // --- Insertion de texte en ligne (§11.5) ---

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
    // Le bloc texte reste affiché en vue de base (hors mode Edit), mais uniquement
    // pour consultation — cliquer dessus n'y ouvre pas l'édition.
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

  // --- Layout en grille (§11.7) ---

  // "Grouper avec le suivant" et "Séparer" sont deux actions indépendantes, pas les deux états
  // d'un même bouton : une rangée déjà groupée à 2 doit pouvoir grandir à 3 (canGrow) ET être
  // séparée (retour à 1) — les afficher l'un XOR l'autre empêchait de dépasser un groupe de 2.
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
    this.albumService.removeItem(this.albumId, itemId).subscribe((album) => {
      this.album.set(album);
      if (this.selectedIds().has(itemId)) {
        this.selectedIds.update((s) => {
          const next = new Set(s);
          next.delete(itemId);
          return next;
        });
      }
    });
  }

  moveUp(index: number): void {
    const items = this.album()?.items ?? [];
    if (index <= 0) {
      return;
    }
    const ids = items.map((i) => i.id);
    [ids[index - 1], ids[index]] = [ids[index], ids[index - 1]];
    this.reorderTo(ids);
  }

  moveDown(index: number): void {
    const items = this.album()?.items ?? [];
    if (index >= items.length - 1) {
      return;
    }
    const ids = items.map((i) => i.id);
    [ids[index], ids[index + 1]] = [ids[index + 1], ids[index]];
    this.reorderTo(ids);
  }

  onCdkDrop(event: CdkDragDrop<AlbumItem[]>): void {
    this.stopAutoScroll();
    if (event.previousIndex === event.currentIndex) {
      return;
    }

    const items = this.album()?.items ?? [];
    const draggedItem = event.item.data as AlbumItem | undefined;
    const selected = this.selectedIds();

    // CDK ne déplace physiquement que l'item dragué (pas de multi-drag natif) — si cet item
    // fait partie d'une sélection multiple, on réinsère TOUTE la sélection comme un bloc
    // contigu à l'endroit où il a atterri, plutôt que de ne déplacer que lui seul.
    if (draggedItem && selected.size > 1 && selected.has(draggedItem.id)) {
      this.moveSelectionTo(items, event.previousIndex, event.currentIndex, selected);
      return;
    }

    const ids = items.map((i) => i.id);
    moveItemInArray(ids, event.previousIndex, event.currentIndex);
    this.reorderTo(ids);
  }

  // Le point de dépôt (combien d'items NON sélectionnés le précèdent) est déduit d'une
  // simulation du déplacement du seul item dragué — exactement ce que CDK a fait visuellement.
  // Mais remaining/selectedBlock sont construits depuis la liste ORIGINALE (pas la simulation) :
  // si l'item dragué franchit d'autres items sélectionnés en atterrissant après eux, les
  // extraire de la simulation inverserait leur ordre relatif au lieu de le conserver.
  private moveSelectionTo(items: AlbumItem[], previousIndex: number, currentIndex: number, selected: Set<string>): void {
    const originalIds = items.map((i) => i.id);

    const afterSingleMove = [...originalIds];
    moveItemInArray(afterSingleMove, previousIndex, currentIndex);

    let nonSelectedBefore = 0;
    for (let i = 0; i < currentIndex; i++) {
      if (!selected.has(afterSingleMove[i])) {
        nonSelectedBefore++;
      }
    }

    const remaining = originalIds.filter((id) => !selected.has(id));
    const selectedBlock = originalIds.filter((id) => selected.has(id));
    const final = [...remaining.slice(0, nonSelectedBefore), ...selectedBlock, ...remaining.slice(nonSelectedBefore)];
    this.reorderTo(final);
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
      window.scrollBy(0, this.autoScrollSpeed);
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

  private reorderTo(ids: string[]): void {
    this.albumService.reorder(this.albumId, ids).subscribe((album) => this.album.set(album));
  }

  goBack(): void {
    this.router.navigateByUrl('/albums');
  }
}

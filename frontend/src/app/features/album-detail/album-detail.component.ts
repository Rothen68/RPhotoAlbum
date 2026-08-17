import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { CdkDragDrop, CdkDragMove, DragDropModule, moveItemInArray } from '@angular/cdk/drag-drop';
import { ActivatedRoute, Router } from '@angular/router';
import { AlbumDetail, AlbumItem, AlbumService } from '../../core/albums/album.service';
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
  imports: [DragDropModule, MarkdownEditorComponent, MarkdownPipe, MediaViewerComponent],
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

  protected readonly viewerIndex = signal<number | null>(null);

  protected readonly mediaItems = computed(() => (this.album()?.items ?? []).filter((i) => i.type === 'media'));
  protected readonly viewerItems = computed(() =>
    this.mediaItems().map((i) => ({ fileId: i.albumCopy!.fileId, mediaType: i.mediaType as 'image' | 'video' })),
  );

  posterUrlFn = (fileId: number): string => this.albumService.thumbnailUrl(fileId, 400);
  imageUrlFn = (fileId: number): string => this.albumService.thumbnailUrl(fileId, 1600, false);
  streamUrlFn = (fileId: number): string => this.albumService.streamUrl(fileId);
  downloadUrlFn = (fileId: number): string => this.albumService.downloadUrl(fileId);

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
  }

  // --- Visionneuse (§11.8) ---

  onMediaClick(item: AlbumItem): void {
    // Les vidéos gardent leur lecture inline (<video controls>, comportement existant) — ouvrir
    // la visionneuse plein écran par-dessus gênerait plus qu'autre chose vu qu'on peut déjà les
    // lire directement dans le fil. Utile surtout pour les photos, dont la miniature est petite.
    if (item.mediaType === 'video') {
      return;
    }

    const idx = this.mediaItems().findIndex((i) => i.id === item.id);
    if (idx >= 0) {
      this.viewerIndex.set(idx);
    }
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
    this.albumService.removeItem(this.albumId, itemId).subscribe((album) => this.album.set(album));
  }

  // Unité de réorganisation = la RANGÉE (une ligne de texte, ou un groupe d'1 à 3 photos),
  // pas l'item individuel — un seul bouton/poignée par rangée, un groupe s'y déplace comme un
  // bloc atomique sans logique de repositionnement dédiée (voir retour utilisateur : la
  // sélection multiple par item s'est avérée trop complexe pour peu de bénéfice une fois la
  // rangée déjà disponible comme unité naturelle depuis l'étape 7).
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

  // Réordonne l'état local IMMÉDIATEMENT (avant même l'appel réseau) : CDK annule son propre
  // rendu de drag (transform de prévisualisation) dès le drop, en s'attendant à ce que les
  // données sous-jacentes reflètent déjà le nouvel ordre au même tick — sans ça, l'item revient
  // un instant à sa position d'origine avant de sauter à sa position finale une fois la réponse
  // serveur arrivée (constaté par l'utilisateur, PC et mobile). L'appel serveur suit derrière
  // pour persister ; sa réponse re-synchronise l'état au cas où (rare) où le serveur aurait dû
  // ajuster quelque chose (ex. normalisation de RowSpan).
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

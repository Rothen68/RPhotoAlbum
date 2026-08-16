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

const MAX_ROW_SPAN = 3;
// Zone centrale d'une rangée cible où un dépôt fusionne (0.25–0.75 de sa hauteur) ; le reste
// (haut/bas) reste réservé à la réorganisation classique (insérer avant/après).
const MERGE_ZONE_MIN = 0.25;
const MERGE_ZONE_MAX = 0.75;

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

  // Rangée média survolée en son centre pendant un drag (candidate à la fusion) — voir
  // onDragMoved/updateMergeTarget. Identifiée par l'id de son ancre (row.items[0].id), comme
  // le `track` du template.
  protected readonly mergeTargetRowId = signal<string | null>(null);

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
    this.persist(ids, { [itemId]: span });
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
    this.persist(reordered.flatMap((r) => r.items.map((i) => i.id)));
  }

  moveRowDown(rowIndex: number): void {
    const rows = this.rows();
    if (rowIndex >= rows.length - 1) {
      return;
    }
    const reordered = [...rows];
    [reordered[rowIndex], reordered[rowIndex + 1]] = [reordered[rowIndex + 1], reordered[rowIndex]];
    this.persist(reordered.flatMap((r) => r.items.map((i) => i.id)));
  }

  onCdkDrop(event: CdkDragDrop<AlbumRow[]>): void {
    this.stopAutoScroll();
    const draggedRow = event.item.data as AlbumRow;
    const targetRowId = this.mergeTargetRowId();
    this.mergeTargetRowId.set(null);

    if (targetRowId) {
      this.mergeRows(draggedRow, targetRowId);
      return;
    }

    if (event.previousIndex === event.currentIndex) {
      return;
    }

    const rows = [...this.rows()];
    moveItemInArray(rows, event.previousIndex, event.currentIndex);
    this.persist(rows.flatMap((r) => r.items.map((i) => i.id)));
  }

  // Fusion par glisser-déposer (étape 8, partie la plus incertaine du plan — pari assumé, à
  // confirmer sur téléphone réel) : dépose une rangée média sur le CENTRE d'une autre rangée
  // média pour l'y ajouter, jusqu'à 3 photos par rangée. Insère toujours après le dernier item
  // de la cible (pas avant l'ancre) — la cible garde son ancre/rowSpan existant, seulement
  // agrandi. Le repli existe déjà nativement : hors de la zone centrale (ou sur une rangée
  // texte, ou si la limite de 3 serait dépassée), mergeTargetRowId reste nul et le drop revient
  // au réordonnancement classique de l'étape 3.
  private mergeRows(draggedRow: AlbumRow, targetRowId: string): void {
    const items = this.album()?.items ?? [];
    const targetRow = this.rows().find((r) => r.items[0].id === targetRowId);
    if (!targetRow) {
      return;
    }

    const mergedSpan = targetRow.items.length + draggedRow.items.length;
    if (mergedSpan > MAX_ROW_SPAN) {
      return;
    }

    const draggedIds = new Set(draggedRow.items.map((i) => i.id));
    const remaining = items.filter((i) => !draggedIds.has(i.id));
    const targetLastId = targetRow.items[targetRow.items.length - 1].id;
    const insertPos = remaining.findIndex((i) => i.id === targetLastId) + 1;

    const finalIds = [
      ...remaining.slice(0, insertPos).map((i) => i.id),
      ...draggedRow.items.map((i) => i.id),
      ...remaining.slice(insertPos).map((i) => i.id),
    ];

    this.persist(finalIds, { [targetRowId]: mergedSpan });
  }

  onDragMoved(event: CdkDragMove<AlbumRow>): void {
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

    this.updateMergeTarget(event);
  }

  onDragEnded(): void {
    this.stopAutoScroll();
    this.mergeTargetRowId.set(null);
  }

  // Hit-testing maison par-dessus les coordonnées pointeur — CDK ne fournit pas nativement de
  // notion de "zone de dépôt = centre vs bord". [attr.data-row-id] sur .row-wrapper (template)
  // permet de retrouver la rangée survolée sans dépendre de la structure interne des composants.
  private updateMergeTarget(event: CdkDragMove<AlbumRow>): void {
    const draggedRow = event.source.data;
    if (draggedRow.items[0].type !== 'media') {
      this.mergeTargetRowId.set(null);
      return;
    }

    const point = event.pointerPosition;
    const el = document.elementFromPoint(point.x, point.y);
    const targetWrapper = el?.closest<HTMLElement>('.row-wrapper');
    const targetRowId = targetWrapper?.dataset['rowId'];

    if (!targetRowId || targetRowId === draggedRow.items[0].id) {
      this.mergeTargetRowId.set(null);
      return;
    }

    const targetRow = this.rows().find((r) => r.items[0].id === targetRowId);
    if (!targetRow || targetRow.items[0].type !== 'media' || targetRow.items.length + draggedRow.items.length > MAX_ROW_SPAN) {
      this.mergeTargetRowId.set(null);
      return;
    }

    const rect = targetWrapper!.getBoundingClientRect();
    const relY = (point.y - rect.top) / rect.height;
    if (relY < MERGE_ZONE_MIN || relY > MERGE_ZONE_MAX) {
      this.mergeTargetRowId.set(null);
      return;
    }

    this.mergeTargetRowId.set(targetRowId);
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

  // Met à jour l'état local IMMÉDIATEMENT (avant même l'appel réseau) : CDK annule son propre
  // rendu de drag (transform de prévisualisation) dès le drop, en s'attendant à ce que les
  // données sous-jacentes reflètent déjà le nouvel état au même tick — sans ça, l'item revient
  // un instant à sa position/apparence d'origine avant de sauter à la position/apparence finale
  // une fois la réponse serveur arrivée (constaté par l'utilisateur, PC et mobile). L'appel
  // serveur suit derrière pour persister ; sa réponse re-synchronise l'état au cas où (rare) où
  // le serveur aurait dû ajuster quelque chose (ex. normalisation de RowSpan).
  private persist(ids: string[], rowSpanChanges: Record<string, number> = {}): void {
    const current = this.album();
    if (current) {
      const byId = new Map(current.items.map((i) => [i.id, i]));
      const reordered = ids
        .map((id) => byId.get(id))
        .filter((i): i is AlbumItem => !!i)
        .map((i) => (i.id in rowSpanChanges ? { ...i, rowSpan: rowSpanChanges[i.id] } : i));
      this.album.set({ ...current, items: reordered });
    }

    this.albumService.reorder(this.albumId, ids, rowSpanChanges).subscribe((album) => this.album.set(album));
  }

  goBack(): void {
    this.router.navigateByUrl('/albums');
  }
}

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
import { MarkdownEditorComponent } from '../../shared/markdown-editor/markdown-editor.component';
import { MarkdownPipe } from '../../shared/markdown.pipe';
import { MediaViewerComponent } from '../../shared/media-viewer/media-viewer.component';
import { isRawFileName } from '../../shared/raw-format';
import { AlbumRow, groupIntoRows } from './album-layout';
import { AlbumVirtualScrollDirective, computeRowHeight } from './album-virtual';

// CDK n'auto-scrolle de façon fiable que les conteneurs explicitement scrollables
// (overflow: auto/scroll) — pas le scroll naturel de la page/fenêtre utilisé ici,
// constaté en test réel (PC et mobile) : impossible de sortir un item de la zone
// visible pendant un glisser. Implémentation manuelle du scroll auto près des bords.
const AUTO_SCROLL_EDGE_PX = 80;
const AUTO_SCROLL_MAX_SPEED = 18;

@Component({
  selector: 'app-album-detail',
  standalone: true,
  imports: [DragDropModule, MarkdownEditorComponent, MarkdownPipe, MediaViewerComponent, ScrollingModule, AlbumVirtualScrollDirective],
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
  private readonly hostEl = inject(ElementRef<HTMLElement>);
  private readonly ngZone = inject(NgZone);

  // Vue de base virtualisée (issue #20) : hauteurs de rangée précalculées à partir de la seule
  // largeur du conteneur (voir album-virtual.ts). Le mode Edit, lui, reste en rendu complet non
  // virtualisé — combiner virtual-scroll et drag-and-drop par rangée (recyclage DOM pendant un
  // défilement auto-scroll déclenché par le drag) est une des combinaisons CDK les plus fragiles
  // en pratique, et un mode Edit reste une session d'action délibérée et bornée (contrairement
  // au simple défilement de consultation, bien plus fréquent) — un compromis délibéré, pas un
  // repli après échec comme la barre de date de Gallery.
  @ViewChild(AlbumVirtualScrollDirective) private scrollStrategy?: AlbumVirtualScrollDirective;
  // Conteneur scrollable du mode Edit (rendu complet, pas de viewport CDK) — l'auto-scroll
  // pendant un glisser doit défiler CE conteneur plutôt que window/document maintenant que les
  // deux modes partagent le même agencement flex borné en hauteur (voir SCSS).
  @ViewChild('editScroll') private editScrollEl?: ElementRef<HTMLElement>;
  protected readonly containerWidth = signal(0);
  protected readonly rowHeights = computed(() => this.rows().map((row) => computeRowHeight(row, this.containerWidth())));
  private resizeObserver?: ResizeObserver;

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
  protected trackRow = (_index: number, row: AlbumRow): string => row.items[0].id;

  protected readonly viewerIndex = signal<number | null>(null);

  // --- Consultation hors-ligne (issue #29) ---
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
    // Couvre le cas "les hauteurs changent pendant que le viewport est déjà attaché" (résultat
    // d'une mutation d'album). Le cas "le viewport vient d'être (re)créé" est couvert séparément
    // par pushRowHeights(), appelée sur l'événement (attached) de la directive — voir
    // AlbumVirtualScrollDirective pour la raison (l'ordre effect-vs-attach() n'est pas garanti).
    effect(() => this.pushRowHeights());

    // Bascule les miniatures affichées vers le cache hors-ligne (Object URL) dès que la
    // connectivité tombe, pour un album rendu disponible hors-ligne — voir OfflineAlbumService
    // (issue #29). Le chemin en ligne (thumbnailUrl() retombant sur l'URL réseau) est inchangé.
    effect(() => {
      const online = this.connectivity.online();
      const album = this.album();
      if (!online && album && this.offlineAlbumService.isOffline(this.albumId)) {
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

  // La stratégie de scroll (offsets cumulés) a besoin de l'empreinte TOTALE de chaque rangée
  // (contenu + marge visuelle), alors que rowHeights() — utilisée pour dimensionner .row/
  // .media-block eux-mêmes — doit rester au contenu exact, sans quoi l'image serait étirée en
  // trop. Le marge (ROW_GAP_PX) doit correspondre exactement au margin-bottom de .row-wrapper
  // en vue virtualisée (voir SCSS) : régression repérée par l'utilisateur (V2.24 déployée sans
  // aucun espacement visuel entre rangées dans la vue de base — le calcul de hauteur ne prenait
  // jusqu'ici en compte QUE le contenu, jamais d'espacement entre rangées).
  private static readonly ROW_GAP_PX = 8;

  protected pushRowHeights(): void {
    const heights = this.rowHeights().map((h) => h + AlbumDetailComponent.ROW_GAP_PX);
    this.scrollStrategy?.updateRowHeights(heights);
  }

  private load(): void {
    this.loading.set(true);
    this.albumService.get(this.albumId).subscribe({
      next: (album) => {
        this.album.set(album);
        this.loading.set(false);
      },
      // Pas de réseau (ou serveur injoignable) : si cet album a été rendu disponible hors-ligne,
      // on le recharge depuis le cache local plutôt que de simplement abandonner (issue #29).
      error: () => {
        if (!this.offlineAlbumService.isOffline(this.albumId)) {
          this.loading.set(false);
          return;
        }
        this.offlineAlbumService.getCachedAlbum(this.albumId).then((cached) => {
          if (cached) {
            this.album.set(cached);
          }
          this.loading.set(false);
        });
      },
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
      // Un rebuild plus récent a déjà démarré (ou la connectivité est repassée en ligne) — on
      // jette ce résultat obsolète plutôt que d'écraser une map plus à jour.
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

  // --- Consultation hors-ligne : actions (issue #29) ---

  makeOfflineAvailable(): void {
    const album = this.album();
    if (!album) {
      return;
    }
    this.offlineError.set(null);
    this.offlineAlbumService.makeAvailable(album).catch((err) => this.reportOfflineError(err));
  }

  // Distingue les causes d'échec (issue #29, retour utilisateur : le message générique ne
  // permettait pas de savoir laquelle s'appliquait) :
  // - contexte non sécurisé (accès en HTTP simple, pas HTTPS/localhost) — l'API Cache Storage
  //   n'existe alors tout simplement pas, message dédié plutôt que de laisser croire à un souci
  //   réseau ou stockage ;
  // - dépassement de quota — chiffres réels de l'appareil via navigator.storage.estimate() ;
  // - échec générique (réseau, ou aucune miniature récupérable même après nouvel essai).
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

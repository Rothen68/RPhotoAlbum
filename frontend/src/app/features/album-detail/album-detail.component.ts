import { Component, OnInit, inject, signal } from '@angular/core';
import { CdkDragDrop, CdkDragMove, DragDropModule, moveItemInArray } from '@angular/cdk/drag-drop';
import { ActivatedRoute, Router } from '@angular/router';
import { AlbumDetail, AlbumItem, AlbumService } from '../../core/albums/album.service';
import { MarkdownEditorComponent } from '../../shared/markdown-editor/markdown-editor.component';
import { MarkdownPipe } from '../../shared/markdown.pipe';

// CDK n'auto-scrolle de façon fiable que les conteneurs explicitement scrollables
// (overflow: auto/scroll) — pas le scroll naturel de la page/fenêtre utilisé ici,
// constaté en test réel (PC et mobile) : impossible de sortir un item de la zone
// visible pendant un glisser. Implémentation manuelle du scroll auto près des bords.
const AUTO_SCROLL_EDGE_PX = 80;
const AUTO_SCROLL_MAX_SPEED = 18;

@Component({
  selector: 'app-album-detail',
  standalone: true,
  imports: [DragDropModule, MarkdownEditorComponent, MarkdownPipe],
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

  // --- Reorder (§11.6) ---

  removeItem(itemId: string): void {
    this.albumService.removeItem(this.albumId, itemId).subscribe((album) => this.album.set(album));
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
    const ids = items.map((i) => i.id);
    moveItemInArray(ids, event.previousIndex, event.currentIndex);
    this.reorderTo(ids);
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

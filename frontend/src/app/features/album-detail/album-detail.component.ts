import { Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { AlbumDetail, AlbumItem, AlbumService } from '../../core/albums/album.service';

@Component({
  selector: 'app-album-detail',
  standalone: true,
  imports: [FormsModule],
  templateUrl: './album-detail.component.html',
  styleUrl: './album-detail.component.scss',
})
export class AlbumDetailComponent implements OnInit {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly albumService = inject(AlbumService);

  private albumId!: string;

  protected readonly album = signal<AlbumDetail | null>(null);
  protected readonly loading = signal(true);
  protected readonly reorderMode = signal(false);

  protected readonly insertingAt = signal<string | null | undefined>(undefined);
  protected readonly editingItemId = signal<string | null>(null);
  protected draftText = '';

  protected readonly draggedItemId = signal<string | null>(null);
  protected readonly dropTargetId = signal<string | null>(null);

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

  toggleReorder(): void {
    this.reorderMode.update((v) => !v);
    this.insertingAt.set(undefined);
    this.editingItemId.set(null);
  }

  // --- Insertion de texte en ligne (§11.5) ---

  startInsert(afterItemId: string | null): void {
    if (this.reorderMode()) {
      return;
    }
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
    if (this.reorderMode()) {
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

  onDragStart(item: AlbumItem): void {
    this.draggedItemId.set(item.id);
  }

  onDragOver(event: DragEvent, item: AlbumItem): void {
    event.preventDefault();
    if (this.draggedItemId() && this.draggedItemId() !== item.id) {
      this.dropTargetId.set(item.id);
    }
  }

  onDragEnd(): void {
    this.draggedItemId.set(null);
    this.dropTargetId.set(null);
  }

  onDrop(target: AlbumItem): void {
    const draggedId = this.draggedItemId();
    this.draggedItemId.set(null);
    this.dropTargetId.set(null);
    if (!draggedId || draggedId === target.id) {
      return;
    }

    const items = this.album()?.items ?? [];
    const ids = items.map((i) => i.id);
    const fromIndex = ids.indexOf(draggedId);
    if (fromIndex < 0) {
      return;
    }
    ids.splice(fromIndex, 1);
    const toIndex = ids.indexOf(target.id);
    ids.splice(toIndex, 0, draggedId);

    this.reorderTo(ids);
  }

  private reorderTo(ids: string[]): void {
    this.albumService.reorder(this.albumId, ids).subscribe((album) => this.album.set(album));
  }

  goBack(): void {
    this.router.navigateByUrl('/albums');
  }
}

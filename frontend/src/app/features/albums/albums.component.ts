import { Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { AlbumService, AlbumSummary } from '../../core/albums/album.service';

@Component({
  selector: 'app-albums',
  standalone: true,
  imports: [RouterLink, FormsModule],
  templateUrl: './albums.component.html',
  styleUrl: './albums.component.scss',
})
export class AlbumsComponent implements OnInit {
  private readonly albumService = inject(AlbumService);

  protected readonly albums = signal<AlbumSummary[]>([]);
  protected readonly loading = signal(true);

  protected readonly showNewAlbumDialog = signal(false);
  protected readonly newAlbumName = signal('');
  protected readonly creating = signal(false);

  protected readonly albumPendingDelete = signal<AlbumSummary | null>(null);
  protected readonly deleting = signal(false);

  ngOnInit(): void {
    this.load();
  }

  private load(): void {
    this.loading.set(true);
    this.albumService.list().subscribe({
      next: (albums) => {
        this.albums.set(albums);
        this.loading.set(false);
      },
      error: () => this.loading.set(false),
    });
  }

  thumbnailUrl(fileId: number): string {
    return this.albumService.thumbnailUrl(fileId);
  }

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
        this.albums.update((current) => current.filter((a) => a.id !== album.id));
      },
      error: () => this.deleting.set(false),
    });
  }
}

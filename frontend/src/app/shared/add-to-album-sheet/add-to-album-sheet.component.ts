import { Component, EventEmitter, Input, OnInit, Output, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { AlbumMembership, AlbumService } from '../../core/albums/album.service';

@Component({
  selector: 'app-add-to-album-sheet',
  standalone: true,
  imports: [FormsModule],
  templateUrl: './add-to-album-sheet.component.html',
  styleUrl: './add-to-album-sheet.component.scss',
})
export class AddToAlbumSheetComponent implements OnInit {
  private readonly albumService = inject(AlbumService);

  @Input({ required: true }) selectedFileIds!: number[];
  @Output() closed = new EventEmitter<void>();

  protected readonly memberships = signal<AlbumMembership[]>([]);
  protected readonly loading = signal(true);
  protected readonly creatingNew = signal(false);
  protected readonly busyAlbumId = signal<string | null>(null);
  protected newAlbumName = '';

  ngOnInit(): void {
    this.refresh();
  }

  private refresh(): void {
    this.loading.set(true);
    this.albumService.membership(this.selectedFileIds).subscribe({
      next: (memberships) => {
        this.memberships.set(memberships);
        this.loading.set(false);
      },
      error: () => this.loading.set(false),
    });
  }

  startNewAlbum(): void {
    this.creatingNew.set(true);
  }

  confirmNewAlbum(): void {
    const name = this.newAlbumName.trim();
    if (!name) {
      return;
    }

    this.albumService.create(name, this.selectedFileIds).subscribe(() => {
      this.newAlbumName = '';
      this.creatingNew.set(false);
      this.refresh();
    });
  }

  toggleAlbum(membership: AlbumMembership): void {
    this.busyAlbumId.set(membership.albumId);
    const request$ = membership.containsAll
      ? this.albumService.removeMedia(membership.albumId, this.selectedFileIds)
      : this.albumService.addMedia(membership.albumId, this.selectedFileIds);

    request$.subscribe({
      next: () => {
        this.busyAlbumId.set(null);
        this.refresh();
      },
      error: () => this.busyAlbumId.set(null),
    });
  }

  close(): void {
    this.closed.emit();
  }
}

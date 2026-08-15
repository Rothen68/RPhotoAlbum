import { HttpClient } from '@angular/common/http';
import { Component, EventEmitter, Input, OnInit, Output, inject, signal } from '@angular/core';

export interface PCloudFolderRef {
  id: number;
  name: string;
  path: string;
}

interface PCloudFolderBrowseResult extends PCloudFolderRef {
  subfolders: { id: number; name: string }[];
}

@Component({
  selector: 'app-pcloud-folder-picker',
  standalone: true,
  templateUrl: './pcloud-folder-picker.component.html',
  styleUrl: './pcloud-folder-picker.component.scss',
})
export class PCloudFolderPickerComponent implements OnInit {
  private readonly http = inject(HttpClient);

  @Input() startFolderId = 0;
  @Output() folderSelected = new EventEmitter<PCloudFolderRef>();
  @Output() cancelled = new EventEmitter<void>();

  protected readonly current = signal<PCloudFolderBrowseResult | null>(null);
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);
  private readonly history: PCloudFolderRef[] = [];

  ngOnInit(): void {
    this.browse(this.startFolderId);
  }

  protected get canGoBack(): boolean {
    return this.history.length > 0;
  }

  browse(folderId: number): void {
    this.loading.set(true);
    this.error.set(null);
    this.http.get<PCloudFolderBrowseResult>(`/api/pcloud/folders/${folderId}`).subscribe({
      next: (result) => {
        this.current.set(result);
        this.loading.set(false);
      },
      error: () => {
        this.loading.set(false);
        this.error.set('Impossible de lire ce dossier pCloud.');
      },
    });
  }

  open(folder: { id: number; name: string }): void {
    const current = this.current();
    if (current) {
      this.history.push({ id: current.id, name: current.name, path: current.path });
    }
    this.browse(folder.id);
  }

  goBack(): void {
    const previous = this.history.pop();
    if (previous) {
      this.browse(previous.id);
    }
  }

  confirm(): void {
    const current = this.current();
    if (current) {
      this.folderSelected.emit({ id: current.id, name: current.name, path: current.path });
    }
  }

  cancel(): void {
    this.cancelled.emit();
  }
}

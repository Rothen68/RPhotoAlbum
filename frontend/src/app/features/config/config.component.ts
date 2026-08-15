import { Component, OnInit, inject, signal } from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { AppConfiguration, ConfigService, SourceFolder } from '../../core/config/config.service';
import { MediaService } from '../../core/media/media.service';
import { PCloudService, PCloudStatus } from '../../core/pcloud/pcloud.service';
import { PCloudFolderPickerComponent, PCloudFolderRef } from '../../shared/pcloud-folder-picker/pcloud-folder-picker.component';

type PickerMode = 'album' | 'source' | null;

@Component({
  selector: 'app-config',
  standalone: true,
  imports: [RouterLink, PCloudFolderPickerComponent],
  templateUrl: './config.component.html',
  styleUrl: './config.component.scss',
})
export class ConfigComponent implements OnInit {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly configService = inject(ConfigService);
  private readonly mediaService = inject(MediaService);
  private readonly pcloud = inject(PCloudService);

  protected readonly pcloudStatus = signal<PCloudStatus | null>(null);
  protected readonly config = signal<AppConfiguration | null>(null);
  protected readonly pickerMode = signal<PickerMode>(null);
  protected readonly saving = signal(false);
  protected readonly reindexing = signal(false);
  protected readonly indexedCount = signal<number | null>(null);
  protected readonly message = signal<string | null>(null);
  protected readonly connectUrl = this.pcloud.connectUrl;

  ngOnInit(): void {
    const result = this.route.snapshot.queryParamMap.get('pcloud');
    if (result === 'connected') {
      this.message.set('Compte pCloud connecté.');
      this.router.navigate([], { relativeTo: this.route, queryParams: {} });
    } else if (result === 'error') {
      this.message.set("Échec de la connexion à pCloud.");
      this.router.navigate([], { relativeTo: this.route, queryParams: {} });
    }

    this.refreshPCloudStatus();
    this.loadConfig();
    this.refreshIndexedCount();
  }

  private refreshPCloudStatus(): void {
    this.pcloud.status().subscribe((status) => this.pcloudStatus.set(status));
  }

  private loadConfig(): void {
    this.configService.get().subscribe((config) => this.config.set(config));
  }

  private refreshIndexedCount(): void {
    this.mediaService.sourceCount().subscribe((page) => this.indexedCount.set(page.total));
  }

  disconnectPCloud(): void {
    this.pcloud.disconnect().subscribe(() => this.refreshPCloudStatus());
  }

  openAlbumFolderPicker(): void {
    this.pickerMode.set('album');
  }

  openSourceFolderPicker(): void {
    this.pickerMode.set('source');
  }

  closePicker(): void {
    this.pickerMode.set(null);
  }

  onFolderSelected(folder: PCloudFolderRef): void {
    const current = this.config() ?? { albumParentFolder: null, sourceFolders: [] };

    if (this.pickerMode() === 'album') {
      this.config.set({ ...current, albumParentFolder: { folderId: folder.id, path: folder.path } });
    } else if (this.pickerMode() === 'source') {
      const alreadyPresent = current.sourceFolders.some((f) => f.folderId === folder.id);
      if (!alreadyPresent) {
        const newFolder: SourceFolder = { folderId: folder.id, label: folder.name, path: folder.path };
        this.config.set({ ...current, sourceFolders: [...current.sourceFolders, newFolder] });
      }
    }

    this.pickerMode.set(null);
  }

  removeSourceFolder(index: number): void {
    const current = this.config();
    if (!current) {
      return;
    }
    this.config.set({ ...current, sourceFolders: current.sourceFolders.filter((_, i) => i !== index) });
  }

  save(): void {
    const current = this.config();
    if (!current) {
      return;
    }

    this.saving.set(true);
    this.message.set(null);
    this.configService.save(current).subscribe({
      next: (saved) => {
        this.config.set(saved);
        this.saving.set(false);
        this.message.set('Configuration enregistrée.');
      },
      error: (err) => {
        this.saving.set(false);
        this.message.set(err.error?.error ?? "Erreur lors de l'enregistrement.");
      },
    });
  }

  reindex(): void {
    this.reindexing.set(true);
    this.message.set(null);
    this.mediaService.reindex().subscribe({
      next: (result) => {
        this.reindexing.set(false);
        this.message.set(`Indexation terminée : ${result.indexed} médias.`);
        this.refreshIndexedCount();
      },
      error: (err) => {
        this.reindexing.set(false);
        this.message.set(err.error?.error ?? "Échec de l'indexation.");
      },
    });
  }
}

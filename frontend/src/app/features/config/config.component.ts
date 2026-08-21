import { Component, OnDestroy, OnInit, inject, signal } from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { AlbumService } from '../../core/albums/album.service';
import { AuthService } from '../../core/auth/auth.service';
import { AppConfiguration, ConfigService, SourceFolder } from '../../core/config/config.service';
import { ExifJobStatus, GeoJobStatus, MediaCacheStatus, MediaService } from '../../core/media/media.service';
import { OfflineModeService } from '../../core/offline/offline-mode.service';
import { PCloudService, PCloudStatus } from '../../core/pcloud/pcloud.service';
import { PCloudFolderPickerComponent, PCloudFolderRef } from '../../shared/pcloud-folder-picker/pcloud-folder-picker.component';
import { APP_VERSION } from '../../core/version';

const STATUS_POLL_MS = 3000;

type PickerMode = 'album' | 'source' | null;

@Component({
  selector: 'app-config',
  standalone: true,
  imports: [RouterLink, PCloudFolderPickerComponent],
  templateUrl: './config.component.html',
  styleUrl: './config.component.scss',
  host: { class: 'page' },
})
export class ConfigComponent implements OnInit, OnDestroy {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly configService = inject(ConfigService);
  private readonly mediaService = inject(MediaService);
  private readonly albumsService = inject(AlbumService);
  private readonly pcloud = inject(PCloudService);
  private readonly auth = inject(AuthService);
  protected readonly offlineMode = inject(OfflineModeService);

  protected readonly pcloudStatus = signal<PCloudStatus | null>(null);
  protected readonly config = signal<AppConfiguration | null>(null);
  protected readonly pickerMode = signal<PickerMode>(null);
  protected readonly saving = signal(false);
  protected readonly reindexing = signal(false);
  protected readonly reindexingAlbums = signal(false);
  protected readonly indexedCount = signal<number | null>(null);
  protected readonly cacheStatus = signal<MediaCacheStatus | null>(null);
  protected readonly message = signal<string | null>(null);
  protected readonly appVersion = APP_VERSION;
  protected readonly connectUrl = this.pcloud.connectUrl;

  protected readonly exifStatus = signal<ExifJobStatus | null>(null);
  protected readonly geoStatus = signal<GeoJobStatus | null>(null);

  private exifPollTimer?: ReturnType<typeof setTimeout>;
  private geoPollTimer?: ReturnType<typeof setTimeout>;

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
    this.refreshCacheStatus();
    this.refreshExifStatus();
    this.refreshGeoStatus();
  }

  ngOnDestroy(): void {
    clearTimeout(this.exifPollTimer);
    clearTimeout(this.geoPollTimer);
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

  private refreshCacheStatus(): void {
    this.mediaService.cacheStatus().subscribe((status) => this.cacheStatus.set(status));
  }

  protected formatMb(bytes: number): number {
    return Math.round(bytes / (1024 * 1024));
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
        const newFolder: SourceFolder = { folderId: folder.id, label: folder.name, path: folder.path, autoIndex: true };
        this.config.set({ ...current, sourceFolders: [...current.sourceFolders, newFolder] });
      }
    }

    this.pickerMode.set(null);
  }

  toggleSourceFolderAutoIndex(index: number): void {
    const current = this.config();
    if (!current) {
      return;
    }
    const sourceFolders = current.sourceFolders.map((f, i) => (i === index ? { ...f, autoIndex: !f.autoIndex } : f));
    this.config.set({ ...current, sourceFolders });
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

  reindexAlbums(): void {
    this.reindexingAlbums.set(true);
    this.message.set(null);
    this.albumsService.reindex().subscribe({
      next: (result) => {
        this.reindexingAlbums.set(false);
        this.message.set(`${result.found} album(s) trouvé(s) sur pCloud.`);
      },
      error: (err) => {
        this.reindexingAlbums.set(false);
        this.message.set(err.error?.error ?? "Échec de la recherche des albums.");
      },
    });
  }

  reindex(): void {
    this.reindexing.set(true);
    this.message.set(null);
    this.mediaService.reindex().subscribe({
      next: (result) => {
        this.reindexing.set(false);
        const failures = result.failedFolders.length
          ? ` Échec pour : ${result.failedFolders.join(', ')}.`
          : '';
        this.message.set(`Indexation terminée : ${result.indexed} médias.${failures}`);
        this.refreshIndexedCount();
      },
      error: (err) => {
        this.reindexing.set(false);
        this.message.set(err.error?.error ?? "Échec de l'indexation.");
      },
    });
  }

  logout(): void {
    this.auth.logout().subscribe(() => this.router.navigateByUrl('/login'));
  }

  // --- Extraction EXIF + géolocalisation (étape 9) ---

  startExif(): void {
    this.mediaService.startExif().subscribe(() => this.refreshExifStatus());
  }

  stopExif(): void {
    this.mediaService.stopExif().subscribe(() => this.refreshExifStatus());
  }

  startGeo(): void {
    this.mediaService.startGeo().subscribe(() => this.refreshGeoStatus());
  }

  stopGeo(): void {
    this.mediaService.stopGeo().subscribe(() => this.refreshGeoStatus());
  }

  // Re-sondage tant que le job tourne (setTimeout plutôt qu'un intervalle fixe : évite
  // d'empiler des requêtes si une réponse tarde) — la progression elle-même vient toujours du
  // serveur (recalculée depuis la base), jamais estimée côté client.
  private refreshExifStatus(): void {
    clearTimeout(this.exifPollTimer);
    this.mediaService.exifStatus().subscribe((status) => {
      this.exifStatus.set(status);
      if (status.running) {
        this.exifPollTimer = setTimeout(() => this.refreshExifStatus(), STATUS_POLL_MS);
      }
    });
  }

  private refreshGeoStatus(): void {
    clearTimeout(this.geoPollTimer);
    this.mediaService.geoStatus().subscribe((status) => {
      this.geoStatus.set(status);
      if (status.running) {
        this.geoPollTimer = setTimeout(() => this.refreshGeoStatus(), STATUS_POLL_MS);
      }
    });
  }
}

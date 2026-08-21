import { Injectable, inject, signal } from '@angular/core';
import { AlbumDetail, AlbumItem, AlbumService } from '../albums/album.service';

const MANIFEST_KEY = 'rphotoalbum:offlineAlbums';

export interface OfflineAlbumMeta {
  itemCount: number;
  sizeBytes: number;
  downloadedAt: string;
}

type Manifest = Record<string, OfflineAlbumMeta>;

// État local à cet appareil (pas synchronisé sur pCloud) — même pattern que
// COLLAPSED_STORAGE_KEY dans albums.component.ts.
function loadManifest(): Manifest {
  try {
    const raw = localStorage.getItem(MANIFEST_KEY);
    return raw ? JSON.parse(raw) : {};
  } catch {
    return {};
  }
}

function saveManifest(manifest: Manifest): void {
  try {
    localStorage.setItem(MANIFEST_KEY, JSON.stringify(manifest));
  } catch {
    // Quota localStorage dépassé ou navigation privée — pas bloquant, juste pas persisté.
  }
}

function cacheNameFor(albumId: string): string {
  return `offline-album-${albumId}`;
}

function albumApiUrl(albumId: string): string {
  return `/api/albums/${albumId}`;
}

// Rend les miniatures d'un album consultables sans connexion (issue #29, V1 : miniatures
// uniquement). Un cache Cache Storage par album (suppression triviale, dédup cross-album non
// géré — un média présent dans deux albums hors-ligne est stocké deux fois, coût accepté en V1).
@Injectable({ providedIn: 'root' })
export class OfflineAlbumService {
  private readonly albumService = inject(AlbumService);

  private readonly manifest = signal<Manifest>(loadManifest());
  private readonly downloading = signal<ReadonlySet<string>>(new Set());
  private readonly progressMap = signal<ReadonlyMap<string, number>>(new Map());

  isOffline(albumId: string): boolean {
    return albumId in this.manifest();
  }

  metaFor(albumId: string): OfflineAlbumMeta | null {
    return this.manifest()[albumId] ?? null;
  }

  isDownloading(albumId: string): boolean {
    return this.downloading().has(albumId);
  }

  progressFor(albumId: string): number {
    return this.progressMap().get(albumId) ?? 0;
  }

  // Doit rester identique à AlbumDetailComponent.thumbnailUrl() — c'est la seule variante
  // (800x800, crop=true) mise en cache pour la consultation hors-ligne (issue #29 V1).
  private thumbnailUrl(fileId: number): string {
    return this.albumService.thumbnailUrl(fileId, 800);
  }

  // Tout ou rien, équivalent Cache Storage du pattern tmp+rename déjà utilisé côté serveur
  // (MediaThumbnailCacheService.cs, #26) : Cache Storage n'a pas de rename atomique, donc on
  // efface le cache AVANT de commencer (nettoie un essai précédent interrompu) et on ne
  // committe le manifest qu'après succès complet — tout échec en cours de route (réseau,
  // QuotaExceededError de cache.put) efface entièrement le cache partiel.
  async makeAvailable(album: AlbumDetail): Promise<void> {
    const albumId = album.id;
    if (this.isDownloading(albumId)) {
      return;
    }

    const cacheName = cacheNameFor(albumId);
    this.setDownloading(albumId, true);
    this.setProgress(albumId, 0);

    try {
      await caches.delete(cacheName);
      const cache = await caches.open(cacheName);

      const mediaItems = album.items.filter((i) => i.type === 'media' && i.albumCopy);
      let sizeBytes = 0;
      for (let i = 0; i < mediaItems.length; i++) {
        const fileId = mediaItems[i].albumCopy!.fileId;
        const url = this.thumbnailUrl(fileId);
        const res = await fetch(url, { credentials: 'include' });
        if (!res.ok) {
          throw new Error(`Téléchargement échoué pour la miniature ${fileId} (${res.status})`);
        }
        sizeBytes += (await res.clone().blob()).size;
        await cache.put(url, res);
        this.setProgress(albumId, (i + 1) / mediaItems.length);
      }

      const albumResponse = new Response(JSON.stringify(album), {
        headers: { 'Content-Type': 'application/json' },
      });
      await cache.put(albumApiUrl(albumId), albumResponse);

      const next = {
        ...this.manifest(),
        [albumId]: { itemCount: mediaItems.length, sizeBytes, downloadedAt: new Date().toISOString() },
      };
      this.manifest.set(next);
      saveManifest(next);
    } catch (err) {
      await caches.delete(cacheName);
      throw err;
    } finally {
      this.setDownloading(albumId, false);
      this.setProgress(albumId, 0);
    }
  }

  async remove(albumId: string): Promise<void> {
    await caches.delete(cacheNameFor(albumId));
    const next = { ...this.manifest() };
    delete next[albumId];
    this.manifest.set(next);
    saveManifest(next);
  }

  async getCachedAlbum(albumId: string): Promise<AlbumDetail | null> {
    try {
      const cache = await caches.open(cacheNameFor(albumId));
      const res = await cache.match(albumApiUrl(albumId));
      return res ? ((await res.json()) as AlbumDetail) : null;
    } catch {
      return null;
    }
  }

  // Avale ses propres erreurs et renvoie une map partielle/vide plutôt que de lever — un cache
  // manquant ou corrompu doit dégrader silencieusement vers le réseau normal côté composant,
  // jamais casser l'affichage.
  async buildObjectUrlMap(albumId: string, mediaItems: AlbumItem[]): Promise<Map<number, string>> {
    const result = new Map<number, string>();
    try {
      const cache = await caches.open(cacheNameFor(albumId));
      for (const item of mediaItems) {
        const fileId = item.albumCopy?.fileId;
        if (fileId === undefined) {
          continue;
        }
        const res = await cache.match(this.thumbnailUrl(fileId));
        if (res) {
          result.set(fileId, URL.createObjectURL(await res.blob()));
        }
      }
    } catch {
      // Cache absent/inaccessible — la map partielle déjà construite (éventuellement vide) suffit.
    }
    return result;
  }

  private setDownloading(albumId: string, value: boolean): void {
    this.downloading.update((current) => {
      const next = new Set(current);
      if (value) {
        next.add(albumId);
      } else {
        next.delete(albumId);
      }
      return next;
    });
  }

  private setProgress(albumId: string, value: number): void {
    this.progressMap.update((current) => {
      const next = new Map(current);
      next.set(albumId, value);
      return next;
    });
  }
}

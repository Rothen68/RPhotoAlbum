import { Injectable, inject, signal } from '@angular/core';
import { AlbumDetail, AlbumItem, AlbumService } from '../albums/album.service';

const MANIFEST_KEY = 'rphotoalbum:offlineAlbums';

export interface OfflineAlbumMeta {
  // Album name at download time — used to display the list of albums available offline when
  // /api/albums itself is unreachable (issue #29: without this fallback, an album made
  // available offline would remain unreachable from the album list once offline, since it
  // couldn't even be found there to click on).
  name: string;
  itemCount: number;
  // Total media count of the album at download time — may differ from itemCount if some
  // thumbnails failed even after retrying (see makeAvailable below): lets the UI report a
  // partial result instead of implying full coverage.
  totalCount: number;
  sizeBytes: number;
  downloadedAt: string;
}

type Manifest = Record<string, OfflineAlbumMeta>;

// State local to this device (not synced to pCloud) — same pattern as COLLAPSED_STORAGE_KEY
// in albums.component.ts.
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
    // localStorage quota exceeded or private browsing — not blocking, just not persisted.
  }
}

function cacheNameFor(albumId: string): string {
  return `offline-album-${albumId}`;
}

function albumApiUrl(albumId: string): string {
  return `/api/albums/${albumId}`;
}

// Makes an album's thumbnails viewable without a connection (issue #29, V1: thumbnails only).
// One Cache Storage cache per album (trivial deletion, cross-album dedup not handled — a media
// item present in two offline albums is stored twice, an accepted cost in V1).
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

  // Fallback for the album list when /api/albums is unreachable (issue #29) — no
  // sections/ordering (never cached, specific to the server structure), just enough to find
  // and open an album already available offline.
  listOffline(): { id: string; meta: OfflineAlbumMeta }[] {
    return Object.entries(this.manifest()).map(([id, meta]) => ({ id, meta }));
  }

  isDownloading(albumId: string): boolean {
    return this.downloading().has(albumId);
  }

  progressFor(albumId: string): number {
    return this.progressMap().get(albumId) ?? 0;
  }

  // Must stay identical to AlbumDetailComponent.thumbnailUrl() — it's the only variant
  // (800x800, crop=true) cached for offline viewing (issue #29 V1).
  private thumbnailUrl(fileId: number): string {
    return this.albumService.thumbnailUrl(fileId, 800);
  }

  // Cache Storage has no atomic rename — we clear the cache BEFORE starting (cleans up a
  // previous interrupted attempt) and only commit the manifest at the end. However, we do NOT
  // require every thumbnail to succeed: MediaController.Thumbnail turns any pCloud error
  // (timeout, transient glitch) into a plain 404 indistinguishable from media that's genuinely
  // absent — on a large album, a single glitch among dozens of calls shouldn't cancel the whole
  // download (observed in real usage: a road-trip album failed entirely because of a single
  // failed thumbnail). Each thumbnail is retried once; if it still fails, it's simply absent
  // from the cache (see totalCount vs itemCount) rather than fatal. Only a genuine storage
  // failure (QuotaExceededError) or zero coverage (no thumbnail retrieved at all) cancels the
  // operation.
  async makeAvailable(album: AlbumDetail): Promise<void> {
    const albumId = album.id;
    if (this.isDownloading(albumId)) {
      return;
    }

    // The Cache Storage API (self.caches) only exists in a secure context (HTTPS, or
    // http://localhost) — on a plain HTTP LAN address (e.g. http://192.168.x.x), `caches` is
    // simply absent from `window`, and fails instantly for reasons unrelated to the network or
    // disk space. Detected explicitly here for a clear message rather than a generic TypeError
    // on `caches.delete is not a function`.
    if (!window.isSecureContext) {
      throw new DOMException(
        'La consultation hors-ligne nécessite une connexion sécurisée (HTTPS).',
        'InsecureContextError',
      );
    }

    const cacheName = cacheNameFor(albumId);
    this.setDownloading(albumId, true);
    this.setProgress(albumId, 0);

    try {
      await caches.delete(cacheName);
      const cache = await caches.open(cacheName);

      const mediaItems = album.items.filter((i) => i.type === 'media' && i.albumCopy);
      let sizeBytes = 0;
      let cachedCount = 0;
      for (let i = 0; i < mediaItems.length; i++) {
        const fileId = mediaItems[i].albumCopy!.fileId;
        const url = this.thumbnailUrl(fileId);
        const res = await this.fetchWithRetry(url);
        if (res) {
          sizeBytes += (await res.clone().blob()).size;
          await cache.put(url, res);
          cachedCount++;
        }
        this.setProgress(albumId, (i + 1) / mediaItems.length);
      }

      if (mediaItems.length > 0 && cachedCount === 0) {
        throw new Error("Aucune miniature n'a pu être téléchargée.");
      }

      const albumResponse = new Response(JSON.stringify(album), {
        headers: { 'Content-Type': 'application/json' },
      });
      await cache.put(albumApiUrl(albumId), albumResponse);

      const next = {
        ...this.manifest(),
        [albumId]: {
          name: album.name,
          itemCount: cachedCount,
          totalCount: mediaItems.length,
          sizeBytes,
          downloadedAt: new Date().toISOString(),
        },
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

  // A single retry after a short delay — enough to absorb a transient glitch (pCloud, mobile
  // network timeout) without excessively slowing down a large album. Returns null (never
  // throws): an individual thumbnail failure is handled by the caller as a simple "missing"
  // case, not a fatal error — see makeAvailable.
  private async fetchWithRetry(url: string): Promise<Response | null> {
    for (let attempt = 0; attempt < 2; attempt++) {
      try {
        const res = await fetch(url, { credentials: 'include' });
        if (res.ok) {
          return res;
        }
      } catch {
        // Network failure (no response at all) — same retry logic below.
      }
      if (attempt === 0) {
        await new Promise((resolve) => setTimeout(resolve, 800));
      }
    }
    return null;
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

  // Swallows its own errors and returns a partial/empty map rather than throwing — a missing or
  // corrupted cache must silently degrade to the normal network path on the component side,
  // never break the display. Reads happen in parallel (Promise.all), not sequentially: on a
  // large album (user report: 66 media items), doing cache.match()+blob() per item one after
  // another could take several cumulative seconds before even a single thumbnail got its real
  // Object URL — meanwhile the component would fall back to the network URL (which stays stuck
  // indefinitely offline, an <img> having no native abandon delay). No network call here (only
  // local Cache Storage), so parallelizing doesn't overload anything.
  async buildObjectUrlMap(albumId: string, mediaItems: AlbumItem[]): Promise<Map<number, string>> {
    const result = new Map<number, string>();
    try {
      const cache = await caches.open(cacheNameFor(albumId));
      await Promise.all(
        mediaItems.map(async (item) => {
          const fileId = item.albumCopy?.fileId;
          if (fileId === undefined) {
            return;
          }
          const res = await cache.match(this.thumbnailUrl(fileId));
          if (res) {
            result.set(fileId, URL.createObjectURL(await res.blob()));
          }
        }),
      );
    } catch {
      // Cache missing/inaccessible — the partial map already built (possibly empty) is enough.
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

import { Injectable, inject, signal } from '@angular/core';
import { AlbumDetail, AlbumItem, AlbumService } from '../albums/album.service';

const MANIFEST_KEY = 'rphotoalbum:offlineAlbums';

export interface OfflineAlbumMeta {
  // Nom de l'album au moment du téléchargement — utilisé pour afficher la liste des albums
  // disponibles hors-ligne quand /api/albums lui-même est injoignable (issue #29 : sans ce
  // repli, un album rendu disponible hors-ligne resterait inatteignable depuis la liste des
  // albums une fois hors-ligne, faute de pouvoir même l'y retrouver pour cliquer dessus).
  name: string;
  itemCount: number;
  // Nombre total de médias de l'album au moment du téléchargement — peut différer de itemCount
  // si certaines miniatures ont échoué même après nouvel essai (voir makeAvailable ci-dessous) :
  // permet à l'UI de signaler un résultat partiel plutôt que de laisser croire à une couverture
  // complète.
  totalCount: number;
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

  // Repli pour la liste des albums quand /api/albums est injoignable (issue #29) — pas de
  // sections/ordre (jamais mis en cache, propre à la structure serveur), juste de quoi retrouver
  // et ouvrir un album déjà disponible hors-ligne.
  listOffline(): { id: string; meta: OfflineAlbumMeta }[] {
    return Object.entries(this.manifest()).map(([id, meta]) => ({ id, meta }));
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

  // Cache Storage n'a pas de rename atomique — on efface le cache AVANT de commencer (nettoie
  // un essai précédent interrompu) et on ne committe le manifest qu'à la fin. En revanche, on
  // n'exige PAS que toutes les miniatures réussissent : MediaController.Thumbnail convertit
  // toute erreur pCloud (timeout, aléa transitoire) en simple 404 indiscernable d'un média
  // réellement absent — sur un grand album, un seul aléa parmi des dizaines d'appels ne doit
  // pas annuler tout le téléchargement (constaté en usage réel : un album de road trip a échoué
  // intégralement à cause d'une seule miniature en défaut). Chaque miniature est retentée une
  // fois ; si elle échoue encore, elle est simplement absente du cache (voir totalCount vs
  // itemCount) plutôt que fatale. Seuls un vrai échec de stockage (QuotaExceededError) ou une
  // couverture nulle (aucune miniature récupérée) annulent l'opération.
  async makeAvailable(album: AlbumDetail): Promise<void> {
    const albumId = album.id;
    if (this.isDownloading(albumId)) {
      return;
    }

    // L'API Cache Storage n'existe (self.caches) que dans un contexte sécurisé (HTTPS, ou
    // http://localhost) — sur une adresse LAN en HTTP simple (ex. http://192.168.x.x), `caches`
    // est tout simplement absent de `window`, et échoue instantanément sans rapport avec le
    // réseau ou l'espace disque. Détecté explicitement ici pour un message clair plutôt qu'un
    // TypeError générique sur `caches.delete is not a function`.
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

  // Un seul nouvel essai après un court délai — suffisant pour absorber un aléa transitoire
  // (pCloud, timeout réseau mobile) sans ralentir excessivement un grand album. Renvoie null
  // (jamais ne lève) : un échec de miniature individuel est géré par l'appelant comme un simple
  // "manquant", pas une erreur fatale — voir makeAvailable.
  private async fetchWithRetry(url: string): Promise<Response | null> {
    for (let attempt = 0; attempt < 2; attempt++) {
      try {
        const res = await fetch(url, { credentials: 'include' });
        if (res.ok) {
          return res;
        }
      } catch {
        // Échec réseau (pas de réponse du tout) — même logique de nouvel essai ci-dessous.
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

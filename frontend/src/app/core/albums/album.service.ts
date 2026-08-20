import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

export interface AlbumSummary {
  id: string;
  name: string;
  itemCount: number;
  coverFileId: number | null;
  updatedAt: string;
}

export interface AlbumMediaRef {
  fileId: number;
  name: string;
}

export interface AlbumItem {
  id: string;
  type: 'media' | 'text';
  mediaType: 'image' | 'video' | null;
  date: string | null;
  source: AlbumMediaRef | null;
  albumCopy: AlbumMediaRef | null;
  markdown: string | null;
  rowSpan: number;
  // Dimensions de l'image d'origine (issues de l'extraction EXIF) — absentes si le média n'a
  // pas encore été traité, ou pour les vidéos. Utilisées pour précalculer la hauteur d'une
  // rangée non groupée dans la virtualisation (issue #20).
  width: number | null;
  height: number | null;
  // Date de prise de vue et localisation (issues des jobs EXIF/géo) — figées au moment où le
  // média a été ajouté à l'album, pas mises à jour rétroactivement si le média source est
  // traité plus tard (même limitation que width/height ci-dessus). Voir issue #22.
  dateTaken: string | null;
  country: string | null;
  region: string | null;
  city: string | null;
}

export interface AlbumDetail {
  id: string;
  name: string;
  updatedAt: string;
  items: AlbumItem[];
}

export interface AlbumMembership {
  albumId: string;
  name: string;
  containsAll: boolean;
}

// Regroupement/ordre des albums (issue #6) — Sections préserve l'ordre persisté, Unsectioned
// regroupe les albums pas encore rangés (nouveaux albums compris, sans action requise).
export interface AlbumSection {
  id: string;
  name: string;
  albums: AlbumSummary[];
}

export interface AlbumListResult {
  sections: AlbumSection[];
  unsectioned: AlbumSummary[];
}

export interface AlbumStructureSectionInput {
  id: string | null;
  name: string;
  albumIds: string[];
}

@Injectable({ providedIn: 'root' })
export class AlbumService {
  private readonly http = inject(HttpClient);

  list(): Observable<AlbumListResult> {
    return this.http.get<AlbumListResult>('/api/albums');
  }

  saveStructure(sections: AlbumStructureSectionInput[], unsectionedAlbumIds: string[]): Observable<AlbumListResult> {
    return this.http.put<AlbumListResult>('/api/albums/structure', { sections, unsectionedAlbumIds });
  }

  reindex(): Observable<{ found: number }> {
    return this.http.post<{ found: number }>('/api/albums/reindex', {});
  }

  create(name: string, initialMediaFileIds?: number[]): Observable<AlbumDetail> {
    return this.http.post<AlbumDetail>('/api/albums', { name, initialMediaFileIds });
  }

  get(id: string): Observable<AlbumDetail> {
    return this.http.get<AlbumDetail>(`/api/albums/${id}`);
  }

  delete(id: string): Observable<void> {
    return this.http.delete<void>(`/api/albums/${id}`);
  }

  membership(fileIds: number[]): Observable<AlbumMembership[]> {
    return this.http.post<AlbumMembership[]>('/api/albums/membership', { fileIds });
  }

  addMedia(id: string, fileIds: number[]): Observable<AlbumDetail> {
    return this.http.post<AlbumDetail>(`/api/albums/${id}/media/add`, { fileIds });
  }

  removeMedia(id: string, fileIds: number[]): Observable<AlbumDetail> {
    return this.http.post<AlbumDetail>(`/api/albums/${id}/media/remove`, { fileIds });
  }

  addText(id: string, afterItemId: string | null, markdown: string): Observable<AlbumDetail> {
    return this.http.post<AlbumDetail>(`/api/albums/${id}/text`, { afterItemId, markdown });
  }

  updateText(id: string, itemId: string, markdown: string): Observable<AlbumDetail> {
    return this.http.put<AlbumDetail>(`/api/albums/${id}/items/${itemId}`, { markdown });
  }

  removeItem(id: string, itemId: string): Observable<AlbumDetail> {
    return this.http.delete<AlbumDetail>(`/api/albums/${id}/items/${itemId}`);
  }

  reorder(id: string, itemIds: string[], rowSpans?: Record<string, number>): Observable<AlbumDetail> {
    return this.http.put<AlbumDetail>(`/api/albums/${id}/order`, { itemIds, rowSpans });
  }

  thumbnailUrl(fileId: number, size = 300, crop = true): string {
    return `/api/media/${fileId}/thumbnail?width=${size}&height=${size}&crop=${crop}`;
  }

  streamUrl(fileId: number): string {
    return `/api/media/${fileId}/stream`;
  }

  downloadUrl(fileId: number): string {
    return `/api/media/${fileId}/download`;
  }
}

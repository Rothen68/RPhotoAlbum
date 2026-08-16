import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

export interface ReindexResult {
  indexed: number;
  failedFolders: string[];
}

export interface MediaItem {
  pCloudFileId: number;
  name: string;
  mediaType: 'image' | 'video';
  size: number;
  createdAt: string | null;
  modifiedAt: string | null;
}

export interface MediaSourcePage {
  total: number;
  page: number;
  pageSize: number;
  items: MediaItem[];
}

export interface DateGroup {
  date: string;
  count: number;
}

// Pas de filtre localisation : aucune donnée GPS/EXIF disponible sans télécharger chaque
// fichier — hors scope (voir ARCHITECTURE.md).
export interface MediaFilters {
  search?: string;
  mediaType?: 'image' | 'video';
  minSize?: number;
  maxSize?: number;
}

@Injectable({ providedIn: 'root' })
export class MediaService {
  private readonly http = inject(HttpClient);

  reindex(): Observable<ReindexResult> {
    return this.http.post<ReindexResult>('/api/media/reindex', {});
  }

  source(page: number, pageSize: number, filters: MediaFilters = {}): Observable<MediaSourcePage> {
    const params = this.buildFilterParams(filters).set('page', page).set('pageSize', pageSize);
    return this.http.get<MediaSourcePage>('/api/media/source', { params });
  }

  sourceCount(): Observable<MediaSourcePage> {
    return this.source(1, 1);
  }

  dateGroups(filters: MediaFilters = {}): Observable<DateGroup[]> {
    return this.http.get<DateGroup[]>('/api/media/date-groups', { params: this.buildFilterParams(filters) });
  }

  reject(fileIds: number[]): Observable<{ rejected: number }> {
    return this.http.post<{ rejected: number }>('/api/media/reject', { fileIds });
  }

  thumbnailUrl(fileId: number, size = 300, crop = true): string {
    return `/api/media/${fileId}/thumbnail?width=${size}&height=${size}&crop=${crop}`;
  }

  streamUrl(fileId: number): string {
    return `/api/media/${fileId}/stream`;
  }

  private buildFilterParams(filters: MediaFilters): HttpParams {
    let params = new HttpParams();
    if (filters.search) {
      params = params.set('search', filters.search);
    }
    if (filters.mediaType) {
      params = params.set('mediaType', filters.mediaType);
    }
    if (filters.minSize !== undefined) {
      params = params.set('minSize', filters.minSize);
    }
    if (filters.maxSize !== undefined) {
      params = params.set('maxSize', filters.maxSize);
    }
    return params;
  }
}

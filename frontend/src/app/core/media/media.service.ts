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
  // Already returned by GET /api/media/source (the full MediaIndexEntry entity), simply not
  // declared here until now — see issue #22. Absent until the EXIF/geo jobs have processed the
  // media.
  dateTaken: string | null;
  country: string | null;
  region: string | null;
  city: string | null;
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

export interface MediaFilters {
  search?: string;
  mediaType?: 'image' | 'video';
  minSize?: number;
  maxSize?: number;
  country?: string;
  region?: string;
  city?: string;
}

// One row per distinct country/region/city combination observed in the library — lets the
// frontend derive dependent filters (issue #10) without an extra round trip on every
// selection change.
export interface LocationCombo {
  country: string;
  region: string | null;
  city: string | null;
}

export interface ExifJobStatus {
  running: boolean;
  processed: number;
  total: number;
  startedAt: string | null;
  lastError: string | null;
}

export type GeoJobStatus = ExifJobStatus;

// Thumbnail disk cache usage (issue #27) — in bytes, converted to MB on the component side
// for display.
export interface MediaCacheStatus {
  usedBytes: number;
  maxBytes: number;
}

@Injectable({ providedIn: 'root' })
export class MediaService {
  private readonly http = inject(HttpClient);

  reindex(): Observable<ReindexResult> {
    return this.http.post<ReindexResult>('/api/media/reindex', {});
  }

  cacheStatus(): Observable<MediaCacheStatus> {
    return this.http.get<MediaCacheStatus>('/api/media/cache-status');
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

  locations(): Observable<LocationCombo[]> {
    return this.http.get<LocationCombo[]>('/api/media/locations');
  }

  startExif(): Observable<void> {
    return this.http.post<void>('/api/media/exif/start', {});
  }

  exifStatus(): Observable<ExifJobStatus> {
    return this.http.get<ExifJobStatus>('/api/media/exif/status');
  }

  stopExif(): Observable<void> {
    return this.http.post<void>('/api/media/exif/stop', {});
  }

  startGeo(): Observable<void> {
    return this.http.post<void>('/api/media/geo/start', {});
  }

  geoStatus(): Observable<GeoJobStatus> {
    return this.http.get<GeoJobStatus>('/api/media/geo/status');
  }

  stopGeo(): Observable<void> {
    return this.http.post<void>('/api/media/geo/stop', {});
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
    if (filters.country) {
      params = params.set('country', filters.country);
    }
    if (filters.region) {
      params = params.set('region', filters.region);
    }
    if (filters.city) {
      params = params.set('city', filters.city);
    }
    return params;
  }
}

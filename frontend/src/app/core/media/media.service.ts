import { HttpClient } from '@angular/common/http';
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

@Injectable({ providedIn: 'root' })
export class MediaService {
  private readonly http = inject(HttpClient);

  reindex(): Observable<ReindexResult> {
    return this.http.post<ReindexResult>('/api/media/reindex', {});
  }

  source(page: number, pageSize: number): Observable<MediaSourcePage> {
    return this.http.get<MediaSourcePage>(`/api/media/source?page=${page}&pageSize=${pageSize}`);
  }

  sourceCount(): Observable<MediaSourcePage> {
    return this.source(1, 1);
  }

  dateGroups(): Observable<DateGroup[]> {
    return this.http.get<DateGroup[]>('/api/media/date-groups');
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
}

import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

export interface ReindexResult {
  indexed: number;
  failedFolders: string[];
}

export interface MediaSourcePage {
  total: number;
  page: number;
  pageSize: number;
}

@Injectable({ providedIn: 'root' })
export class MediaService {
  private readonly http = inject(HttpClient);

  reindex(): Observable<ReindexResult> {
    return this.http.post<ReindexResult>('/api/media/reindex', {});
  }

  sourceCount(): Observable<MediaSourcePage> {
    return this.http.get<MediaSourcePage>('/api/media/source?page=1&pageSize=1');
  }
}

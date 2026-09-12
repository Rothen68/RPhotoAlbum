import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

export interface PCloudStatus {
  connected: boolean;
  hostname: string | null;
}

// pCloud account quota (bytes) — distinct from the local thumbnail cache
// (MediaService.cacheStatus, issue #27): the raw media copy (compression disabled,
// ARCHITECTURE.md §13/§20) has no limit built into the application, only the pCloud quota
// itself is a hard limit.
export interface PCloudQuotaStatus {
  usedBytes: number;
  totalBytes: number;
}

@Injectable({ providedIn: 'root' })
export class PCloudService {
  private readonly http = inject(HttpClient);

  status(): Observable<PCloudStatus> {
    return this.http.get<PCloudStatus>('/api/pcloud/status');
  }

  disconnect(): Observable<void> {
    return this.http.post<void>('/api/pcloud/disconnect', {});
  }

  quota(): Observable<PCloudQuotaStatus> {
    return this.http.get<PCloudQuotaStatus>('/api/pcloud/quota');
  }

  // Deliberate full-page navigation (OAuth flow), not an HttpClient call.
  readonly connectUrl = '/api/auth/pcloud/start';
}

import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

export interface PCloudStatus {
  connected: boolean;
  hostname: string | null;
}

// Quota du compte pCloud (octets) — distinct du cache miniatures local (MediaService.cacheStatus,
// issue #27) : la copie brute des médias (compression désactivée, ARCHITECTURE.md §13/§20)
// n'a aucune limite intégrée à l'application, seul le quota pCloud lui-même est une limite dure.
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

  // Navigation plein-page volontaire (flux OAuth), pas un appel HttpClient.
  readonly connectUrl = '/api/auth/pcloud/start';
}

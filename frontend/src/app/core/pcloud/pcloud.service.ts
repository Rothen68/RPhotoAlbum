import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

export interface PCloudStatus {
  connected: boolean;
  hostname: string | null;
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

  // Navigation plein-page volontaire (flux OAuth), pas un appel HttpClient.
  readonly connectUrl = '/api/auth/pcloud/start';
}

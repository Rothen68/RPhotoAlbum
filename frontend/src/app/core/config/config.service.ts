import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

export interface AlbumFolder {
  folderId: number;
  path: string;
}

export interface SourceFolder {
  folderId: number;
  label: string;
  path: string;
  // Included in the automatic periodic reindex, or only during a manual "Reindex now" —
  // useful for a frozen archive folder (issue #28).
  autoIndex: boolean;
}

export interface AppConfiguration {
  albumParentFolder: AlbumFolder | null;
  sourceFolders: SourceFolder[];
}

@Injectable({ providedIn: 'root' })
export class ConfigService {
  private readonly http = inject(HttpClient);

  get(): Observable<AppConfiguration> {
    return this.http.get<AppConfiguration>('/api/config');
  }

  save(config: AppConfiguration): Observable<AppConfiguration> {
    return this.http.put<AppConfiguration>('/api/config', config);
  }
}

import { HttpClient } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { Observable, catchError, of, tap } from 'rxjs';

export interface Session {
  username: string;
}

@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly http = inject(HttpClient);

  private readonly session = signal<Session | null>(null);
  readonly isAuthenticated = computed(() => this.session() !== null);
  readonly username = computed(() => this.session()?.username ?? null);

  login(username: string, password: string): Observable<Session> {
    return this.http.post<Session>('/api/auth/login', { username, password }).pipe(
      tap((session) => this.session.set(session)),
    );
  }

  logout(): Observable<void> {
    return this.http.post<void>('/api/auth/logout', {}).pipe(
      tap(() => this.session.set(null)),
    );
  }

  // Interroge la session courante (ex. au démarrage de l'application) sans provoquer d'erreur console si non connecté.
  refresh(): Observable<Session | null> {
    return this.http.get<Session>('/api/auth/me').pipe(
      tap((session) => this.session.set(session)),
      catchError(() => {
        this.session.set(null);
        return of(null);
      }),
    );
  }
}

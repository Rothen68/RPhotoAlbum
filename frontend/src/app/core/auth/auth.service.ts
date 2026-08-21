import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { Observable, catchError, of, tap } from 'rxjs';
import { ConnectivityService } from '../offline/connectivity.service';

export interface Session {
  username: string;
}

const LAST_KNOWN_SESSION_KEY = 'rphotoalbum:lastKnownSession';

// Miroir local du dernier /api/auth/me confirmé par le serveur — pas une session en soi (le
// cookie HttpOnly reste la seule source d'autorité), juste de quoi décider, hors-ligne, s'il faut
// faire confiance au cookie déjà présent plutôt que bloquer sur /login (issue #29 : sans ça,
// ouvrir l'app hors-ligne après un redémarrage à froid — donc sans session en mémoire — renvoie
// systématiquement vers /login, page sur laquelle il n'y a de toute façon aucun moyen de se
// connecter sans réseau, empêchant même la consultation d'un album déjà disponible hors-ligne).
function loadLastKnownSession(): Session | null {
  try {
    const raw = localStorage.getItem(LAST_KNOWN_SESSION_KEY);
    return raw ? (JSON.parse(raw) as Session) : null;
  } catch {
    return null;
  }
}

function saveLastKnownSession(session: Session | null): void {
  try {
    if (session) {
      localStorage.setItem(LAST_KNOWN_SESSION_KEY, JSON.stringify(session));
    } else {
      localStorage.removeItem(LAST_KNOWN_SESSION_KEY);
    }
  } catch {
    // Quota localStorage dépassé ou navigation privée — pas bloquant.
  }
}

@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly http = inject(HttpClient);
  private readonly connectivity = inject(ConnectivityService);

  private readonly session = signal<Session | null>(null);
  readonly isAuthenticated = computed(() => this.session() !== null);
  readonly username = computed(() => this.session()?.username ?? null);

  login(username: string, password: string): Observable<Session> {
    return this.http.post<Session>('/api/auth/login', { username, password }).pipe(
      tap((session) => {
        this.session.set(session);
        saveLastKnownSession(session);
      }),
    );
  }

  logout(): Observable<void> {
    return this.http.post<void>('/api/auth/logout', {}).pipe(
      tap(() => {
        this.session.set(null);
        saveLastKnownSession(null);
      }),
    );
  }

  // Interroge la session courante (ex. au démarrage de l'application) sans provoquer d'erreur console si non connecté.
  refresh(): Observable<Session | null> {
    // Déjà su hors-ligne : inutile d'attendre l'échec (parfois lent, plusieurs secondes selon
    // l'appareil/réseau) d'une requête réseau vouée à échouer — repli direct sur le dernier
    // /api/auth/me confirmé, comme le ferait le catchError ci-dessous pour un status 0 (issue
    // #29 : sans ça, la page /login pouvait rester visiblement bloquée un moment avant que
    // l'échec réseau soit détecté, malgré une session locale valide disponible immédiatement).
    if (!this.connectivity.online()) {
      const lastKnown = loadLastKnownSession();
      this.session.set(lastKnown);
      return of(lastKnown);
    }

    return this.http.get<Session>('/api/auth/me').pipe(
      tap((session) => {
        this.session.set(session);
        saveLastKnownSession(session);
      }),
      catchError((err: HttpErrorResponse) => {
        // status 0 = la requête n'a jamais atteint le serveur (hors-ligne), à distinguer d'un
        // vrai 401 (serveur joint, cookie explicitement rejeté) — seul le premier cas justifie
        // de faire confiance au dernier /api/auth/me confirmé plutôt que d'exiger une
        // reconnexion impossible sans réseau. Un vrai 401 continue de déconnecter normalement.
        const lastKnown = err.status === 0 ? loadLastKnownSession() : null;
        this.session.set(lastKnown);
        if (!lastKnown) {
          saveLastKnownSession(null);
        }
        return of(lastKnown);
      }),
    );
  }
}

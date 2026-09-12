import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { Observable, of, tap } from 'rxjs';
import { ConnectivityService } from '../offline/connectivity.service';
import { OfflineModeService } from '../offline/offline-mode.service';

// navigator.onLine can be wrong or slow to update (observed in real usage: a device stayed
// "online" for a moment after switching to airplane mode, leaving the /api/auth/me request
// stuck pending indefinitely instead of failing cleanly) — an explicit delay guarantees we
// never stay stuck, whatever the cause of the network stall.
// Plain JS timer rather than the RxJS timeout() operator: observed in real usage that a
// request intercepted by the service worker (every /api/* request is, even without a
// dedicated cache rule) can stay stuck well beyond the RxJS delay — until the underlying TCP
// connection naturally fails (net::ERR_CONNECTION_TIMED_OUT, observed after several MINUTES).
// This fallback doesn't depend on any mechanism to actually cancel the HTTP request itself:
// once the delay has elapsed, we simply decide and ignore any late response.
const REQUEST_TIMEOUT_MS = 6000;

export interface Session {
  username: string;
}

const LAST_KNOWN_SESSION_KEY = 'rphotoalbum:lastKnownSession';

// Local mirror of the last /api/auth/me confirmed by the server — not a session in itself (the
// HttpOnly cookie remains the sole source of authority), just enough to decide, offline, whether
// to trust the cookie already present rather than blocking on /login (issue #29: without this,
// opening the app offline after a cold restart — so with no session in memory — always redirects
// to /login, a page where there's no way to log in without network anyway, which would even
// prevent viewing an album already available offline).
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
    // localStorage quota exceeded or private browsing — not a blocking issue.
  }
}

@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly http = inject(HttpClient);
  private readonly connectivity = inject(ConnectivityService);
  private readonly offlineMode = inject(OfflineModeService);

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

  // Queries the current session (e.g. on app startup) without causing a console error when not logged in.
  refresh(): Observable<Session | null> {
    // Offline mode forced by the user, or already known to be offline: no point waiting for
    // the failure (sometimes slow, several seconds depending on device/network) of a network
    // request doomed to fail — fall back directly to the last confirmed /api/auth/me, the same
    // way the catchError below would for a status 0 (issue #29: without this, the /login page
    // could stay visibly stuck for a moment before the network failure was detected, despite a
    // valid local session being immediately available).
    if (this.offlineMode.manualOfflineMode() || !this.connectivity.online()) {
      const lastKnown = loadLastKnownSession();
      this.session.set(lastKnown);
      return of(lastKnown);
    }

    return new Observable<Session | null>((subscriber) => {
      let settled = false;
      const fallbackTimer = setTimeout(() => {
        if (settled) {
          return;
        }
        settled = true;
        this.offlineMode.markUnreachable();
        const lastKnown = loadLastKnownSession();
        this.session.set(lastKnown);
        subscriber.next(lastKnown);
        subscriber.complete();
      }, REQUEST_TIMEOUT_MS);

      const subscription = this.http.get<Session>('/api/auth/me').subscribe({
        next: (session) => {
          if (settled) {
            return;
          }
          settled = true;
          clearTimeout(fallbackTimer);
          this.session.set(session);
          saveLastKnownSession(session);
          subscriber.next(session);
          subscriber.complete();
        },
        error: (err: HttpErrorResponse) => {
          if (settled) {
            return;
          }
          settled = true;
          clearTimeout(fallbackTimer);
          // A real 401 (server reached, cookie explicitly rejected) should log out normally.
          // Everything else (status 0: never reached the server) is NOT confirmation that the
          // session is invalid: we trust the last actually confirmed /api/auth/me rather than
          // requiring a re-login that might be impossible without network.
          const isRealRejection = err.status !== 0;
          if (!isRealRejection) {
            this.offlineMode.markUnreachable();
          }
          const lastKnown = isRealRejection ? null : loadLastKnownSession();
          this.session.set(lastKnown);
          if (!lastKnown) {
            saveLastKnownSession(null);
          }
          subscriber.next(lastKnown);
          subscriber.complete();
        },
      });

      return () => {
        clearTimeout(fallbackTimer);
        subscription.unsubscribe();
      };
    });
  }
}

import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { AuthService, Session } from './auth.service';

const LAST_KNOWN_SESSION_KEY = 'rphotoalbum:lastKnownSession';
// Doit rester synchronisé avec REQUEST_TIMEOUT_MS dans auth.service.ts (non exporté).
const REQUEST_TIMEOUT_MS = 6000;

// Verrouille le pattern setTimeout manuel de refresh() (remplace timeout() RxJS, jugé peu
// fiable derrière le service worker en usage réel — voir issue #29) : c'est la logique la plus
// subtile ajoutée pendant V3, la plus rentable à couvrir. navigator.onLine vaut true par défaut
// dans jsdom, donc refresh() emprunte ici le chemin réseau (pas le repli immédiat hors-ligne).
describe('AuthService.refresh()', () => {
  let service: AuthService;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    localStorage.clear();
    vi.useFakeTimers();
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(AuthService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
    vi.useRealTimers();
    localStorage.clear();
  });

  it('resolves from the server response when it arrives before the fallback timeout', () => {
    const results: (Session | null)[] = [];
    service.refresh().subscribe((s) => results.push(s));

    httpMock.expectOne('/api/auth/me').flush({ username: 'alice' });

    expect(results).toEqual([{ username: 'alice' }]);
  });

  it('falls back to the last known session when the request never resolves', async () => {
    localStorage.setItem(LAST_KNOWN_SESSION_KEY, JSON.stringify({ username: 'cached' }));

    const results: (Session | null)[] = [];
    service.refresh().subscribe((s) => results.push(s));

    httpMock.expectOne('/api/auth/me'); // reste en attente, jamais flush()
    await vi.advanceTimersByTimeAsync(REQUEST_TIMEOUT_MS);

    expect(results).toEqual([{ username: 'cached' }]);
  });

  // Le repli, une fois déclenché, désabonne la requête HTTP sous-jacente (teardown de
  // l'Observable) — une réponse tardive ne peut donc plus jamais atteindre l'abonné, quel que
  // soit son contenu.
  it('cancels the in-flight request once the fallback timeout commits', async () => {
    localStorage.setItem(LAST_KNOWN_SESSION_KEY, JSON.stringify({ username: 'cached' }));

    const results: (Session | null)[] = [];
    service.refresh().subscribe((s) => results.push(s));

    const req = httpMock.expectOne('/api/auth/me');
    await vi.advanceTimersByTimeAsync(REQUEST_TIMEOUT_MS);

    expect(req.cancelled).toBe(true);
    expect(results).toEqual([{ username: 'cached' }]);
  });

  it('returns null with no last-known session when neither the request nor a cache exist', async () => {
    const results: (Session | null)[] = [];
    service.refresh().subscribe((s) => results.push(s));

    httpMock.expectOne('/api/auth/me');
    await vi.advanceTimersByTimeAsync(REQUEST_TIMEOUT_MS);

    expect(results).toEqual([null]);
  });
});

import { Injectable, signal } from '@angular/core';

const STORAGE_KEY = 'rphotoalbum:manualOfflineMode';

// State local to this device (not synced to pCloud) — same pattern as COLLAPSED_STORAGE_KEY
// in albums.component.ts.
function loadManualOfflineMode(): boolean {
  try {
    return localStorage.getItem(STORAGE_KEY) === 'true';
  } catch {
    return false;
  }
}

// Manual "offline mode" toggle (issue #29): rather than relying solely on automatic detection
// (navigator.onLine, network delays) — which several rounds of real-world testing have shown
// can be slow or unreliable depending on the device — lets the user switch explicitly BEFORE
// losing connectivity (before a flight, a dead zone...). Once enabled,
// AuthService/AlbumsComponent/AlbumDetailComponent jump straight to the offline fallback, with
// no network attempt or waiting delay at all.
@Injectable({ providedIn: 'root' })
export class OfflineModeService {
  readonly manualOfflineMode = signal(loadManualOfflineMode());

  // Reactive suggestion (issue #29): if the user has NOT enabled offline mode but a critical
  // network call had to fall back (delay elapsed with no response), suggests switching rather
  // than staying at the mercy of the same wait on every new attempt.
  readonly suggestSwitch = signal(false);

  set(value: boolean): void {
    this.manualOfflineMode.set(value);
    if (value) {
      this.suggestSwitch.set(false);
    }
    try {
      localStorage.setItem(STORAGE_KEY, String(value));
    } catch {
      // localStorage quota exceeded or private browsing — not a blocking issue.
    }
  }

  toggle(): void {
    this.set(!this.manualOfflineMode());
  }

  // Signals an actual network failure (delay elapsed) — see AuthService.refresh(),
  // AlbumsComponent.load(), AlbumDetailComponent.load().
  markUnreachable(): void {
    if (!this.manualOfflineMode()) {
      this.suggestSwitch.set(true);
    }
  }

  dismissSuggestion(): void {
    this.suggestSwitch.set(false);
  }
}

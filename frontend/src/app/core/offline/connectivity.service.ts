import { Injectable, NgZone, inject, signal } from '@angular/core';

// Shared signal wired to the browser's online/offline events — used to switch the album
// thumbnail display over to the local offline cache (see OfflineAlbumService, issue #29).
// navigator.onLine can give a false positive ("online" while the network is actually
// unreachable, e.g. a captive portal) but never a reliable false negative — sufficient here
// since a false positive is caught by the normal failure of network requests.
@Injectable({ providedIn: 'root' })
export class ConnectivityService {
  private readonly ngZone = inject(NgZone);

  readonly online = signal(navigator.onLine);

  constructor() {
    window.addEventListener('online', () => this.ngZone.run(() => this.online.set(true)));
    window.addEventListener('offline', () => this.ngZone.run(() => this.online.set(false)));
  }
}

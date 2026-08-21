import { Injectable, NgZone, inject, signal } from '@angular/core';

// Signal partagé connecté aux événements online/offline du navigateur — utilisé pour basculer
// l'affichage des miniatures d'album vers le cache local hors-ligne (voir OfflineAlbumService,
// issue #29). navigator.onLine peut donner un faux positif ("en ligne" alors que le réseau est en
// réalité inaccessible, ex. portail captif) mais jamais de faux négatif fiable — suffisant ici
// puisqu'un faux positif se rattrape par l'échec normal des requêtes réseau.
@Injectable({ providedIn: 'root' })
export class ConnectivityService {
  private readonly ngZone = inject(NgZone);

  readonly online = signal(navigator.onLine);

  constructor() {
    window.addEventListener('online', () => this.ngZone.run(() => this.online.set(true)));
    window.addEventListener('offline', () => this.ngZone.run(() => this.online.set(false)));
  }
}

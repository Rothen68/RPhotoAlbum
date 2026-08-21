import { Injectable, signal } from '@angular/core';

const STORAGE_KEY = 'rphotoalbum:manualOfflineMode';

// État local à cet appareil (pas synchronisé sur pCloud) — même pattern que
// COLLAPSED_STORAGE_KEY dans albums.component.ts.
function loadManualOfflineMode(): boolean {
  try {
    return localStorage.getItem(STORAGE_KEY) === 'true';
  } catch {
    return false;
  }
}

// Bascule manuelle "mode hors-ligne" (issue #29) : plutôt que de dépendre uniquement de la
// détection automatique (navigator.onLine, délais réseau) — dont plusieurs rounds de test en
// conditions réelles ont montré qu'elle peut être lente ou peu fiable selon l'appareil — permet
// à l'utilisateur de basculer explicitement AVANT de perdre la connexion (avant un vol, une zone
// blanche...). Une fois activée, AuthService/AlbumsComponent/AlbumDetailComponent sautent
// directement au repli hors-ligne, sans la moindre tentative réseau ni délai d'attente.
@Injectable({ providedIn: 'root' })
export class OfflineModeService {
  readonly manualOfflineMode = signal(loadManualOfflineMode());

  // Suggestion réactive (issue #29) : si l'utilisateur n'a PAS activé le mode hors-ligne mais
  // qu'un appel réseau critique a dû recourir à son repli (délai écoulé sans réponse), propose
  // de basculer plutôt que de rester à la merci de la même attente à chaque nouvelle tentative.
  readonly suggestSwitch = signal(false);

  set(value: boolean): void {
    this.manualOfflineMode.set(value);
    if (value) {
      this.suggestSwitch.set(false);
    }
    try {
      localStorage.setItem(STORAGE_KEY, String(value));
    } catch {
      // Quota localStorage dépassé ou navigation privée — pas bloquant.
    }
  }

  toggle(): void {
    this.set(!this.manualOfflineMode());
  }

  // Signale un échec réseau réel (délai écoulé) — voir AuthService.refresh(),
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

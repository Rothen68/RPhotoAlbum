import { Component, inject } from '@angular/core';
import { OfflineModeService } from '../../core/offline/offline-mode.service';

// Bandeau global (monté une fois dans App, visible sur toutes les routes y compris /login et
// /albums/:id qui ne passent pas par ShellComponent) — voir OfflineModeService pour le
// raisonnement (bascule manuelle plutôt que détection automatique seule, issue #29).
@Component({
  selector: 'app-offline-banner',
  standalone: true,
  templateUrl: './offline-banner.component.html',
  styleUrl: './offline-banner.component.scss',
})
export class OfflineBannerComponent {
  protected readonly offlineMode = inject(OfflineModeService);

  disable(): void {
    this.offlineMode.set(false);
  }

  enable(): void {
    this.offlineMode.set(true);
  }

  dismiss(): void {
    this.offlineMode.dismissSuggestion();
  }
}

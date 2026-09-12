import { Component, inject } from '@angular/core';
import { OfflineModeService } from '../../core/offline/offline-mode.service';

// Global banner (mounted once in App, visible on all routes including /login and
// /albums/:id which don't go through ShellComponent) — see OfflineModeService for the
// reasoning (manual toggle rather than automatic detection alone, issue #29).
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

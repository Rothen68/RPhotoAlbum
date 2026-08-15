import { Component, OnInit, inject, signal } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { AuthService } from '../../core/auth/auth.service';
import { PCloudService, PCloudStatus } from '../../core/pcloud/pcloud.service';

@Component({
  selector: 'app-albums',
  standalone: true,
  templateUrl: './albums.component.html',
})
export class AlbumsComponent implements OnInit {
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly pcloud = inject(PCloudService);
  protected readonly auth = inject(AuthService);

  protected readonly pcloudStatus = signal<PCloudStatus | null>(null);
  protected readonly pcloudMessage = signal<string | null>(null);
  protected readonly connectUrl = this.pcloud.connectUrl;

  ngOnInit(): void {
    const result = this.route.snapshot.queryParamMap.get('pcloud');
    if (result === 'connected') {
      this.pcloudMessage.set('Compte pCloud connecté.');
    } else if (result === 'error') {
      this.pcloudMessage.set("Échec de la connexion à pCloud.");
    }

    this.refreshPCloudStatus();
  }

  refreshPCloudStatus(): void {
    this.pcloud.status().subscribe((status) => this.pcloudStatus.set(status));
  }

  disconnectPCloud(): void {
    this.pcloud.disconnect().subscribe(() => this.refreshPCloudStatus());
  }

  logout(): void {
    this.auth.logout().subscribe(() => this.router.navigateByUrl('/login'));
  }
}

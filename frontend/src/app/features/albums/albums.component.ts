import { Component, inject } from '@angular/core';
import { Router } from '@angular/router';
import { AuthService } from '../../core/auth/auth.service';

@Component({
  selector: 'app-albums',
  standalone: true,
  templateUrl: './albums.component.html',
})
export class AlbumsComponent {
  private readonly router = inject(Router);
  protected readonly auth = inject(AuthService);

  logout(): void {
    this.auth.logout().subscribe(() => this.router.navigateByUrl('/login'));
  }
}

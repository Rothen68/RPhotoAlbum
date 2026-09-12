import { Routes } from '@angular/router';
import { authGuard } from './core/auth/auth.guard';
import { GalleryComponent } from './features/gallery/gallery.component';
import { ShellComponent } from './shell/shell.component';

// Lazy loading per route (issue #19) — only Gallery (first screen) and Shell (lightweight
// wrapper, tab bar) stay loaded upfront. The rest (Login, Config, Album Detail, Albums) doesn't
// need to be present on first render.
export const routes: Routes = [
  {
    path: 'login',
    loadComponent: () => import('./features/login/login.component').then((m) => m.LoginComponent),
  },
  {
    path: 'config',
    loadComponent: () => import('./features/config/config.component').then((m) => m.ConfigComponent),
    canActivate: [authGuard],
  },
  {
    path: 'albums/:id',
    loadComponent: () => import('./features/album-detail/album-detail.component').then((m) => m.AlbumDetailComponent),
    canActivate: [authGuard],
  },
  {
    path: '',
    component: ShellComponent,
    canActivate: [authGuard],
    children: [
      { path: '', component: GalleryComponent },
      {
        path: 'albums',
        loadComponent: () => import('./features/albums/albums.component').then((m) => m.AlbumsComponent),
      },
    ],
  },
];

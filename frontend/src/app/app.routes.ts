import { Routes } from '@angular/router';
import { authGuard } from './core/auth/auth.guard';
import { GalleryComponent } from './features/gallery/gallery.component';
import { ShellComponent } from './shell/shell.component';

// Chargement paresseux par route (issue #19) — seuls Gallery (premier écran) et Shell (wrapper
// léger, tab bar) restent chargés d'emblée. Le reste (Login, Config, Album Detail, Albums) n'a
// pas besoin d'être présent au premier affichage.
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

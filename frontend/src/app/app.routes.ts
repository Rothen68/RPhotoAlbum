import { Routes } from '@angular/router';
import { authGuard } from './core/auth/auth.guard';
import { AlbumDetailComponent } from './features/album-detail/album-detail.component';
import { AlbumsComponent } from './features/albums/albums.component';
import { ConfigComponent } from './features/config/config.component';
import { GalleryComponent } from './features/gallery/gallery.component';
import { LoginComponent } from './features/login/login.component';
import { ShellComponent } from './shell/shell.component';

export const routes: Routes = [
  { path: 'login', component: LoginComponent },
  { path: 'config', component: ConfigComponent, canActivate: [authGuard] },
  { path: 'albums/:id', component: AlbumDetailComponent, canActivate: [authGuard] },
  {
    path: '',
    component: ShellComponent,
    canActivate: [authGuard],
    children: [
      { path: '', component: GalleryComponent },
      { path: 'albums', component: AlbumsComponent },
    ],
  },
];

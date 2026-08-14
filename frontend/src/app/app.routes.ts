import { Routes } from '@angular/router';
import { authGuard } from './core/auth/auth.guard';
import { AlbumsComponent } from './features/albums/albums.component';
import { LoginComponent } from './features/login/login.component';

export const routes: Routes = [
  { path: 'login', component: LoginComponent },
  { path: '', component: AlbumsComponent, canActivate: [authGuard] },
];

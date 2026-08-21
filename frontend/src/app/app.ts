import { Component } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { OfflineBannerComponent } from './shared/offline-banner/offline-banner.component';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet, OfflineBannerComponent],
  templateUrl: './app.html',
  styleUrl: './app.scss'
})
export class App {}

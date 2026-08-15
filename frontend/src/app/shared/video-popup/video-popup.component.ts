import { Component, EventEmitter, Input, Output } from '@angular/core';

@Component({
  selector: 'app-video-popup',
  standalone: true,
  templateUrl: './video-popup.component.html',
  styleUrl: './video-popup.component.scss',
})
export class VideoPopupComponent {
  @Input({ required: true }) src!: string;
  @Output() closed = new EventEmitter<void>();
}

import { DatePipe } from '@angular/common';
import { Component, EventEmitter, HostListener, Input, OnDestroy, OnInit, Output, computed, signal } from '@angular/core';
import { isRawFileName } from '../raw-format';

export interface MediaViewerItem {
  fileId: number;
  mediaType: 'image' | 'video';
  // Facultatifs : n'affiche que ce qui est réellement disponible (voir issue #22) — un média pas
  // encore traité par les jobs EXIF/géo n'a ni dateTaken ni localisation, un média sans nom connu
  // (cas hypothétique) n'affiche pas non plus le badge RAW.
  name?: string | null;
  dateTaken?: string | null;
  country?: string | null;
  region?: string | null;
  city?: string | null;
}

const SWIPE_THRESHOLD_PX = 50;

// Visionneuse plein écran généralisée (image + vidéo, navigation suivant/précédent) —
// remplace VideoPopupComponent (vidéo seule). Réutilisée par Gallery et Album Detail.
@Component({
  selector: 'app-media-viewer',
  standalone: true,
  imports: [DatePipe],
  templateUrl: './media-viewer.component.html',
  styleUrl: './media-viewer.component.scss',
})
export class MediaViewerComponent implements OnInit, OnDestroy {
  @Input({ required: true }) items!: MediaViewerItem[];
  @Input({ required: true }) startIndex!: number;
  // Miniature vidéo (poster) — petite taille, comme dans la grille.
  @Input({ required: true }) posterUrl!: (fileId: number) => string;
  // Affichage plein écran d'une image : une grande miniature non recadrée générée par
  // pCloud, pas le fichier brut — les formats RAW (ex. .CR2) ne sont pas décodables par
  // un <img>, et pCloud peut renvoyer une erreur sur le lien direct pour ces fichiers.
  @Input({ required: true }) imageUrl!: (fileId: number) => string;
  @Input({ required: true }) streamUrl!: (fileId: number) => string;
  @Input({ required: true }) downloadUrl!: (fileId: number) => string;
  @Output() closed = new EventEmitter<void>();

  protected readonly index = signal(0);
  protected readonly current = computed(() => this.items[this.index()]);
  protected readonly hasPrevious = computed(() => this.index() > 0);
  protected readonly hasNext = computed(() => this.index() < this.items.length - 1);
  protected readonly isRaw = computed(() => isRawFileName(this.current()?.name));
  // Chaîne "ville, région, pays" ne gardant que les segments réellement connus — voir issue #22.
  protected readonly locationLabel = computed(() => {
    const item = this.current();
    const parts = [item?.city, item?.region, item?.country].filter((p): p is string => !!p);
    return parts.length > 0 ? parts.join(', ') : null;
  });

  private touchStartX: number | null = null;
  private previousBodyOverflow = '';

  ngOnInit(): void {
    this.index.set(this.startIndex);
    // L'overlay est en `position: fixed` mais ça ne suffit pas à empêcher le scroll de la page
    // sous-jacente (surtout sur mobile) — voir issue #2.
    this.previousBodyOverflow = document.body.style.overflow;
    document.body.style.overflow = 'hidden';
  }

  ngOnDestroy(): void {
    document.body.style.overflow = this.previousBodyOverflow;
  }

  @HostListener('window:keydown', ['$event'])
  onKeydown(event: KeyboardEvent): void {
    if (event.key === 'Escape') {
      this.closed.emit();
    } else if (event.key === 'ArrowLeft') {
      this.previous();
    } else if (event.key === 'ArrowRight') {
      this.next();
    }
  }

  previous(): void {
    if (this.hasPrevious()) {
      this.index.update((i) => i - 1);
    }
  }

  next(): void {
    if (this.hasNext()) {
      this.index.update((i) => i + 1);
    }
  }

  onTouchStart(event: TouchEvent): void {
    this.touchStartX = event.touches[0]?.clientX ?? null;
  }

  onTouchEnd(event: TouchEvent): void {
    if (this.touchStartX === null) {
      return;
    }

    const endX = event.changedTouches[0]?.clientX ?? this.touchStartX;
    const delta = endX - this.touchStartX;
    this.touchStartX = null;

    if (delta > SWIPE_THRESHOLD_PX) {
      this.previous();
    } else if (delta < -SWIPE_THRESHOLD_PX) {
      this.next();
    }
  }
}

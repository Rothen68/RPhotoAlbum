import { CdkVirtualScrollViewport, VIRTUAL_SCROLL_STRATEGY } from '@angular/cdk/scrolling';
import { Directive, EventEmitter, OnDestroy, Output } from '@angular/core';
import { AlbumItem } from '../../core/albums/album.service';
import { PrecomputedVirtualScrollStrategy } from '../../shared/virtual-scroll/precomputed-virtual-scroll-strategy';
import { AlbumRow } from './album-layout';

// Estimation utilisée pour un bloc texte tant que sa hauteur réelle n'a pas encore été mesurée
// (issue #30) — un contenu Markdown n'a pas de hauteur connaissable à l'avance, contrairement à
// une image (ratio EXIF). AlbumDetailComponent mesure la hauteur réelle une fois chaque bloc
// rendu (MeasureHeightDirective, ResizeObserver) et corrige l'entrée correspondante — cette
// estimation ne sert donc que pour le tout premier rendu d'une rangée texte donnée, avant
// correction (léger réajustement visuel possible à ce moment-là, imperceptible en défilement
// normal grâce à la zone tampon de la virtualisation qui rend les rangées en avance).
export const TEXT_BLOCK_HEIGHT_ESTIMATE_PX = 160;

// Ratio largeur/hauteur utilisé tant que les dimensions réelles de l'image ne sont pas encore
// connues (média pas encore traité par le job EXIF, voir issue #20/MediaExifService) — purement
// une estimation de mise en page ; se corrige de lui-même dès que l'EXIF est disponible (nouveau
// chargement de l'album). 4:3 est un compromis neutre, ni portrait ni très large.
const FALLBACK_ASPECT_RATIO = 4 / 3;

const ROW_GAP_PX = 8; // .row { gap: 0.5rem }

// Hauteur d'une rangée en vue virtualisée (lecture seule, pas de contrôles d'édition en
// superposition) :
// - Bloc texte : hauteur réelle si déjà mesurée (issue #30, voir measuredTextHeight), sinon
//   TEXT_BLOCK_HEIGHT_ESTIMATE_PX en attendant le premier rendu.
// - Rangée groupée (2-3 médias) : aspect-ratio 1:1 forcé (voir .block.grouped .media-block en
//   CSS) — la hauteur ne dépend donc que de la largeur de colonne, jamais du contenu.
// - Média seul (RowSpan=1) : garde son ratio naturel (comportement existant) — utilise les
//   dimensions réelles (Width/Height) si connues, sinon l'estimation FALLBACK_ASPECT_RATIO.
export function computeRowHeight(row: AlbumRow, containerWidth: number, measuredTextHeight?: number): number {
  const first = row.items[0];
  if (first.type === 'text') {
    return measuredTextHeight ?? TEXT_BLOCK_HEIGHT_ESTIMATE_PX;
  }

  const cols = row.items.length;
  if (cols > 1) {
    return (containerWidth - ROW_GAP_PX * (cols - 1)) / cols;
  }

  const ratio = aspectRatioOf(first);
  return containerWidth / ratio;
}

function aspectRatioOf(item: AlbumItem): number {
  if (item.width && item.height) {
    return item.width / item.height;
  }
  return FALLBACK_ASPECT_RATIO;
}

@Directive({
  selector: 'cdk-virtual-scroll-viewport[appAlbumVirtualScroll]',
  standalone: true,
  providers: [{ provide: VIRTUAL_SCROLL_STRATEGY, useExisting: AlbumVirtualScrollDirective }],
})
export class AlbumVirtualScrollDirective extends PrecomputedVirtualScrollStrategy implements OnDestroy {
  // CDK attache cette stratégie à son viewport lors de la propre initialisation du composant
  // <cdk-virtual-scroll-viewport> — un ordre pas garanti par rapport à l'effect() du composant
  // parent qui pousse les hauteurs (voir AlbumDetailComponent) : constaté en pratique, au retour
  // en vue de base depuis le mode Edit (viewport recréé), l'effect pouvait se déclencher AVANT
  // cet attach(), poussant des hauteurs dans le vide (viewport encore interne à null côté
  // stratégie) sans jamais les réappliquer ensuite — écran vide malgré des données correctes.
  // Ce signal permet au composant de repousser les hauteurs à coup sûr JUSTE APRÈS l'attache
  // réelle, plutôt que de dépendre d'un ordre d'exécution implicite.
  @Output() readonly attached = new EventEmitter<void>();

  override attach(viewport: CdkVirtualScrollViewport): void {
    super.attach(viewport);
    this.attached.emit();
  }

  ngOnDestroy(): void {
    this.detach();
  }
}

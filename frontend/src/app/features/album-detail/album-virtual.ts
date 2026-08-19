import { CdkVirtualScrollViewport, VIRTUAL_SCROLL_STRATEGY } from '@angular/cdk/scrolling';
import { Directive, EventEmitter, OnDestroy, Output } from '@angular/core';
import { AlbumItem } from '../../core/albums/album.service';
import { PrecomputedVirtualScrollStrategy } from '../../shared/virtual-scroll/precomputed-virtual-scroll-strategy';
import { AlbumRow } from './album-layout';

// Hauteur réservée à un bloc texte en vue virtualisée (lecture seule) — fixe plutôt que mesurée,
// pour rester dans le même schéma "hauteurs connues à l'avance" que le reste de la stratégie
// (voir PrecomputedVirtualScrollStrategy) : un contenu Markdown a une hauteur de rendu
// arbitraire, impossible à connaître sans le rendre d'abord. Le bloc lui-même est contraint en
// CSS à cette même hauteur avec un défilement interne (.text-block.virtualized) si le contenu la
// dépasse — un dépassement non contraint chevaucherait la rangée suivante, positionnée à
// l'offset cumulé fixe suivant.
export const TEXT_BLOCK_HEIGHT_PX = 160;

// Ratio largeur/hauteur utilisé tant que les dimensions réelles de l'image ne sont pas encore
// connues (média pas encore traité par le job EXIF, voir issue #20/MediaExifService) — purement
// une estimation de mise en page ; se corrige de lui-même dès que l'EXIF est disponible (nouveau
// chargement de l'album). 4:3 est un compromis neutre, ni portrait ni très large.
const FALLBACK_ASPECT_RATIO = 4 / 3;

const ROW_GAP_PX = 8; // .row { gap: 0.5rem }

// Hauteur d'une rangée en vue virtualisée (lecture seule, pas de contrôles d'édition en
// superposition) — connue à l'avance à partir de la seule largeur du conteneur :
// - Bloc texte : hauteur fixe (voir TEXT_BLOCK_HEIGHT_PX).
// - Rangée groupée (2-3 médias) : aspect-ratio 1:1 forcé (voir .block.grouped .media-block en
//   CSS) — la hauteur ne dépend donc que de la largeur de colonne, jamais du contenu.
// - Média seul (RowSpan=1) : garde son ratio naturel (comportement existant) — utilise les
//   dimensions réelles (Width/Height) si connues, sinon l'estimation FALLBACK_ASPECT_RATIO.
export function computeRowHeight(row: AlbumRow, containerWidth: number): number {
  const first = row.items[0];
  if (first.type === 'text') {
    return TEXT_BLOCK_HEIGHT_PX;
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

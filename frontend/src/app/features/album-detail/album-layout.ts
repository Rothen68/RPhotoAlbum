import { AlbumItem } from '../../core/albums/album.service';

const MAX_ROW_SPAN = 3;

// Regroupe la liste à plat en rangées d'affichage : un item média isolé ou un bloc texte
// forme une rangée à lui seul, un groupe de 2/3 médias consécutifs (RowSpan porté par
// l'ancre, le premier item du groupe) forme une rangée à N colonnes. Chaque rangée devient
// une mini-grille indépendante (grid-template-columns: repeat(N, 1fr)) — un unique grid
// partagé pour tout le fil ne peut pas donner à une rangée de 2 photos une largeur 50/50 et à
// une rangée de 3 une largeur 33/33/33 avec des colonnes de largeur uniforme.
export interface AlbumRow {
  items: AlbumItem[];
  startIndex: number;
  canGrow: boolean;
}

export function groupIntoRows(items: AlbumItem[]): AlbumRow[] {
  const rows: AlbumRow[] = [];
  let i = 0;

  while (i < items.length) {
    const item = items[i];
    if (item.type !== 'media') {
      rows.push({ items: [item], startIndex: i, canGrow: false });
      i++;
      continue;
    }

    let available = 1;
    while (i + available < items.length && items[i + available].type === 'media' && available < MAX_ROW_SPAN) {
      available++;
    }

    const span = Math.min(Math.max(1, item.rowSpan || 1), available);
    const canGrow = span < MAX_ROW_SPAN && items[i + span]?.type === 'media';
    rows.push({ items: items.slice(i, i + span), startIndex: i, canGrow });
    i += span;
  }

  return rows;
}

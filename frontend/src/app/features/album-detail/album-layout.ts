import { AlbumItem } from '../../core/albums/album.service';

const MAX_ROW_SPAN = 3;

// Groups the flat list into display rows: an isolated media item or a text block
// forms a row on its own, a group of 2/3 consecutive media (RowSpan carried by
// the anchor, the group's first item) forms an N-column row. Each row becomes
// an independent mini-grid (grid-template-columns: repeat(N, 1fr)) — a single grid
// shared across the whole feed can't give a row of 2 photos a 50/50 width and
// a row of 3 a 33/33/33 width with uniform-width columns.
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

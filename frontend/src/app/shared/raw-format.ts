// RAW detection by file extension — no format data is stored on the backend side (see
// GitHub issue #24), so it's computed on the fly from the name already available everywhere
// (MediaItem.name, AlbumItem.source/albumCopy.name). List is not exhaustive but covers the
// main manufacturers; extend as needed rather than introducing dedicated storage as long as
// this stays a simple display matter (badge).
const RAW_EXTENSIONS = new Set([
  'cr2', 'cr3', // Canon
  'nef', 'nrw', // Nikon
  'arw', 'srf', 'sr2', // Sony
  'raf', // Fujifilm
  'orf', // Olympus
  'rw2', // Panasonic
  'pef', // Pentax
  'srw', // Samsung
  'dng', // Adobe / generic
  'raw', '3fr', 'erf', 'mef', 'mrw', 'x3f',
]);

export function isRawFileName(name: string | null | undefined): boolean {
  if (!name) {
    return false;
  }

  const dot = name.lastIndexOf('.');
  return dot >= 0 && RAW_EXTENSIONS.has(name.slice(dot + 1).toLowerCase());
}

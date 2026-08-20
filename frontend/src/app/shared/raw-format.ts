// Détection RAW par extension de fichier — aucune donnée de format n'est stockée côté backend
// (voir issue GitHub #24), donc calculée à la volée à partir du nom déjà disponible partout
// (MediaItem.name, AlbumItem.source/albumCopy.name). Liste non exhaustive mais couvre les
// principaux fabricants ; à étendre si besoin plutôt que d'introduire un stockage dédié tant
// que ça reste une simple question d'affichage (badge).
const RAW_EXTENSIONS = new Set([
  'cr2', 'cr3', // Canon
  'nef', 'nrw', // Nikon
  'arw', 'srf', 'sr2', // Sony
  'raf', // Fujifilm
  'orf', // Olympus
  'rw2', // Panasonic
  'pef', // Pentax
  'srw', // Samsung
  'dng', // Adobe / générique
  'raw', '3fr', 'erf', 'mef', 'mrw', 'x3f',
]);

export function isRawFileName(name: string | null | undefined): boolean {
  if (!name) {
    return false;
  }

  const dot = name.lastIndexOf('.');
  return dot >= 0 && RAW_EXTENSIONS.has(name.slice(dot + 1).toLowerCase());
}

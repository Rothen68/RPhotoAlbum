using System.Runtime.CompilerServices;

// Donne au projet de tests l'accès aux membres `internal` (ex. AlbumService.NormalizeRowSpans) —
// évite de rendre publique une logique interne à la classe uniquement pour la rendre testable.
[assembly: InternalsVisibleTo("RPhotoAlbum.Api.Tests")]

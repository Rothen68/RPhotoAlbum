using System.Runtime.CompilerServices;

// Gives the test project access to `internal` members (e.g. AlbumService.NormalizeRowSpans) —
// avoids making logic internal to the class public just to make it testable.
[assembly: InternalsVisibleTo("RPhotoAlbum.Api.Tests")]

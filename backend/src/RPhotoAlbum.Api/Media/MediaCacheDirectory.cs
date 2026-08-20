namespace RPhotoAlbum.Api.Media;

// Chemin du cache disque des miniatures (issue #26), calculé une seule fois dans Program.cs
// (même logique dev/prod que le dossier des clés Data Protection) et injecté tel quel — pas de
// dépendance à IWebHostEnvironment ici, pour rester trivial à construire dans les tests
// (new MediaCacheDirectory(tempDir)).
public record MediaCacheDirectory(string Path);

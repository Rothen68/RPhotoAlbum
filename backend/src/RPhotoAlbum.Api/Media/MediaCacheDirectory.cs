namespace RPhotoAlbum.Api.Media;

// Path to the thumbnail disk cache (issue #26), computed once in Program.cs (same dev/prod
// logic as the Data Protection keys folder) and injected as-is — no dependency on
// IWebHostEnvironment here, so it stays trivial to construct in tests
// (new MediaCacheDirectory(tempDir)).
public record MediaCacheDirectory(string Path);

namespace RPhotoAlbum.Api.Media;

// See GitHub issue #26 — thumbnail disk cache, size-bounded LRU eviction (no TTL:
// a pCloud fileId references immutable content).
public class MediaCacheOptions
{
    public int MaxSizeMb { get; set; } = 1024;
    public int EvictionIntervalMinutes { get; set; } = 30;
}

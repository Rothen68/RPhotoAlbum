using Microsoft.Extensions.Logging.Abstractions;
using RPhotoAlbum.Api.Media;
using RPhotoAlbum.Api.Tests.Fakes;

namespace RPhotoAlbum.Api.Tests.Media;

// Couvre MediaThumbnailCacheService (issue #26) — répertoire temporaire réel (pas de provider
// in-memory pour du vrai I/O disque), nettoyé via IDisposable.
public sealed class MediaThumbnailCacheServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FakePCloudClient _client = new();
    private readonly MediaThumbnailCacheService _service;

    public MediaThumbnailCacheServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "rphotoalbum-tests-" + Guid.NewGuid().ToString("N"));
        _service = new MediaThumbnailCacheService(
            new MediaCacheDirectory(_tempDir), _client, NullLogger<MediaThumbnailCacheService>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task CacheMiss_FetchesFromClientAndWritesToDisk()
    {
        var expected = new byte[] { 1, 2, 3, 4 };
        _client.SetThumbnail(42, 300, 300, true, expected);

        var (bytes, contentType) = await _service.GetAsync(42, 300, 300, true, CancellationToken.None);

        Assert.Equal(expected, bytes);
        Assert.Equal("image/jpeg", contentType);
        Assert.True(File.Exists(Path.Combine(_tempDir, "42_300x300_c.bin")));
        Assert.Equal(1, _client.ThumbnailCallCounts["42_300x300_c"]);
    }

    [Fact]
    public async Task CacheHit_DoesNotCallClientAgain()
    {
        var expected = new byte[] { 5, 6, 7 };
        _client.SetThumbnail(7, 300, 300, true, expected);

        var first = await _service.GetAsync(7, 300, 300, true, CancellationToken.None);
        var second = await _service.GetAsync(7, 300, 300, true, CancellationToken.None);

        Assert.Equal(expected, first.Bytes);
        Assert.Equal(expected, second.Bytes);
        Assert.Equal(1, _client.ThumbnailCallCounts["7_300x300_c"]);
    }

    [Fact]
    public async Task DifferentDimensions_AreDistinctCacheEntries()
    {
        _client.SetThumbnail(9, 300, 300, true, [1]);
        _client.SetThumbnail(9, 800, 800, false, [2]);

        await _service.GetAsync(9, 300, 300, true, CancellationToken.None);
        await _service.GetAsync(9, 800, 800, false, CancellationToken.None);

        Assert.Equal(1, _client.ThumbnailCallCounts["9_300x300_c"]);
        Assert.Equal(1, _client.ThumbnailCallCounts["9_800x800_n"]);
    }

    [Fact]
    public async Task CacheHit_TouchesLastWriteTime()
    {
        _client.SetThumbnail(3, 300, 300, true, [9]);
        await _service.GetAsync(3, 300, 300, true, CancellationToken.None);

        var path = Path.Combine(_tempDir, "3_300x300_c.bin");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-1));

        await _service.GetAsync(3, 300, 300, true, CancellationToken.None);

        Assert.True(File.GetLastWriteTimeUtc(path) > DateTime.UtcNow.AddMinutes(-1));
    }
}

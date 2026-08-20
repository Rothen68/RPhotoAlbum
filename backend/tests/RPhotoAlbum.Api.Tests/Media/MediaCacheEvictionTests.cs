using RPhotoAlbum.Api.Media;

namespace RPhotoAlbum.Api.Tests.Media;

// Couvre MediaCacheEvictionBackgroundService.Evict (issue #26) directement — logique pure,
// répertoire temporaire réel, pas besoin de lancer le BackgroundService complet.
public sealed class MediaCacheEvictionTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "rphotoalbum-tests-" + Guid.NewGuid().ToString("N"));

    public MediaCacheEvictionTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private void CreateFile(string name, int sizeBytes, DateTime lastWriteUtc)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllBytes(path, new byte[sizeBytes]);
        File.SetLastWriteTimeUtc(path, lastWriteUtc);
    }

    [Fact]
    public void WhenUnderCap_DeletesNothing()
    {
        CreateFile("1_100x100_c.bin", 100, DateTime.UtcNow);
        CreateFile("2_100x100_c.bin", 100, DateTime.UtcNow);

        MediaCacheEvictionBackgroundService.Evict(_tempDir, maxBytes: 1000);

        Assert.Equal(2, Directory.GetFiles(_tempDir, "*.bin").Length);
    }

    [Fact]
    public void WhenOverCap_DeletesOldestFilesFirstUntilUnderCap()
    {
        var now = DateTime.UtcNow;
        CreateFile("oldest.bin", 100, now.AddHours(-3));
        CreateFile("middle.bin", 100, now.AddHours(-2));
        CreateFile("newest.bin", 100, now.AddHours(-1));

        // Plafond de 150 octets pour 300 au total : doit supprimer le plus ancien (et seulement lui,
        // 300 - 100 = 200 > 150, il faut aussi supprimer "middle" pour repasser sous 150).
        MediaCacheEvictionBackgroundService.Evict(_tempDir, maxBytes: 150);

        var remaining = Directory.GetFiles(_tempDir, "*.bin").Select(Path.GetFileName).ToHashSet();
        Assert.DoesNotContain("oldest.bin", remaining);
        Assert.DoesNotContain("middle.bin", remaining);
        Assert.Contains("newest.bin", remaining);
    }

    [Fact]
    public void NonexistentDirectory_DoesNothing()
    {
        var missing = Path.Combine(_tempDir, "does-not-exist");

        var exception = Record.Exception(() => MediaCacheEvictionBackgroundService.Evict(missing, maxBytes: 10));

        Assert.Null(exception);
    }
}

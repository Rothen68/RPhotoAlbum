using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using RPhotoAlbum.Api.Data;
using RPhotoAlbum.Api.Media;
using RPhotoAlbum.Api.Models;
using RPhotoAlbum.Api.PCloud;
using RPhotoAlbum.Api.Tests.Fakes;

namespace RPhotoAlbum.Api.Tests.Media;

// SQLite en mémoire (pas le provider InMemory d'EF Core) : on veut le vrai comportement de
// contraintes (index unique sur PCloudFileId) qu'utilise MediaIndexService, que le provider
// InMemory n'applique pas fidèlement.
public sealed class MediaIndexServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly CacheDbContext _db;
    private readonly PCloudTokenStore _tokenStore;
    private readonly FakePCloudClient _client = new();

    public MediaIndexServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<CacheDbContext>()
            .UseSqlite(_connection)
            .Options;
        _db = new CacheDbContext(options);
        _db.Database.EnsureCreated();

        _tokenStore = new PCloudTokenStore(_db, new EphemeralDataProtectionProvider(), new MemoryCache(new MemoryCacheOptions()));
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private MediaIndexService CreateService() => new(_db, _client, _tokenStore, NullLogger<MediaIndexService>.Instance);

    private async Task ConnectAsync() => await _tokenStore.SaveAsync("api.pcloud.com", "fake-token");

    private async Task<SourceFolder> AddSourceFolderAsync(long pCloudFolderId, string label = "Photos")
    {
        var folder = new SourceFolder { PCloudFolderId = pCloudFolderId, Label = label, Path = "/" + label };
        _db.SourceFolders.Add(folder);
        await _db.SaveChangesAsync();
        return folder;
    }

    private static PCloudItem Image(long fileId, string name, string? modified = "Wed, 12 Jun 2013 12:15:41 +0000") =>
        new(name, false, fileId, null, 1024, 12345, "image/jpeg", modified, modified, false, "/" + name, null);

    private static PCloudItem Folder(string name, IEnumerable<PCloudItem> contents) =>
        new(name, true, null, 1, null, null, null, null, null, null, "/" + name, contents.ToList());

    private static PCloudFolderListing Listing(params PCloudItem[] items) =>
        new(0, null, new PCloudFolderMetadata("root", 1, "/", items.ToList()));

    [Fact]
    public async Task NotConnected_ReturnsEmptyResult_WithoutTouchingFolders()
    {
        await AddSourceFolderAsync(1);
        var result = await CreateService().ReindexAsync();

        Assert.Equal(0, result.Indexed);
        Assert.Equal(0, result.NewlyIndexed);
        Assert.Empty(result.FailedFolders);
        Assert.False(result.IsAlreadyRunning);
    }

    [Fact]
    public async Task NoSourceFolders_ReturnsEmptyResult()
    {
        await ConnectAsync();
        var result = await CreateService().ReindexAsync();

        Assert.Equal(0, result.Indexed);
        Assert.Equal(0, result.NewlyIndexed);
        Assert.Empty(result.FailedFolders);
    }

    [Fact]
    public async Task NewMedia_IsIndexedAndCountedAsNewlyIndexed()
    {
        await ConnectAsync();
        await AddSourceFolderAsync(100);
        _client.SetFolderListing(100, Listing(Image(1, "a.jpg"), Image(2, "b.jpg")));

        var result = await CreateService().ReindexAsync();

        Assert.Equal(2, result.Indexed);
        Assert.Equal(2, result.NewlyIndexed);
        Assert.Empty(result.FailedFolders);
        Assert.Equal(2, await _db.MediaIndex.CountAsync());
    }

    [Fact]
    public async Task ExistingMedia_ReconfirmedOnSecondPass_IsNotCountedAsNewlyIndexed()
    {
        await ConnectAsync();
        await AddSourceFolderAsync(100);
        _client.SetFolderListing(100, Listing(Image(1, "a.jpg"), Image(2, "b.jpg")));
        await CreateService().ReindexAsync();

        var result = await CreateService().ReindexAsync();

        Assert.Equal(2, result.Indexed);
        Assert.Equal(0, result.NewlyIndexed);
        Assert.Equal(2, await _db.MediaIndex.CountAsync());
    }

    [Fact]
    public async Task VanishedMedia_IsPurged_WhenNoFolderFailed()
    {
        await ConnectAsync();
        await AddSourceFolderAsync(100);
        _client.SetFolderListing(100, Listing(Image(1, "a.jpg"), Image(2, "b.jpg")));
        await CreateService().ReindexAsync();

        _client.SetFolderListing(100, Listing(Image(1, "a.jpg")));
        var result = await CreateService().ReindexAsync();

        Assert.Equal(1, result.Indexed);
        Assert.Equal(0, result.NewlyIndexed);
        Assert.Equal(1, await _db.MediaIndex.CountAsync());
        Assert.Equal(1, (await _db.MediaIndex.SingleAsync()).PCloudFileId);
    }

    [Fact]
    public async Task FailedFolder_SkipsPurgeAndKeepsExistingEntries()
    {
        await ConnectAsync();
        await AddSourceFolderAsync(100, "Photos");
        _client.SetFolderListing(100, Listing(Image(1, "a.jpg"), Image(2, "b.jpg")));
        await CreateService().ReindexAsync();

        _client.SetFolderFailure(100, new HttpRequestException("pCloud indisponible"));
        var result = await CreateService().ReindexAsync();

        Assert.Equal(0, result.Indexed);
        Assert.Equal(0, result.NewlyIndexed);
        Assert.Equal(["Photos"], result.FailedFolders);
        // Le dossier a échoué : la purge est sautée, les entrées précédemment indexées restent.
        Assert.Equal(2, await _db.MediaIndex.CountAsync());
    }

    [Fact]
    public async Task NonMediaContentType_IsIgnored()
    {
        await ConnectAsync();
        await AddSourceFolderAsync(100);
        var document = new PCloudItem("doc.pdf", false, 5, null, 100, 999, "application/pdf", null, null, false, "/doc.pdf", null);
        _client.SetFolderListing(100, Listing(Image(1, "a.jpg"), document));

        var result = await CreateService().ReindexAsync();

        Assert.Equal(1, result.Indexed);
        Assert.Equal(1, await _db.MediaIndex.CountAsync());
    }

    [Fact]
    public async Task NestedFolders_AreFlattenedRecursively()
    {
        await ConnectAsync();
        await AddSourceFolderAsync(100);
        _client.SetFolderListing(100, Listing(
            Image(1, "top.jpg"),
            Folder("sub", [Image(2, "nested.jpg"), Folder("subsub", [Image(3, "deep.jpg")])])));

        var result = await CreateService().ReindexAsync();

        Assert.Equal(3, result.Indexed);
        Assert.Equal(3, await _db.MediaIndex.CountAsync());
    }

    [Fact]
    public async Task ConcurrentReindex_SecondCallReturnsAlreadyRunning()
    {
        await ConnectAsync();
        await AddSourceFolderAsync(100);
        _client.SetFolderListing(100, Listing(Image(1, "a.jpg")));
        _client.Gate = new TaskCompletionSource<bool>();

        var service = CreateService();
        var firstCall = service.ReindexAsync();

        // Attend que le premier appel soit effectivement entré dans la section critique
        // (verrou pris, en train d'appeler ListFolderAsync) avant de tenter le second.
        await Task.Delay(50);
        var secondResult = await service.ReindexAsync();

        Assert.True(secondResult.IsAlreadyRunning);

        _client.Gate.SetResult(true);
        var firstResult = await firstCall;
        Assert.False(firstResult.IsAlreadyRunning);
        Assert.Equal(1, firstResult.Indexed);
    }
}

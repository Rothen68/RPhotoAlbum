using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RPhotoAlbum.Api.Albums;
using RPhotoAlbum.Api.Data;
using RPhotoAlbum.Api.Models;
using RPhotoAlbum.Api.PCloud;
using RPhotoAlbum.Api.Tests.Fakes;

namespace RPhotoAlbum.Api.Tests.Albums;

// Couvre AlbumService.ListGroupedAsync/SaveStructureAsync (issue #6) — même approche que
// MediaIndexServiceTests : SQLite en mémoire + FakePCloudClient, pas de vrai réseau pCloud.
public sealed class AlbumServiceStructureTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly CacheDbContext _db;
    private readonly FakePCloudClient _client = new();

    public AlbumServiceStructureTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<CacheDbContext>().UseSqlite(_connection).Options;
        _db = new CacheDbContext(options);
        _db.Database.EnsureCreated();

        _db.AppConfigurations.Add(new AppConfiguration { Id = 1, AlbumParentFolderId = 1, AlbumParentFolderPath = "/albums" });
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private AlbumService CreateService()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IPCloudClient>(_client);
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        return new AlbumService(_db, _client, scopeFactory, NullLogger<AlbumService>.Instance);
    }

    private async Task<AlbumSummary> AddAlbumAsync(string id, string name)
    {
        var summary = new AlbumSummary
        {
            Id = id,
            Slug = id,
            Name = name,
            AlbumFolderId = 100,
            AlbumFolderPath = $"/albums/{id}",
            AlbumJsonFileId = 1,
            ItemCount = 0,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.AlbumSummaries.Add(summary);
        await _db.SaveChangesAsync();
        return summary;
    }

    [Fact]
    public async Task NewAlbum_NeverOrganized_AppearsInUnsectioned_WithoutWritingStructureFile()
    {
        await AddAlbumAsync("alb_1", "Vacances");

        var result = await CreateService().ListGroupedAsync();

        Assert.Empty(result.Sections);
        Assert.Single(result.Unsectioned);
        Assert.Equal("alb_1", result.Unsectioned[0].Id);

        var config = await _db.AppConfigurations.AsNoTracking().FirstAsync(c => c.Id == 1);
        Assert.Null(config.AlbumStructureFileId);
    }

    [Fact]
    public async Task SaveStructure_WithSectionsAndOrder_IsReadBackCorrectly()
    {
        await AddAlbumAsync("alb_1", "Vacances");
        await AddAlbumAsync("alb_2", "Anniversaire");
        await AddAlbumAsync("alb_3", "Noël");

        var service = CreateService();
        var saved = await service.SaveStructureAsync(
            [new AlbumSectionInput(null, "2026", ["alb_2", "alb_1"])],
            ["alb_3"]);

        Assert.Single(saved.Sections);
        Assert.Equal("2026", saved.Sections[0].Name);
        Assert.Equal(["alb_2", "alb_1"], saved.Sections[0].Albums.Select(a => a.Id));
        Assert.Equal(["alb_3"], saved.Unsectioned.Select(a => a.Id));

        var reloaded = await CreateService().ListGroupedAsync();
        Assert.Single(reloaded.Sections);
        Assert.Equal("2026", reloaded.Sections[0].Name);
        Assert.Equal(["alb_2", "alb_1"], reloaded.Sections[0].Albums.Select(a => a.Id));
        Assert.Equal(["alb_3"], reloaded.Unsectioned.Select(a => a.Id));

        var config = await _db.AppConfigurations.AsNoTracking().FirstAsync(c => c.Id == 1);
        Assert.NotNull(config.AlbumStructureFileId);
    }

    [Fact]
    public async Task DeletedAlbum_ReferencedInOldStructure_IsFilteredOnNextList()
    {
        await AddAlbumAsync("alb_1", "Vacances");
        await AddAlbumAsync("alb_2", "Anniversaire");
        var service = CreateService();
        await service.SaveStructureAsync([new AlbumSectionInput(null, "2026", ["alb_1", "alb_2"])], []);

        var toDelete = await _db.AlbumSummaries.FirstAsync(a => a.Id == "alb_2");
        _db.AlbumSummaries.Remove(toDelete);
        await _db.SaveChangesAsync();

        var result = await CreateService().ListGroupedAsync();

        Assert.Single(result.Sections);
        Assert.Equal(["alb_1"], result.Sections[0].Albums.Select(a => a.Id));
        Assert.Empty(result.Unsectioned);
    }

    [Fact]
    public async Task SaveStructure_DuplicateAlbumIdAcrossPayload_IsKeptOnlyOnce()
    {
        await AddAlbumAsync("alb_1", "Vacances");
        var service = CreateService();

        var result = await service.SaveStructureAsync(
            [
                new AlbumSectionInput(null, "A", ["alb_1"]),
                new AlbumSectionInput(null, "B", ["alb_1"]),
            ],
            ["alb_1"]);

        var allPlaced = result.Sections.SelectMany(s => s.Albums.Select(a => a.Id))
            .Concat(result.Unsectioned.Select(a => a.Id))
            .ToList();
        Assert.Single(allPlaced);
    }

    [Fact]
    public async Task SaveStructure_UnknownAlbumId_IsDropped()
    {
        await AddAlbumAsync("alb_1", "Vacances");
        var service = CreateService();

        var result = await service.SaveStructureAsync(
            [new AlbumSectionInput(null, "A", ["alb_1", "alb_ghost"])], []);

        Assert.Equal(["alb_1"], result.Sections[0].Albums.Select(a => a.Id));
    }

    [Fact]
    public async Task SaveStructure_AlbumOmittedFromPayload_IsReinjectedIntoUnsectioned()
    {
        await AddAlbumAsync("alb_1", "Vacances");
        await AddAlbumAsync("alb_2", "Anniversaire");
        var service = CreateService();

        // alb_2 existe mais n'apparaît nulle part dans le payload envoyé.
        var result = await service.SaveStructureAsync([new AlbumSectionInput(null, "A", ["alb_1"])], []);

        Assert.Equal(["alb_1"], result.Sections[0].Albums.Select(a => a.Id));
        Assert.Equal(["alb_2"], result.Unsectioned.Select(a => a.Id));
    }

    [Fact]
    public async Task SaveStructure_BlankSectionId_GeneratesNewId()
    {
        await AddAlbumAsync("alb_1", "Vacances");
        var service = CreateService();

        var result = await service.SaveStructureAsync([new AlbumSectionInput(null, "A", ["alb_1"])], []);

        Assert.False(string.IsNullOrWhiteSpace(result.Sections[0].Id));
    }

    [Fact]
    public async Task SaveStructure_ExistingSectionId_IsPreservedAcrossSaves()
    {
        await AddAlbumAsync("alb_1", "Vacances");
        var service = CreateService();

        var first = await service.SaveStructureAsync([new AlbumSectionInput(null, "A", ["alb_1"])], []);
        var sectionId = first.Sections[0].Id;

        var second = await service.SaveStructureAsync(
            [new AlbumSectionInput(sectionId, "A renommée", ["alb_1"])], []);

        Assert.Equal(sectionId, second.Sections[0].Id);
        Assert.Equal("A renommée", second.Sections[0].Name);
    }
}

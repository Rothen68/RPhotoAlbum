using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RPhotoAlbum.Api.Data;
using RPhotoAlbum.Api.Media;
using RPhotoAlbum.Api.Models;
using RPhotoAlbum.Api.PCloud;
using RPhotoAlbum.Api.Tests.Fakes;

namespace RPhotoAlbum.Api.Tests.Media;

// Regression #12 (see issue #17): MediaExifService.ExtractAsync runs with bounded concurrency
// (MaxConcurrency) and MUST resolve its own scope — hence its own IPCloudClient, hence its
// own CacheDbContext — for each operation, never a scope shared between two simultaneous
// extractions (EF Core DbContext is not thread-safe). This test verifies that mechanism
// directly via an IServiceScopeFactory that records every resolved IPCloudClient, rather than
// inferring isolation indirectly from the absence of a crash (too weak a signal: a shared
// DbContext could just as well succeed by chance depending on timing).
public sealed class MediaExifServiceScopeIsolationTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;
    private readonly RecordingServiceScopeFactory _scopeFactory;

    public MediaExifServiceScopeIsolationTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<CacheDbContext>(o => o.UseSqlite(_connection));
        // Scoped (not singleton): a new instance per scope, like IPCloudClient in
        // production (AddHttpClient<IPCloudClient, PCloudClient>()) — necessary for two
        // distinct scopes to actually be observable as two distinct instances.
        services.AddScoped<IPCloudClient, FakePCloudClient>();
        _provider = services.BuildServiceProvider();

        using (var scope = _provider.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<CacheDbContext>().Database.EnsureCreated();
        }

        _scopeFactory = new RecordingServiceScopeFactory(_provider);
    }

    public void Dispose()
    {
        _provider.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task ConcurrentExtraction_ResolvesADistinctIPCloudClientPerItem()
    {
        const int itemCount = 6;
        using (var scope = _provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CacheDbContext>();
            for (var i = 1; i <= itemCount; i++)
            {
                db.MediaIndex.Add(new MediaIndexEntry
                {
                    PCloudFileId = i,
                    Name = $"img{i}.jpg",
                    Path = "",
                    Hash = "",
                    MediaType = "image",
                    IndexedAt = DateTime.UtcNow,
                });
            }

            await db.SaveChangesAsync();
        }

        var geoService = new GeoLookupService(_scopeFactory, new HttpClient(), NullLogger<GeoLookupService>.Instance);
        var exifService = new MediaExifService(_scopeFactory, geoService, NullLogger<MediaExifService>.Instance);

        await exifService.StartAsync();
        await WaitUntilIdleAsync(exifService);

        // At least one IPCloudClient resolved per media item (RunAsync/GeoLookupService also
        // each resolve one for their own use — >= rather than ==, which doesn't matter here:
        // all that counts is the absence of sharing between concurrent extractions).
        Assert.True(_scopeFactory.ResolvedClients.Count >= itemCount);
        Assert.Equal(_scopeFactory.ResolvedClients.Count, _scopeFactory.ResolvedClients.Distinct().Count());
    }

    private static async Task WaitUntilIdleAsync(MediaExifService service)
    {
        for (var i = 0; i < 100; i++)
        {
            var status = await service.GetStatusAsync(CancellationToken.None);
            if (!status.Running)
            {
                return;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("Le job EXIF ne s'est pas terminé à temps.");
    }
}

// Delegates to a real IServiceScopeFactory while recording every IPCloudClient resolved at
// scope creation — allows verifying afterward that no instance was shared between two
// distinct scopes (see the class comment above).
internal sealed class RecordingServiceScopeFactory(IServiceProvider inner) : IServiceScopeFactory
{
    private readonly IServiceScopeFactory _innerFactory = inner.GetRequiredService<IServiceScopeFactory>();

    public readonly ConcurrentBag<IPCloudClient> ResolvedClients = new();

    public IServiceScope CreateScope()
    {
        var scope = _innerFactory.CreateScope();
        ResolvedClients.Add(scope.ServiceProvider.GetRequiredService<IPCloudClient>());
        return scope;
    }
}

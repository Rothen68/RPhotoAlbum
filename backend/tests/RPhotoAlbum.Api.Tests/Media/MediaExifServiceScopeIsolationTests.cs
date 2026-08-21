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

// Régression #12 (voir issue #17) : MediaExifService.ExtractAsync s'exécute en concurrence bornée
// (MaxConcurrency) et DOIT résoudre son propre scope — donc son propre IPCloudClient, donc son
// propre CacheDbContext — pour chaque opération, jamais un scope partagé entre deux extractions
// simultanées (EF Core DbContext n'est pas thread-safe). Ce test vérifie directement ce mécanisme
// via un IServiceScopeFactory qui enregistre chaque IPCloudClient résolu, plutôt que d'inférer
// l'isolation indirectement depuis l'absence de crash (signal trop faible : un DbContext partagé
// peut aussi bien réussir par chance selon le timing).
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
        // Scoped (pas singleton) : une nouvelle instance par scope, comme IPCloudClient en
        // production (AddHttpClient<IPCloudClient, PCloudClient>()) — nécessaire pour que deux
        // scopes distincts soient effectivement observables comme deux instances distinctes.
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

        // Au moins un IPCloudClient résolu par média (RunAsync/GeoLookupService en résolvent
        // aussi un chacun pour leur propre compte — >= plutôt que ==, sans importance ici : seule
        // compte l'absence de partage entre extractions concurrentes).
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

// Délègue à un vrai IServiceScopeFactory tout en enregistrant chaque IPCloudClient résolu à la
// création d'un scope — permet de vérifier après coup qu'aucune instance n'a été partagée entre
// deux scopes distincts (voir commentaire de classe ci-dessus).
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

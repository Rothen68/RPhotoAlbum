using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using RPhotoAlbum.Api.Data;

namespace RPhotoAlbum.Api.PCloud;

public record PCloudConnectionInfo(string Hostname, string AccessToken);

// Ligne unique persistée dans le cache local (SQLite) — jeton chiffré au repos
// via Data Protection, jamais exposé au frontend — voir ARCHITECTURE.md §14.
public class PCloudTokenStore
{
    private const int SingletonId = 1;
    private const string ProtectorPurpose = "PCloudAccessToken";
    // Pas de TTL : le jeton ne change que sur reconnexion/déconnexion, invalidé explicitement
    // dans SaveAsync/ClearAsync plutôt que sur une durée arbitraire. Avant ce cache, GetAsync()
    // interrogeait la base SQLite à CHAQUE appel pCloud (le jeton n'était jamais mis en cache) —
    // avec un job comme l'extraction EXIF qui fait un appel par média, ça représentait des
    // dizaines de milliers de requêtes DB évitables, et c'est ce qui a rendu visible (issues
    // #12, #13) qu'un DbContext EF Core partagé entre opérations concurrentes n'est pas
    // thread-safe. Le cache mémoire (thread-safe, lui) élimine l'essentiel de cette charge ;
    // le correctif scope-par-tâche déjà en place (voir MediaExifService, AlbumService) reste la
    // protection de fond pour les cache-miss concurrents.
    private const string CacheKey = "pcloud:connection";

    private readonly CacheDbContext _db;
    private readonly IDataProtector _protector;
    private readonly IMemoryCache _cache;

    public PCloudTokenStore(CacheDbContext db, IDataProtectionProvider dataProtectionProvider, IMemoryCache cache)
    {
        _db = db;
        _protector = dataProtectionProvider.CreateProtector(ProtectorPurpose);
        _cache = cache;
    }

    public async Task SaveAsync(string hostname, string accessToken)
    {
        var encrypted = _protector.Protect(accessToken);
        var existing = await _db.PCloudConnections.FindAsync(SingletonId);

        if (existing is null)
        {
            _db.PCloudConnections.Add(new PCloudConnection
            {
                Id = SingletonId,
                Hostname = hostname,
                EncryptedAccessToken = encrypted,
                ConnectedAt = DateTime.UtcNow,
            });
        }
        else
        {
            existing.Hostname = hostname;
            existing.EncryptedAccessToken = encrypted;
            existing.ConnectedAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync();
        _cache.Set(CacheKey, new PCloudConnectionInfo(hostname, accessToken));
    }

    public async Task<PCloudConnectionInfo?> GetAsync()
    {
        if (_cache.TryGetValue(CacheKey, out PCloudConnectionInfo? cached))
        {
            return cached;
        }

        var entry = await _db.PCloudConnections.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == SingletonId);

        if (entry is null)
        {
            return null;
        }

        var info = new PCloudConnectionInfo(entry.Hostname, _protector.Unprotect(entry.EncryptedAccessToken));
        _cache.Set(CacheKey, info);
        return info;
    }

    public async Task ClearAsync()
    {
        var entry = await _db.PCloudConnections.FindAsync(SingletonId);
        if (entry is not null)
        {
            _db.PCloudConnections.Remove(entry);
            await _db.SaveChangesAsync();
        }

        _cache.Remove(CacheKey);
    }
}

using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using RPhotoAlbum.Api.Data;

namespace RPhotoAlbum.Api.PCloud;

public record PCloudConnectionInfo(string Hostname, string AccessToken);

// Single row persisted in the local cache (SQLite) — token encrypted at rest
// via Data Protection, never exposed to the frontend — see ARCHITECTURE.md §14.
public class PCloudTokenStore
{
    private const int SingletonId = 1;
    private const string ProtectorPurpose = "PCloudAccessToken";
    // No TTL: the token only changes on reconnect/disconnect, explicitly invalidated
    // in SaveAsync/ClearAsync rather than on an arbitrary duration. Before this cache, GetAsync()
    // queried the SQLite database on EVERY pCloud call (the token was never cached) —
    // with a job like EXIF extraction making one call per media item, that represented
    // tens of thousands of avoidable DB queries, and this is what exposed (issues
    // #12, #13) that an EF Core DbContext shared across concurrent operations is not
    // thread-safe. The memory cache (which is thread-safe) eliminates most of this load;
    // the per-task scope fix already in place (see MediaExifService, AlbumService) remains the
    // underlying protection for concurrent cache misses.
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

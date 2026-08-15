using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using RPhotoAlbum.Api.Data;

namespace RPhotoAlbum.Api.PCloud;

public record PCloudConnectionInfo(string Hostname, string AccessToken);

// Ligne unique persistée dans le cache local (SQLite) — jeton chiffré au repos
// via Data Protection, jamais exposé au frontend — voir ARCHITECTURE.md §14.
public class PCloudTokenStore
{
    private const int SingletonId = 1;
    private const string ProtectorPurpose = "PCloudAccessToken";

    private readonly CacheDbContext _db;
    private readonly IDataProtector _protector;

    public PCloudTokenStore(CacheDbContext db, IDataProtectionProvider dataProtectionProvider)
    {
        _db = db;
        _protector = dataProtectionProvider.CreateProtector(ProtectorPurpose);
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
                ConnectedAt = DateTimeOffset.UtcNow,
            });
        }
        else
        {
            existing.Hostname = hostname;
            existing.EncryptedAccessToken = encrypted;
            existing.ConnectedAt = DateTimeOffset.UtcNow;
        }

        await _db.SaveChangesAsync();
    }

    public async Task<PCloudConnectionInfo?> GetAsync()
    {
        var entry = await _db.PCloudConnections.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == SingletonId);

        return entry is null ? null : new PCloudConnectionInfo(entry.Hostname, _protector.Unprotect(entry.EncryptedAccessToken));
    }

    public async Task ClearAsync()
    {
        var entry = await _db.PCloudConnections.FindAsync(SingletonId);
        if (entry is not null)
        {
            _db.PCloudConnections.Remove(entry);
            await _db.SaveChangesAsync();
        }
    }
}

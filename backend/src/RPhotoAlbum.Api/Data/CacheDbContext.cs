using Microsoft.EntityFrameworkCore;
using RPhotoAlbum.Api.Models;
using RPhotoAlbum.Api.PCloud;

namespace RPhotoAlbum.Api.Data;

// Cache local (SQLite) : index de performance reconstructible depuis pCloud.
// N'est jamais la source de vérité métier — voir ARCHITECTURE.md §3 et §6.1.
// Exception : PCloudConnections stocke le jeton OAuth chiffré, qui n'existe pas côté pCloud.
public class CacheDbContext(DbContextOptions<CacheDbContext> options) : DbContext(options)
{
    public DbSet<MediaIndexEntry> MediaIndex => Set<MediaIndexEntry>();
    public DbSet<AlbumSummary> AlbumSummaries => Set<AlbumSummary>();
    public DbSet<PCloudConnection> PCloudConnections => Set<PCloudConnection>();
    public DbSet<SourceFolder> SourceFolders => Set<SourceFolder>();
    public DbSet<AppConfiguration> AppConfigurations => Set<AppConfiguration>();
    public DbSet<GeoLocationCache> GeoLocationCache => Set<GeoLocationCache>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<MediaIndexEntry>()
            .HasIndex(m => m.PCloudFileId)
            .IsUnique();

        modelBuilder.Entity<AlbumSummary>()
            .HasKey(a => a.Id);

        modelBuilder.Entity<PCloudConnection>()
            .HasKey(c => c.Id);

        modelBuilder.Entity<SourceFolder>()
            .HasIndex(f => f.PCloudFolderId)
            .IsUnique();

        modelBuilder.Entity<AppConfiguration>()
            .HasKey(c => c.Id);

        modelBuilder.Entity<GeoLocationCache>()
            .HasIndex(g => new { g.RoundedLatitude, g.RoundedLongitude })
            .IsUnique();
    }
}

using Microsoft.EntityFrameworkCore;
using RPhotoAlbum.Api.Models;

namespace RPhotoAlbum.Api.Data;

// Cache local (SQLite) : index de performance reconstructible depuis pCloud.
// N'est jamais la source de vérité métier — voir ARCHITECTURE.md §3 et §6.1.
public class CacheDbContext(DbContextOptions<CacheDbContext> options) : DbContext(options)
{
    public DbSet<MediaIndexEntry> MediaIndex => Set<MediaIndexEntry>();
    public DbSet<AlbumSummary> AlbumSummaries => Set<AlbumSummary>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<MediaIndexEntry>()
            .HasIndex(m => m.PCloudFileId)
            .IsUnique();

        modelBuilder.Entity<AlbumSummary>()
            .HasKey(a => a.Id);
    }
}

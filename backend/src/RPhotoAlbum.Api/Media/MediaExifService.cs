using Microsoft.EntityFrameworkCore;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.Jpeg;
using MetadataExtractor.Formats.QuickTime;
using RPhotoAlbum.Api.Data;
using RPhotoAlbum.Api.Models;
using RPhotoAlbum.Api.PCloud;

namespace RPhotoAlbum.Api.Media;

public record ExifJobStatus(bool Running, int Processed, int Total, DateTime? StartedAt, string? LastError);

internal record ExifResult(long Id, DateTime? DateTaken, double? Latitude, double? Longitude, int? Width, int? Height);

// Manual job (not periodic like MediaIndexBackgroundService): extracts the actual capture date
// (EXIF DateTimeOriginal for images, QuickTime/MP4 mvhd.creation_time atom for videos — see
// issue #21) and the GPS coordinates of the cached images, downloading only a small header of
// each file (see PCloudClient.DownloadPartialAsync) — see V2 plan step 9. No GPS for videos:
// rare in consumer video metadata.
public class MediaExifService(IServiceScopeFactory scopeFactory, GeoLookupService geoService, ILogger<MediaExifService> logger)
{
    // Read from the start of the file: enough for the EXIF/GPS IFD of nearly all JPEG and
    // RAW (TIFF-based, e.g. CR2) files — much smaller than a full RAW file (tens of MB).
    private const int ExifReadBytes = 512 * 1024;
    // Read size for the "moov" atom (QuickTime/MP4, contains mvhd.creation_time) —
    // distinct from ExifReadBytes above (same value for now, but semantically
    // different, tunable independently if real-world experience shows it needs adjusting).
    // Unlike JPEG (EXIF always at the start), "moov" can be at the start OR the end of the
    // file depending on the encoder (no "faststart") — see TryReadVideoCreationDateAsync
    // below, which tries both before giving up.
    private const int VideoReadBytes = 512 * 1024;
    // Concurrency deliberately limited (see V2 step 4: pCloud can be slow to generate a
    // link for a file that has never been accessed) — this job must not worsen the perceived
    // latency of normal app usage happening in parallel.
    private const int MaxConcurrency = 3;
    private const int SaveBatchSize = 50;

    private static readonly SemaphoreSlim RunLock = new(1, 1);
    private static volatile bool _running;
    private static DateTime? _startedAt;
    private static string? _lastError;
    private static CancellationTokenSource? _cts;

    public async Task<ExifJobStatus> GetStatusAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CacheDbContext>();
        var total = await db.MediaIndex.CountAsync(m => m.MediaType == "image" || m.MediaType == "video", ct);
        var processed = await db.MediaIndex.CountAsync(
            m => (m.MediaType == "image" || m.MediaType == "video") && m.ExifProcessedAt != null, ct);
        return new ExifJobStatus(_running, processed, total, _startedAt, _lastError);
    }

    // Starts in the background without blocking the calling HTTP request — idempotent (no-op if
    // already running). Progress itself is recomputed from the database on every GetStatusAsync
    // (no in-memory counter besides _running): a container restart therefore loses no progress
    // already written, only the "running" flag goes back to false (naturally resumed by
    // relaunching, which re-filters on ExifProcessedAt == null).
    public async Task StartAsync()
    {
        if (!await RunLock.WaitAsync(0))
        {
            return;
        }

        _cts = new CancellationTokenSource();
        _running = true;
        _startedAt = DateTime.UtcNow;
        _lastError = null;

        _ = Task.Run(() => RunAsync(_cts.Token));
    }

    public void Stop() => _cts?.Cancel();

    // Two phases deliberately separated PER BATCH (SaveBatchSize): the download + EXIF
    // extraction of a batch (network I/O, concurrent, bounded by MaxConcurrency) NEVER TOUCH the
    // DbContext; the database write for that batch is then done sequentially on a SINGLE
    // DbContext, before moving to the next batch — progress (recomputed from the database on
    // every status call) therefore advances steadily throughout the job, not only at the very
    // end.
    //
    // db is resolved HERE, from a scope created for (and lasting) the entire duration of the
    // job — NOT injected into MediaExifService's constructor. Root cause of a bug observed in
    // practice (job running with no logged error, but no date ever persisted): MediaExifService
    // is itself Scoped, so a service injected into its constructor stays bound to the scope of
    // the /exif/start HTTP request — which completes (and disposes its scope) almost immediately
    // after the background Task.Run starts. The downloads then failed silently
    // (ObjectDisposedException swallowed by ExtractAsync's generic catch).
    //
    // PCloudClient, on the other hand, is NOT resolved here but individually in each concurrent
    // call to ExtractAsync (see below): PCloudClient depends on PCloudTokenStore, which queries
    // CacheDbContext on every pCloud call (the token is not cached). An EF Core DbContext is NOT
    // thread-safe — sharing it across concurrent tasks (MaxConcurrency) caused random "Cannot
    // access a disposed object: SQLitePCL.sqlite3" errors (issue #12), the DbContext being hit
    // by several threads at once.
    private async Task RunAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CacheDbContext>();
        using var throttle = new SemaphoreSlim(MaxConcurrency, MaxConcurrency);

        try
        {
            var pendingIds = await db.MediaIndex.AsNoTracking()
                .Where(m => (m.MediaType == "image" || m.MediaType == "video") && m.ExifProcessedAt == null)
                .Select(m => new { m.Id, m.PCloudFileId, m.MediaType })
                .ToListAsync(ct);

            foreach (var batch in pendingIds.Chunk(SaveBatchSize))
            {
                var extractTasks = batch.Select(async item =>
                {
                    await throttle.WaitAsync(ct);
                    try
                    {
                        return await ExtractAsync(item.Id, item.PCloudFileId, item.MediaType, ct);
                    }
                    finally
                    {
                        throttle.Release();
                    }
                });

                var results = await Task.WhenAll(extractTasks);

                // Tracked (not AsNoTracking): we're going to modify these entities — same pattern
                // as MediaIndexService.ReindexAsync (already proven at ~64k entries), but loaded
                // batch by batch here instead of all at once (63k permanently tracked entities
                // would be needlessly heavy given the batch-by-batch writing).
                var batchIds = batch.Select(b => b.Id).ToList();
                var entries = await db.MediaIndex.Where(m => batchIds.Contains(m.Id)).ToDictionaryAsync(e => e.Id, ct);

                foreach (var result in results)
                {
                    var entry = entries[result.Id];
                    entry.DateTaken = result.DateTaken;
                    entry.Latitude = result.Latitude;
                    entry.Longitude = result.Longitude;
                    entry.Width = result.Width;
                    entry.Height = result.Height;
                    entry.ExifProcessedAt = DateTime.UtcNow;
                }

                await db.SaveChangesAsync(ct);
                db.ChangeTracker.Clear(); // avoids accumulating previous batches' entities in memory
            }

            // Issue #11: automatically chains into geolocation of the GPS coordinates that were
            // just extracted — only here (normal end of the loop), not in the finally block
            // below, so as to never trigger geo after a manual Stop() or a real error.
            // StartAsync() is idempotent (no-op if already running).
            await geoService.StartAsync();
        }
        catch (OperationCanceledException)
        {
            // Stop requested (Stop()) — every already-complete batch was saved as it went,
            // nothing to catch up on here; the batch in progress at the moment of the stop is
            // simply lost (naturally resumed on the next run, since ExifProcessedAt stayed null
            // for it).
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            logger.LogError(ex, "Échec du job d'extraction EXIF.");
        }
        finally
        {
            _running = false;
            RunLock.Release();
        }
    }

    private async Task<ExifResult> ExtractAsync(long id, long pCloudFileId, string mediaType, CancellationToken ct)
    {
        // Scope dedicated to THIS concurrent call (see comment on RunAsync): isolates the
        // CacheDbContext used by PCloudTokenStore from those of the other extractions
        // running in parallel.
        using var scope = scopeFactory.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<IPCloudClient>();

        if (mediaType == "video")
        {
            var videoDate = await TryReadVideoCreationDateAsync(() => client.DownloadPartialAsync(pCloudFileId, VideoReadBytes, ct), ct)
                ?? await TryReadVideoCreationDateAsync(() => client.DownloadTailAsync(pCloudFileId, VideoReadBytes, ct), ct);
            return new ExifResult(id, videoDate, null, null, null, null);
        }

        try
        {
            var bytes = await client.DownloadPartialAsync(pCloudFileId, ExifReadBytes, ct);
            using var stream = new MemoryStream(bytes);
            var directories = ImageMetadataReader.ReadMetadata(stream);

            DateTime? dateTaken = null;
            var subIfd = directories.OfType<ExifSubIfdDirectory>().FirstOrDefault();
            if (subIfd?.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var dt) == true)
            {
                dateTaken = dt;
            }

            double? latitude = null;
            double? longitude = null;
            var gpsDirectory = directories.OfType<GpsDirectory>().FirstOrDefault();
            // GeoLocation is a struct (so GetGeoLocation() returns a GeoLocation? in the
            // Nullable<T> sense) — the capture pattern is needed to get a usable non-nullable
            // variable.
            if (gpsDirectory?.GetGeoLocation() is { IsZero: false } location)
            {
                latitude = location.Latitude;
                longitude = location.Longitude;
            }

            // Image dimensions — needed to precompute row height in Album Detail's
            // virtualization (issue #20) without having to measure each image after rendering.
            // PixelXDimension/PixelYDimension (EXIF SubIFD) first — this is the actual dimension
            // as recorded by the device, reliable for RAW (TIFF-based) too — with a fallback to
            // JPEG SOF markers if the EXIF doesn't carry them (some editors don't rewrite these
            // tags).
            int? width = null;
            int? height = null;
            if (subIfd?.TryGetInt32(ExifDirectoryBase.TagExifImageWidth, out var exifWidth) == true &&
                subIfd.TryGetInt32(ExifDirectoryBase.TagExifImageHeight, out var exifHeight) == true)
            {
                width = exifWidth;
                height = exifHeight;
            }
            else if (directories.OfType<JpegDirectory>().FirstOrDefault() is { } jpegDirectory &&
                jpegDirectory.TryGetInt32(JpegDirectory.TagImageWidth, out var jpegWidth) &&
                jpegDirectory.TryGetInt32(JpegDirectory.TagImageHeight, out var jpegHeight))
            {
                width = jpegWidth;
                height = jpegHeight;
            }

            return new ExifResult(id, dateTaken, latitude, longitude, width, height);
        }
        // Do NOT exclude OperationCanceledException here: HttpClient throws a TaskCanceledException
        // (which DERIVES from it) on a plain request timeout (default 100s — cold pCloud links
        // already observed taking 30s+ to generate, see step 4). A filter on the exception type
        // let this timeout propagate up to RunAsync's catch (OperationCanceledException), which
        // wrongly interpreted it as a deliberate Stop() and silently stopped the whole job (no
        // logged error) after only a few dozen items — bug observed in practice across two
        // successive runs. We now distinguish a deliberate stop only via the actual state of our
        // own token.
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // No usable EXIF (unsupported format, corrupted file, incomplete header,
            // network timeout…) — normal for a good part of the library, not an error
            // to surface.
            logger.LogDebug(ex, "Pas d'EXIF exploitable pour le média {Id}.", id);
            return new ExifResult(id, null, null, null, null, null);
        }
    }

    // Classic QuickTime/Mac epoch (seconds since this reference point) — an mvhd.creation_time
    // of zero (thus converted as-is by MetadataExtractor to 1904-01-01T00:00:00) is a very common
    // sentinel value meaning "never set" (observed in practice on a video re-encoded by a
    // third-party tool, not a real capture date) — to be distinguished from a real date.
    private static readonly DateTime QuickTimeEpoch = new(1904, 1, 1);

    // Tries to read mvhd.creation_time from the given bytes (start OR end of file — see
    // caller). Deliberately silent (returns null) on any failure: a file truncated at this read
    // size (moov too large, or located elsewhere than the portion read) is a normal case, not an
    // error to surface — the next attempt (start then end) or the absence of a date takes over.
    // Does NOT mask a real cancellation (see comment on ExtractAsync above, same pitfall).
    private static async Task<DateTime?> TryReadVideoCreationDateAsync(Func<Task<byte[]>> downloadBytes, CancellationToken ct)
    {
        try
        {
            var bytes = await downloadBytes();
            using var stream = new MemoryStream(bytes);
            var directories = ImageMetadataReader.ReadMetadata(stream);
            var movieHeader = directories.OfType<QuickTimeMovieHeaderDirectory>().FirstOrDefault();
            if (movieHeader?.TryGetDateTime(QuickTimeMovieHeaderDirectory.TagCreated, out var dt) != true)
            {
                return null;
            }

            return dt <= QuickTimeEpoch ? null : dt;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }
}

using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using RPhotoAlbum.Api.Albums;
using RPhotoAlbum.Api.Auth;
using RPhotoAlbum.Api.Data;
using RPhotoAlbum.Api.Media;
using RPhotoAlbum.Api.PCloud;
using Serilog;

if (args is ["hash-password", var password])
{
    var hash = new PasswordHasher<object>().HashPassword(new object(), password);
    Console.WriteLine(hash);
    return;
}

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console());

builder.Services.AddControllers();
builder.Services.AddOpenApi();

builder.Services.AddDbContext<CacheDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("Cache")));

builder.Services.Configure<AppAuthOptions>(builder.Configuration.GetSection("App"));
builder.Services.Configure<PCloudOptions>(builder.Configuration.GetSection("PCloud"));
builder.Services.AddScoped<PCloudTokenStore>();
builder.Services.AddMemoryCache();
// IPCloudClient (not just PCloudClient): allows a hand-written fake in tests
// (RPhotoAlbum.Api.Tests) without depending on the real pCloud network — see GitHub issue #17.
// Explicit timeout (otherwise HttpClient's default: 100s, never configured until now) — was
// silently capping EVERY pCloud request at 100s regardless of the CancellationToken passed at
// call time, hidden as long as nginx's proxy_read_timeout (60s by default) was shorter anyway
// and cut the connection first — only revealed once that was fixed (issue #26).
// Set above the thumbnail cache's application-level timeout
// (MediaThumbnailCacheService.ThumbnailFetchTimeout, 150s) and below nginx's proxy_read_timeout
// (180s, see reverse-proxy/nginx.conf) to keep the intended order: the application-level timeout
// is always the one that triggers first (a clean failure, 404), with HttpClient and nginx
// serving only as safety nets.
builder.Services.AddHttpClient<IPCloudClient, PCloudClient>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(170);
});

builder.Services.Configure<IndexingOptions>(builder.Configuration.GetSection("Indexing"));
builder.Services.AddScoped<MediaIndexService>();
builder.Services.AddHostedService<MediaIndexBackgroundService>();

builder.Services.AddScoped<MediaExifService>();

// Custom User-Agent required by Nominatim's usage policy (not HttpClient's generic default
// one) — see GeoLookupService.
builder.Services.AddHttpClient<GeoLookupService>(client =>
{
    client.DefaultRequestHeaders.UserAgent.ParseAdd("RPhotoAlbum/1.0 (usage personnel, self-hosted)");
});

builder.Services.AddScoped<AlbumService>();

// Thumbnail disk cache (issue #26) — avoids going back through pCloud (getthumblink + CDN) on
// every display. Scoped (not singleton): IPCloudClient depends on PCloudTokenStore, which is
// itself scoped — the same captive dependency trap as #12, avoided by never promoting this
// service beyond request scope.
builder.Services.Configure<MediaCacheOptions>(builder.Configuration.GetSection("MediaCache"));
builder.Services.AddScoped<MediaThumbnailCacheService>();
builder.Services.AddHostedService<MediaCacheEvictionBackgroundService>();

var keysPath = builder.Environment.IsDevelopment()
    ? Path.Combine(builder.Environment.ContentRootPath, ".keys")
    : "/data/keys";

var thumbnailCacheDir = builder.Environment.IsDevelopment()
    ? Path.Combine(builder.Environment.ContentRootPath, ".thumbnail-cache")
    : "/data/thumbnails";
builder.Services.AddSingleton(new MediaCacheDirectory(thumbnailCacheDir));

builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keysPath))
    .SetApplicationName("RPhotoAlbum");

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "RPhotoAlbum.Session";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.ExpireTimeSpan = TimeSpan.FromDays(30);
        options.SlidingExpiration = true;
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    });

builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build());

// Limits login attempts (no protection before this, VPN/LAN was the only safeguard) —
// partitioned by client IP (not a global counter): a third party brute-forcing the password
// does not lock out the legitimate user. RemoteIpAddress already reflects the real IP thanks to
// UseForwardedHeaders (X-Forwarded-For forwarded by nginx), configured further below.
builder.Services.AddRateLimiter(options =>
{
    options.OnRejected = async (context, ct) =>
    {
        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        await context.HttpContext.Response.WriteAsJsonAsync(
            new { error = "Trop de tentatives, réessayez plus tard." }, ct);
    };
    options.AddPolicy("login", httpContext => RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        factory: _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 5,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        }));
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var cacheDb = scope.ServiceProvider.GetRequiredService<CacheDbContext>();
    cacheDb.Database.Migrate();

    // WAL mode — a setting stored in the file itself (not per connection), so it only needs to
    // be done once at startup; idempotent if already enabled. Reduces contention between
    // background write jobs (indexing, EXIF, geolocation) and the app's normal requests while a
    // job is running — see issue #18.
    var connection = cacheDb.Database.GetDbConnection();
    await connection.OpenAsync();
    await using (var pragma = connection.CreateCommand())
    {
        pragma.CommandText = "PRAGMA journal_mode=WAL;";
        await pragma.ExecuteScalarAsync();
    }
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// TLS termination happens entirely on the reverse-proxy side (nginx) — the backend only ever
// receives plain HTTP internally (Docker network), so Request.IsHttps would always be false
// there without this middleware, even when the client is on HTTPS (issue #29: the session
// cookie and the pCloud OAuth cookie, see PCloudController.cs, would then never be marked
// Secure).
// KnownNetworks/KnownProxies cleared: the backend service has no port published in
// docker-compose.yml, so the reverse-proxy is the only possible caller — trusting any internal
// source IP is safe here, since direct exposure is not possible.
var forwardedHeadersOptions = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
};
forwardedHeadersOptions.KnownIPNetworks.Clear();
forwardedHeadersOptions.KnownProxies.Clear();
app.UseForwardedHeaders(forwardedHeadersOptions);

app.UseHttpsRedirection();

app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();

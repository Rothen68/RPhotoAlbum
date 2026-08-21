using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
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
// IPCloudClient (pas juste PCloudClient) : permet un faux fait main dans les tests
// (RPhotoAlbum.Api.Tests) sans dépendre du vrai réseau pCloud — voir issue GitHub #17.
// Timeout explicite (défaut HttpClient sinon : 100s, jamais configuré jusqu'ici) — plafonnait
// silencieusement TOUTE requête pCloud à 100s quel que soit le CancellationToken passé en appel,
// masqué tant que le proxy_read_timeout de nginx (60s par défaut) était de toute façon plus court
// et coupait la connexion en premier — révélé seulement une fois ce dernier corrigé (issue #26).
// Réglé au-dessus du délai applicatif du cache miniatures
// (MediaThumbnailCacheService.ThumbnailFetchTimeout, 150s) et en dessous du proxy_read_timeout
// nginx (180s, voir reverse-proxy/nginx.conf) pour garder l'ordre voulu : c'est toujours le délai
// applicatif qui tranche en premier (échec propre, 404), HttpClient et nginx ne servant que de
// filets de sécurité.
builder.Services.AddHttpClient<IPCloudClient, PCloudClient>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(170);
});

builder.Services.Configure<IndexingOptions>(builder.Configuration.GetSection("Indexing"));
builder.Services.AddScoped<MediaIndexService>();
builder.Services.AddHostedService<MediaIndexBackgroundService>();

builder.Services.AddScoped<MediaExifService>();

// User-Agent personnalisé obligatoire par la politique d'usage Nominatim (pas celui, générique,
// de HttpClient) — voir GeoLookupService.
builder.Services.AddHttpClient<GeoLookupService>(client =>
{
    client.DefaultRequestHeaders.UserAgent.ParseAdd("RPhotoAlbum/1.0 (usage personnel, self-hosted)");
});

builder.Services.AddScoped<AlbumService>();

// Cache disque des miniatures (issue #26) — évite de repasser par pCloud (getthumblink + CDN) à
// chaque affichage. Scoped (pas singleton) : IPCloudClient dépend de PCloudTokenStore, lui-même
// scoped — même piège de dépendance captive que #12, évité en ne remontant jamais ce service
// au-delà de la portée requête.
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

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var cacheDb = scope.ServiceProvider.GetRequiredService<CacheDbContext>();
    cacheDb.Database.Migrate();

    // Mode WAL — réglage stocké dans le fichier lui-même (pas par connexion), donc suffit de le
    // faire une fois au démarrage ; idempotent si déjà activé. Réduit la contention entre les
    // jobs qui écrivent en tâche de fond (indexation, EXIF, géolocalisation) et les requêtes
    // normales de l'app pendant qu'un job tourne — voir issue #18.
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

// La terminaison TLS se fait entièrement côté reverse-proxy (nginx) — le backend ne reçoit
// jamais que du HTTP simple en interne (réseau Docker), donc Request.IsHttps y serait toujours
// false sans ce middleware, même quand le client est en HTTPS (issue #29 : cookie de session et
// cookie OAuth pCloud, voir PCloudController.cs, ne seraient alors jamais marqués Secure).
// KnownNetworks/KnownProxies vidés : le service backend n'a aucun port publié dans
// docker-compose.yml, le reverse-proxy est donc le seul appelant possible — faire confiance à
// n'importe quelle IP source interne est sûr ici, pas d'exposition directe possible.
var forwardedHeadersOptions = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
};
forwardedHeadersOptions.KnownIPNetworks.Clear();
forwardedHeadersOptions.KnownProxies.Clear();
app.UseForwardedHeaders(forwardedHeadersOptions);

app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();

using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using RPhotoAlbum.Api.Auth;
using RPhotoAlbum.Api.Data;
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
builder.Services.AddHttpClient<PCloudClient>();

var keysPath = builder.Environment.IsDevelopment()
    ? Path.Combine(builder.Environment.ContentRootPath, ".keys")
    : "/data/keys";

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
    scope.ServiceProvider.GetRequiredService<CacheDbContext>().Database.EnsureCreated();
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();

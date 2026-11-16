using System.Security.Claims;
using System.Threading.RateLimiting;
using ASync.Api;
using ASync.Api.Auth;
using ASync.Api.HostedServices;
using ASync.Api.Hubs;
using ASync.Api.Services;
using ASync.Api.Services.Alerts;
using ASync.Api.Services.Push;
using ASync.Core.Interfaces;
using ASync.Core.Models;
using ASync.Infrastructure.Data;
using ASync.Infrastructure.Repositories;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Load optional local overrides (not committed to Git)
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

// ── Database ──────────────────────────────────────────────────────────────────
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? "Host=localhost;Port=5432;Database=async;Username=async;Password=async";

// `dotnet ASync.Api.dll secrets show` — print database-stored secrets as env vars, then exit.
if (SecretsCommand.Matches(args))
    return await SecretsCommand.RunAsync(args, connectionString, builder.Configuration);

// AddDbContextFactory registers IDbContextFactory<AppDbContext> (Singleton) and also AppDbContext as Scoped.
builder.Services.AddDbContextFactory<AppDbContext>(options => options.UseNpgsql(connectionString));

// Key ring in the database: OIDC correlation cookies (and storage back-end credentials from M1) survive restarts.
builder.Services.AddDataProtection()
    .SetApplicationName("ASync")
    .PersistKeysToDbContext<AppDbContext>();

// ── Core services ─────────────────────────────────────────────────────────────
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddMemoryCache();
builder.Services.AddScoped<IUserRepository, UserRepository>();
builder.Services.AddScoped<IRefreshTokenRepository, RefreshTokenRepository>();
builder.Services.AddScoped<IDeviceRepository, DeviceRepository>();
builder.Services.AddScoped<IPasswordHasher<AppUser>, PasswordHasher<AppUser>>();
builder.Services.AddSingleton<PublicUrl>();
builder.Services.AddScoped<DeviceProvisioningService>();
builder.Services.AddScoped<IStorageBackendRepository, StorageBackendRepository>();
builder.Services.AddScoped<StorageBackendService>();
builder.Services.AddScoped<IFolderRepository, FolderRepository>();
builder.Services.AddScoped<FolderService>();
builder.Services.AddSingleton<FolderLeaseService>();
builder.Services.AddSingleton<FolderBackendCache>();
builder.Services.AddHostedService<FolderBackendSweeper>();
builder.Services.AddSingleton<SnapshotService>();
builder.Services.AddSingleton<PlainFileService>();
builder.Services.AddSingleton<UploadLimits>();
builder.Services.AddScoped<IUploadSessionRepository, UploadSessionRepository>();
builder.Services.AddScoped<UploadService>();
builder.Services.AddHostedService<CleanupService>();
builder.Services.AddSingleton<MaintenanceClaims>();
builder.Services.AddScoped<FolderMaintenanceService>();
builder.Services.AddHostedService<MaintenanceScheduler>();

// ── Storage usage, quotas and back-end health ─────────────────────────────────
builder.Services.AddScoped<IUsageRepository, UsageRepository>();
builder.Services.AddSingleton<UsageOptions>();
builder.Services.AddSingleton<UsageScanSignal>();
builder.Services.AddScoped<UsageService>();
builder.Services.AddHostedService<UsageScanner>();
builder.Services.AddSingleton<HealthOptions>();
builder.Services.AddScoped<StorageHealthService>();
builder.Services.AddHostedService<StorageHealthScheduler>();

// ── Deleting folders and users (purging stored data in the background) ────────
builder.Services.AddScoped<IPurgeJobRepository, PurgeJobRepository>();
builder.Services.AddScoped<PurgeService>();
builder.Services.AddScoped<AccountDeletionService>();
builder.Services.AddSingleton<PurgeSignal>();
builder.Services.AddHostedService<PurgeWorker>();

// ── Alerts (overdue detection; webhook, ntfy, e-mail) and push mode ───────────
// Alerts need no push: they work from what devices report. Push__Mode: UnifiedPush (default) or None; see PushSettings.
var pushSettings = PushSettings.FromConfiguration(builder.Configuration);
builder.Services.AddSingleton(pushSettings);
builder.Services.AddSingleton<PushQueue>();
builder.Services.AddSingleton<PushThrottle>();
builder.Services.AddScoped<PushService>();
builder.Services.AddHostedService<PushDispatcher>();
// Device-supplied endpoints: the same stance as alert webhooks (http/https only, no redirects, a short timeout).
builder.Services.AddHttpClient(PushService.HttpClientName, c => c.Timeout = pushSettings.Timeout)
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = pushSettings.Timeout });
builder.Services.AddSingleton(AlertOptions.FromConfiguration(builder.Configuration));
builder.Services.AddSingleton(SmtpOptions.FromConfiguration(builder.Configuration));
builder.Services.AddSingleton(new ServerStarted(DateTime.UtcNow));
// User-supplied addresses: short timeout, no redirects (and AlertSender only accepts http/https).
builder.Services.AddHttpClient(AlertSender.HttpClientName, c => c.Timeout = AlertSender.Timeout)
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = AlertSender.Timeout });
builder.Services.AddScoped<IAlertRepository, AlertRepository>();
builder.Services.AddScoped<IAlertSender, AlertSender>();
builder.Services.AddScoped<AlertService>();
builder.Services.AddScoped<AlertMonitor>();
builder.Services.AddHostedService<AlertScheduler>();

// ── Auth ──────────────────────────────────────────────────────────────────────
// People: a short-lived JWT access token in page memory, refreshed with an httpOnly cookie sent only to /api/auth
// (see JwtTokenService). Devices: the same kind of access token, obtained by signing a challenge with the device's
// key (see DeviceProvisioningService). The signing key is resolved per request because it is only known once the
// database is migrated (JwtKeyService may generate and store it).
builder.Services.AddSingleton<JwtKeyService>();
builder.Services.AddScoped<JwtTokenService>();
var oidc = OidcSettings.FromConfiguration(builder.Configuration);
builder.Services.AddSingleton(oidc);

var auth = builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.MapInboundClaims = false;
        o.TokenValidationParameters = JwtTokenService.ValidationParameters();
        o.Events = new JwtBearerEvents
        {
            // Browsers cannot set headers on WebSocket/SSE requests, so SignalR passes the token in the query string.
            OnMessageReceived = ctx =>
            {
                if (ctx.HttpContext.Request.Path.StartsWithSegments("/hubs")
                    && ctx.Request.Query["access_token"] is { Count: > 0 } token)
                    ctx.Token = token;
                return Task.CompletedTask;
            },
            // A revoked device's access token, and a deleted person's, stop working at once, not when they expire.
            OnTokenValidated = async ctx =>
            {
                if (ctx.Principal?.DeviceId() is Guid deviceId)
                {
                    if (!await ctx.HttpContext.RequestServices.GetRequiredService<IDeviceRepository>().IsActiveAsync(deviceId))
                        ctx.Fail("This device has been revoked.");
                }
                else if (ctx.Principal is { } person && int.TryParse(person.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)
                         && !await ctx.HttpContext.RequestServices.GetRequiredService<IUserRepository>().ExistsAsync(userId))
                {
                    ctx.Fail("This account has been deleted.");
                }
            },
        };
    });
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<JwtKeyService>((o, keys) =>
        o.TokenValidationParameters.IssuerSigningKeyResolver = (_, _, _, _) => [keys.SigningKey]);

if (oidc.Enabled)
{
    auth.AddCookie(OidcSettings.ExternalScheme, o =>
        {
            o.Cookie.Name = "async_external";
            o.Cookie.Path = "/api/auth/oidc";
            o.ExpireTimeSpan = TimeSpan.FromMinutes(5);
        })
        .AddOpenIdConnect(OidcSettings.Scheme, o =>
        {
            o.Authority = oidc.Authority;
            o.ClientId = oidc.ClientId;
            o.ClientSecret = oidc.ClientSecret;
            o.RequireHttpsMetadata = oidc.RequireHttpsMetadata;
            o.ResponseType = "code";
            o.UsePkce = true;
            o.CallbackPath = OidcSettings.CallbackPath;
            o.SignInScheme = OidcSettings.ExternalScheme;
            o.MapInboundClaims = false;
            o.GetClaimsFromUserInfoEndpoint = true;
            o.SaveTokens = false;
            o.Scope.Add("profile");
            o.Scope.Add("email");
        });
}
builder.Services.AddAuthorization();

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy(RateLimits.Auth, http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = RateLimits.AuthPermitsPerMinute, Window = TimeSpan.FromMinutes(1) }));
    o.AddPolicy(RateLimits.DeviceSignIn, http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = RateLimits.DeviceSignInPermitsPerMinute, Window = TimeSpan.FromMinutes(1) }));
});

// Behind a reverse proxy (TLS terminated there): trust X-Forwarded-For/Proto so client IPs and https links are right.
if (builder.Configuration.GetValue<bool>("TrustForwardedHeaders"))
{
    builder.Services.Configure<ForwardedHeadersOptions>(o =>
    {
        o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
        o.KnownIPNetworks.Clear();
        o.KnownProxies.Clear();
    });
}

// ── SignalR (/hubs/sync: notifications only) ──────────────────────────────────
builder.Services.AddSignalR()
    .AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddSingleton<ISyncNotifier, SyncNotifier>();
builder.Services.AddSingleton<DeviceConnections>();

// ── API ───────────────────────────────────────────────────────────────────────
// Enums travel as strings, in controllers and (from M2) SignalR alike.
builder.Services.AddControllers()
    .AddJsonOptions(o =>
    {
        o.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        // Polymorphic DTOs (storage settings) carry a "type" discriminator that need not come first.
        o.JsonSerializerOptions.AllowOutOfOrderMetadataProperties = true;
    });

// ── Health (for container orchestration / uptime checks) ──────────────────────
builder.Services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("database");

var app = builder.Build();

// ── Migrations on startup ─────────────────────────────────────────────────────
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    app.Logger.LogInformation("Applying database migrations…");
    await db.Database.MigrateAsync();
    app.Logger.LogInformation("Database migrations applied.");
    await app.Services.GetRequiredService<JwtKeyService>().InitializeAsync(app.Configuration, db, app.Logger);
}

if (app.Configuration.GetValue<bool>("TrustForwardedHeaders"))
    app.UseForwardedHeaders();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.MapControllers();
app.MapHub<SyncHub>("/hubs/sync");
app.MapHealthChecks("/health").AllowAnonymous();

// React SPA fallback — serve index.html for all non-API routes
app.MapFallbackToFile("index.html");

app.Logger.LogInformation("ASync server starting. Single sign-on is {Oidc}. Push mode: {Push}.", oidc.Enabled ? "on" : "off", pushSettings.Mode);
if (pushSettings.Warning is { } pushWarning) app.Logger.LogWarning("{Warning}", pushWarning);
app.Run();
return 0;

/// <summary>Visible to integration tests (WebApplicationFactory).</summary>
public partial class Program;

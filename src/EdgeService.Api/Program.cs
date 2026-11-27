using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http.Features;
using EdgeService.Api.Maintenance;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// The routes and backend addresses live in appsettings.json under "ReverseProxy".
builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

builder.Services.AddHealthChecks();

// SCRUM-174/175/194: maintenance flags ("design to be disabled"). In Redis when a connection string is
// set (the cluster), so every edge pod sees the same flags; in memory otherwise (local runs, tests).
builder.Services.AddSingleton(TimeProvider.System);
var redisConnection = builder.Configuration.GetConnectionString("Redis");
if (string.IsNullOrWhiteSpace(redisConnection))
{
    builder.Services.AddSingleton<IMaintenanceStore, InMemoryMaintenanceStore>();
}
else
{
    builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
    {
        var options = ConfigurationOptions.Parse(redisConnection);
        options.AbortOnConnectFail = false; // start and proxy even when Redis is down (flags fail open)
        options.ConnectTimeout = 2000;
        options.AsyncTimeout = 2000;
        return ConnectionMultiplexer.Connect(options);
    });
    builder.Services.AddSingleton<IMaintenanceStore, RedisMaintenanceStore>();
}

// Only the admin endpoints need a user: JWT from UserService, validated like the other services.
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    var signingKey = builder.Configuration["Jwt:SigningKey"];
    options.MapInboundClaims = false;
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidIssuer = builder.Configuration["Jwt:Issuer"] ?? "slopster-userservice",
        ValidateAudience = true,
        ValidAudience = builder.Configuration["Jwt:Audience"] ?? "slopster",
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        // Without a configured key no token validates, so the admin endpoints return 401.
        IssuerSigningKey = string.IsNullOrEmpty(signingKey)
            ? null
            : new SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(signingKey)),
        ClockSkew = TimeSpan.FromSeconds(30),
        RoleClaimType = "role",
    };
});
builder.Services.AddAuthorization(options =>
    options.AddPolicy(MaintenanceEndpoints.AdminPolicy, policy => policy.RequireRole("Admin")));

// Cloudflare Tunnel (cloudflared) sits in front of us and sets X-Forwarded-For/-Proto.
// The edge is only reachable from inside the cluster, so every proxy is trusted.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

// One limit per user (JWT sub) or, without a token, per client IP for every proxied route
// (SCRUM-197, ADR-0005; see RateLimitKey). The policy keeps its old name "per-ip" so the routes in
// appsettings.json are unchanged.
var rateLimit = builder.Configuration.GetSection("RateLimit");
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // The edge does not check token signatures, so a client could invent a new sub per request and
    // get a fresh per-user quota each time (also on login). This ceiling per client IP caps that,
    // whatever the token says. It is high enough for many players behind one NAT, or 50+ load-test
    // bots on one machine. The Kubernetes probes on /health are not counted.
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        context.Request.Path.StartsWithSegments("/health")
            ? RateLimitPartition.GetNoLimiter("health")
            : RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = rateLimit.GetValue("IpCeiling", 1000),
                    Window = TimeSpan.FromSeconds(rateLimit.GetValue("WindowSeconds", 10)),
                    QueueLimit = 0,
                }));

    options.AddPolicy("per-ip", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            EdgeService.Api.RateLimitKey.For(context),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = rateLimit.GetValue("PermitLimit", 100),
                Window = TimeSpan.FromSeconds(rateLimit.GetValue("WindowSeconds", 10)),
                QueueLimit = 0,
            }));
});

// SCRUM-173: traces, metrics and logs over OTLP. The collector address comes from
// OTEL_EXPORTER_OTLP_ENDPOINT (default http://localhost:4317) and the service name from OTEL_SERVICE_NAME
// (both set by the gitops chart). The edge is the first hop, so a request's trace starts here; the
// forwarded call carries the traceparent header, and the service behind joins the same trace.
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation(o => o.Filter = ctx =>
            !ctx.Request.Path.StartsWithSegments("/health") && !ctx.Request.Path.StartsWithSegments("/api/edge/health"))
        .AddSource("Yarp.ReverseProxy")   // the forwarding step
        .AddSource("System.Net.Http"))    // the call to the backend
    .WithMetrics(metrics => metrics
        .AddAspNetCoreInstrumentation()   // http.server.request.duration, per route
        .AddMeter("System.Net.Http", "System.Runtime", "Microsoft.AspNetCore.RateLimiting"))
    .WithLogging(logging => { }, options =>
    {
        options.IncludeFormattedMessage = true;
        options.IncludeScopes = true;
    })
    .UseOtlpExporter();

var app = builder.Build();

app.UseForwardedHeaders();
app.UseWebSockets(); // SignalR on /hubs/* goes through as WebSockets
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// SCRUM-194: refuse calls to a switched-off service with 503, before they are proxied.
app.UseMaintenanceGate();

// Kestrel rejects request bodies over ~28.6 MiB with 413. Admin song uploads
// (POST /api/musicservice/playlists/{id}/songs, multipart mp3) are bigger, so only that
// route gets a higher limit; every other route keeps Kestrel's default.
// Cloudflare's free plan still caps a request at 100 MB in front of us.
app.Use((context, next) =>
{
    if (IsSongUpload(context.Request)
        && context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
    {
        limit.MaxRequestBodySize = SongUploadMaxBytes;
    }
    return next(context);
});

// The edge's own endpoints live under /api/edge (public), plus /health for the Kubernetes probes.
app.MapHealthChecks("/api/edge/health");
app.MapHealthChecks("/health");
app.MapMaintenance(); // SCRUM-175: /api/edge/maintenance

// /api/<service>/<rest> -> http://<service>:8080/api/<rest> (routes in appsettings.json).
app.MapReverseProxy();

app.Run();

static bool IsSongUpload(HttpRequest request)
{
    if (!HttpMethods.IsPost(request.Method))
        return false;
    // /api/musicservice/playlists/{id}/songs
    var segments = request.Path.Value?.Trim('/').Split('/') ?? [];
    return segments is ["api", "musicservice", "playlists", { Length: > 0 }, "songs"];
}

// Exposed so integration tests can use WebApplicationFactory<Program>.
public partial class Program
{
    /// <summary>Largest song upload the edge lets through (200 MB, above Cloudflare's 100 MB cap).</summary>
    public const long SongUploadMaxBytes = 200L * 1024 * 1024;
}

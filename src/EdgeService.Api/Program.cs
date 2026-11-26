using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);

// The routes and backend addresses live in appsettings.json under "ReverseProxy".
builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

builder.Services.AddHealthChecks();

// Cloudflare Tunnel (cloudflared) sits in front of us and sets X-Forwarded-For/-Proto.
// The edge is only reachable from inside the cluster, so every proxy is trusted.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

// One limit per client IP for every proxied route ("RateLimiterPolicy": "per-ip").
var rateLimit = builder.Configuration.GetSection("RateLimit");
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("per-ip", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = rateLimit.GetValue("PermitLimit", 100),
                Window = TimeSpan.FromSeconds(rateLimit.GetValue("WindowSeconds", 10)),
                QueueLimit = 0,
            }));
});

var app = builder.Build();

app.UseForwardedHeaders();
app.UseWebSockets(); // SignalR on /hubs/* goes through as WebSockets
app.UseRateLimiter();

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

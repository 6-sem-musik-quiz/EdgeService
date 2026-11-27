using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

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

// One limit per user (JWT sub) or, without a token, per client IP for every proxied route
// (SCRUM-197, ADR-0005; see RateLimitKey). The policy keeps its old name "per-ip" so the routes in
// appsettings.json are unchanged.
var rateLimit = builder.Configuration.GetSection("RateLimit");
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
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

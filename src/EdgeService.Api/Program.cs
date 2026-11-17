using System.Threading.RateLimiting;
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

app.MapHealthChecks("/health");
app.MapReverseProxy();

app.Run();

// Exposed so integration tests can use WebApplicationFactory<Program>.
public partial class Program;

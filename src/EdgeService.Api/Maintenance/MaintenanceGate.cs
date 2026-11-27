using Microsoft.AspNetCore.Mvc;

namespace EdgeService.Api.Maintenance;

// SCRUM-194: a switched-off service is refused here, at the edge, for every caller (not only in the
// frontend): 503 Service Unavailable with ProblemDetails and Retry-After. Every public request passes
// the edge, so this is the one place that sees them all.
// Let through, so nothing running is cut off and the admin can always get back in:
//   /api/<service>/hubs/...   SignalR: running lobbies and games keep their connections
//   /api/<service>/health     probes
//   /api/<service>/admin/...  admin endpoints
//   /api/userservice/auth/... login, refresh, logout: logged-in players keep playing (refresh), and
//                             the admin can log in to switch the service back on. The frontend blocks
//                             login for players (SCRUM-177).
// Calls inside the cluster (LobbyService -> GameService when a game starts) do not pass the edge;
// GameService checks its own flag for that (SCRUM-179).
public static class MaintenanceGate
{
    public const string ProblemType = "https://quiz.munkcloud.dk/problems/maintenance";
    public const int RetryAfterSeconds = 30;

    // The service a request is for, or null if the gate does not apply to it.
    public static string? GatedService(PathString path)
    {
        var segments = path.Value?.Trim('/').Split('/') ?? [];
        if (segments.Length < 2 || segments[0] != "api" || !MaintenanceServices.All.ContainsKey(segments[1]))
            return null;
        var next = segments.Length > 2 ? segments[2] : "";
        if (next is "hubs" or "health" or "admin") return null;
        if (segments[1] == "userservice" && next == "auth") return null;
        return segments[1];
    }

    public static IApplicationBuilder UseMaintenanceGate(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (GatedService(context.Request.Path) is { } service)
            {
                var store = context.RequestServices.GetRequiredService<IMaintenanceStore>();
                var flags = await store.GetAllAsync(context.RequestAborted);
                if (flags.TryGetValue(service, out var flag))
                {
                    await WriteUnavailableAsync(context, service, flag);
                    return;
                }
            }
            await next(context);
        });

    public static Task WriteUnavailableAsync(HttpContext context, string service, MaintenanceFlag flag)
    {
        var name = MaintenanceServices.All[service];
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.Headers.RetryAfter = RetryAfterSeconds.ToString();
        var problem = new ProblemDetails
        {
            Type = ProblemType,
            Title = $"{name} is under maintenance",
            Status = StatusCodes.Status503ServiceUnavailable,
            Detail = string.IsNullOrWhiteSpace(flag.Message) ? $"{name} is under maintenance. Try again later." : flag.Message,
        };
        problem.Extensions["service"] = service;
        return context.Response.WriteAsJsonAsync(problem, (System.Text.Json.JsonSerializerOptions?)null, "application/problem+json");
    }
}

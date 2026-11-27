using System.Security.Claims;

namespace EdgeService.Api.Maintenance;

// SCRUM-175: the flags for the admin panel and the frontend.
//   GET /api/edge/maintenance            anyone: every service, on or off (the frontend polls this)
//   PUT /api/edge/maintenance/{service}  Admin only: { "off": true, "message": "..." } or { "off": false }
public static class MaintenanceEndpoints
{
    public const string AdminPolicy = "maintenance-admin";

    public sealed record ServiceStatus(string Service, string Name, bool Off, string? Message, string? By, DateTimeOffset? Since);
    public sealed record SwitchRequest(bool Off, string? Message);

    public static IEndpointRouteBuilder MapMaintenance(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/edge/maintenance", async (IMaintenanceStore store, CancellationToken ct) =>
        {
            var flags = await store.GetAllAsync(ct);
            return Results.Ok(MaintenanceServices.All.Select(s =>
                flags.TryGetValue(s.Key, out var f)
                    ? new ServiceStatus(s.Key, s.Value, true, f.Message, f.By, f.Since)
                    : new ServiceStatus(s.Key, s.Value, false, null, null, null)));
        });

        app.MapPut("/api/edge/maintenance/{service}", async (string service, SwitchRequest request, ClaimsPrincipal user,
            IMaintenanceStore store, TimeProvider time, ILoggerFactory loggers, CancellationToken ct) =>
        {
            if (!MaintenanceServices.All.ContainsKey(service))
                return Results.Problem(statusCode: 404, title: "Unknown service", detail: $"No service called '{service}'.");
            var message = request.Message?.Trim();
            if (message is { Length: > 300 })
                return Results.Problem(statusCode: 400, title: "Message too long", detail: "At most 300 characters.");

            var by = user.FindFirstValue("unique_name") ?? user.FindFirstValue("sub") ?? "admin";
            if (request.Off)
                await store.SwitchOffAsync(service, new MaintenanceFlag(string.IsNullOrEmpty(message) ? null : message, by, time.GetUtcNow()), ct);
            else
                await store.SwitchOnAsync(service, ct);
            loggers.CreateLogger("Maintenance").LogInformation("{Admin} switched {Service} {State}", by, service, request.Off ? "off" : "on");
            return Results.NoContent();
        }).RequireAuthorization(AdminPolicy);

        return app;
    }
}

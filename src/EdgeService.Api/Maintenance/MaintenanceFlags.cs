using System.Text.Json;
using StackExchange.Redis;

namespace EdgeService.Api.Maintenance;

// SCRUM-174/175/194 ("design to be disabled", AKF Scalability Rules): an admin can switch each backend
// service off. The flags live in Redis, so they hold for every edge pod and survive restarts, and
// change without a deploy. The edge is never switched off itself, so the admin can always switch a
// service back on.
public static class MaintenanceServices
{
    // The route prefix under /api (the YARP route id) and the name shown to users.
    public static readonly IReadOnlyDictionary<string, string> All = new Dictionary<string, string>
    {
        ["userservice"] = "User",
        ["lobbyservice"] = "Lobby",
        ["gameservice"] = "Game",
        ["musicservice"] = "Music",
        ["statisticservice"] = "Statistics",
    };
}

// A service in maintenance: the optional message for users, who switched it off and when.
public sealed record MaintenanceFlag(string? Message, string By, DateTimeOffset Since);

public interface IMaintenanceStore
{
    // Only the services that are switched off. Fails open: if the store cannot be read, nothing is off.
    Task<IReadOnlyDictionary<string, MaintenanceFlag>> GetAllAsync(CancellationToken ct = default);
    Task SwitchOffAsync(string service, MaintenanceFlag flag, CancellationToken ct = default);
    Task SwitchOnAsync(string service, CancellationToken ct = default);
}

// One Redis hash, slopster:maintenance, with one field per switched-off service (JSON value).
// Reads are cached for CacheFor, so Redis is not hit on every proxied request; a change reaches every
// pod within that time. A write clears this pod's cache at once.
public sealed class RedisMaintenanceStore(IConnectionMultiplexer redis, TimeProvider time, ILogger<RedisMaintenanceStore> logger)
    : IMaintenanceStore
{
    public const string Key = "slopster:maintenance";
    public static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(2);

    private readonly SemaphoreSlim _refresh = new(1, 1);
    private IReadOnlyDictionary<string, MaintenanceFlag> _cached = new Dictionary<string, MaintenanceFlag>();
    private DateTimeOffset _readAt = DateTimeOffset.MinValue;

    public async Task<IReadOnlyDictionary<string, MaintenanceFlag>> GetAllAsync(CancellationToken ct = default)
    {
        if (time.GetUtcNow() - _readAt < CacheFor) return _cached;
        await _refresh.WaitAsync(ct);
        try
        {
            if (time.GetUtcNow() - _readAt < CacheFor) return _cached;
            try
            {
                var entries = await redis.GetDatabase().HashGetAllAsync(Key);
                var flags = new Dictionary<string, MaintenanceFlag>();
                foreach (var e in entries)
                    if (JsonSerializer.Deserialize<MaintenanceFlag>(e.Value.ToString()) is { } flag)
                        flags[e.Name.ToString()] = flag;
                _cached = flags;
            }
            catch (Exception e) when (e is RedisException or TimeoutException or JsonException)
            {
                // Fail open: a Redis problem must not switch the whole game off.
                logger.LogWarning(e, "Could not read the maintenance flags; treating every service as on.");
                _cached = new Dictionary<string, MaintenanceFlag>();
            }
            _readAt = time.GetUtcNow();
            return _cached;
        }
        finally
        {
            _refresh.Release();
        }
    }

    public async Task SwitchOffAsync(string service, MaintenanceFlag flag, CancellationToken ct = default)
    {
        await redis.GetDatabase().HashSetAsync(Key, service, JsonSerializer.Serialize(flag));
        _readAt = DateTimeOffset.MinValue;
    }

    public async Task SwitchOnAsync(string service, CancellationToken ct = default)
    {
        await redis.GetDatabase().HashDeleteAsync(Key, service);
        _readAt = DateTimeOffset.MinValue;
    }
}

// Without a Redis connection string (local runs, tests): flags in memory, one pod only.
public sealed class InMemoryMaintenanceStore : IMaintenanceStore
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, MaintenanceFlag> _flags = new();

    public Task<IReadOnlyDictionary<string, MaintenanceFlag>> GetAllAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<string, MaintenanceFlag>>(new Dictionary<string, MaintenanceFlag>(_flags));

    public Task SwitchOffAsync(string service, MaintenanceFlag flag, CancellationToken ct = default)
    {
        _flags[service] = flag;
        return Task.CompletedTask;
    }

    public Task SwitchOnAsync(string service, CancellationToken ct = default)
    {
        _flags.TryRemove(service, out _);
        return Task.CompletedTask;
    }
}

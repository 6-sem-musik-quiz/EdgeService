using System.Text;
using Microsoft.Extensions.Options;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.LoadBalancing;
using Yarp.ReverseProxy.Model;

namespace EdgeService.Api;

// ADR-0004 (Platform, SCRUM-129): every game lives in one GameService pod (StatefulSet
// gameservice-0..N-1), and the edge sends everything for a game to that pod:
//   shard(gameId) = FNV-1a-32(UTF-8(gameId.ToString("D").ToLowerInvariant())) % N
// The same function as GameService's GameShard; N must be the same in both (Sharding__ShardCount there,
// GameSharding__ShardCount here).

// The "GameSharding" section (env GameSharding__ShardCount ...).
public sealed class GameShardingOptions
{
    public const string SectionName = "GameSharding";

    // N. 1 = no sharding: the gameservice cluster keeps its one destination from "ReverseProxy".
    public int ShardCount { get; set; } = 1;

    // Address of shard k; {0} is k. The headless Service gives every StatefulSet pod its own name.
    public string AddressTemplate { get; set; } = "http://gameservice-{0}.gameservice-headless:8080/";

    // Optional explicit addresses, index = shard (tests use these instead of the template).
    public List<string> Addresses { get; set; } = [];

    public string AddressOf(int shard) =>
        shard < Addresses.Count ? Addresses[shard] : string.Format(AddressTemplate, shard);
}

public static class GameShard
{
    public const string ClusterId = "gameservice";
    public const string PolicyName = "GameShard";

    public static uint Fnv1a32(Guid gameId)
    {
        var hash = 2166136261u;
        foreach (var b in Encoding.UTF8.GetBytes(gameId.ToString("D").ToLowerInvariant()))
            hash = unchecked((hash ^ b) * 16777619u);
        return hash;
    }

    public static int Of(Guid gameId, int shardCount) => (int)(Fnv1a32(gameId) % (uint)shardCount);

    public static string DestinationId(int shard) => $"shard-{shard}";

    // gameservice.gamehub@1.3: ?gameId= on the hub URL (negotiate and WebSocket keep the query).
    // REST calls for one game: /api/gameservice/games/{gameId}/...
    public static Guid? GameIdOf(HttpRequest request)
    {
        if (Guid.TryParse(request.Query["gameId"].ToString(), out var fromQuery))
            return fromQuery;

        var segments = request.Path.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries) ?? [];
        for (var i = 0; i < segments.Length - 1; i++)
            if (segments[i].Equals("games", StringComparison.OrdinalIgnoreCase)
                && Guid.TryParse(segments[i + 1], out var fromPath))
                return fromPath;
        return null;
    }
}

// With ShardCount > 1, replaces the gameservice cluster's destinations with shard-0..N-1 and gives it
// the GameShard policy. With 1 the configuration is left as it is.
public sealed class GameShardConfigFilter(IOptions<GameShardingOptions> options) : IProxyConfigFilter
{
    public ValueTask<ClusterConfig> ConfigureClusterAsync(ClusterConfig cluster, CancellationToken cancel)
    {
        var sharding = options.Value;
        if (cluster.ClusterId != GameShard.ClusterId || sharding.ShardCount <= 1)
            return ValueTask.FromResult(cluster);

        var destinations = Enumerable.Range(0, sharding.ShardCount).ToDictionary(
            GameShard.DestinationId,
            shard => new DestinationConfig { Address = sharding.AddressOf(shard) },
            StringComparer.OrdinalIgnoreCase);
        return ValueTask.FromResult(cluster with
        {
            Destinations = destinations,
            LoadBalancingPolicy = GameShard.PolicyName
        });
    }

    public ValueTask<RouteConfig> ConfigureRouteAsync(RouteConfig route, ClusterConfig? cluster, CancellationToken cancel) =>
        ValueTask.FromResult(route);
}

// Picks the shard that owns the request's game. Without a gameId (e.g. LobbyService-style calls that
// create a game) any shard will do. If the owner is not available, no destination: YARP answers 503,
// which is better than a pod that does not know the game.
public sealed class GameShardPolicy(IOptions<GameShardingOptions> options) : ILoadBalancingPolicy
{
    public string Name => GameShard.PolicyName;

    public DestinationState? PickDestination(HttpContext context, ClusterState cluster,
        IReadOnlyList<DestinationState> availableDestinations)
    {
        if (availableDestinations.Count == 0)
            return null;

        if (GameShard.GameIdOf(context.Request) is not { } gameId)
            return availableDestinations[Random.Shared.Next(availableDestinations.Count)];

        var owner = GameShard.DestinationId(GameShard.Of(gameId, options.Value.ShardCount));
        foreach (var destination in availableDestinations)
            if (string.Equals(destination.DestinationId, owner, StringComparison.OrdinalIgnoreCase))
                return destination;
        return null;
    }
}

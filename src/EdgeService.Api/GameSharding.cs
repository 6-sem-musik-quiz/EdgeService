using System.Globalization;
using Microsoft.Extensions.Options;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.LoadBalancing;
using Yarp.ReverseProxy.Model;

namespace EdgeService.Api;

// ADR-0004 (Platform, SCRUM-129, gameservice.gamehub@1.4): every game lives in the GameService pod that
// created it (StatefulSet gameservice-0..N-1), and that pod's number is the first two hex characters of
// the gameId (02a7f3c1-... = gameservice-2). The edge reads it and sends the game's traffic there.
// The edge does not need the number of pods, only an upper bound (MaxShards).

// The "GameSharding" section (env GameSharding__Enabled ...).
public sealed class GameShardingOptions
{
    public const string SectionName = "GameSharding";

    // false = the gameservice cluster keeps its one destination from "ReverseProxy" (one pod, local dev).
    public bool Enabled { get; set; }

    // Destinations shard-0..MaxShards-1 are created. At least the HPA's maxReplicas; at most 256.
    public int MaxShards { get; set; } = 16;

    // Address of pod k; {0} is k. The headless Service gives every StatefulSet pod its own name.
    public string AddressTemplate { get; set; } = "http://gameservice-{0}.gameservice-headless:8080/";

    // For requests without a gameId: the normal Service, which only sends to ready pods.
    public string AnyAddress { get; set; } = "http://gameservice:8080/";

    // Optional explicit addresses, index = shard (tests use these instead of the template).
    public List<string> Addresses { get; set; } = [];

    public string AddressOf(int shard) =>
        shard < Addresses.Count ? Addresses[shard] : string.Format(CultureInfo.InvariantCulture, AddressTemplate, shard);
}

public static class GameShard
{
    public const string ClusterId = "gameservice";
    public const string PolicyName = "GameShard";
    public const string AnyDestination = "any";

    // The first two hex characters of the gameId ("D" format), 0-255.
    public static int Of(Guid gameId) =>
        int.Parse(gameId.ToString("D").AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    public static string DestinationId(int shard) => $"shard-{shard}";

    // gameservice.gamehub@1.3+: ?gameId= on the hub URL (negotiate and WebSocket keep the query).
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

// When enabled, replaces the gameservice cluster's destinations with shard-0..MaxShards-1 plus "any"
// and gives it the GameShard policy. Disabled: the configuration is left as it is.
public sealed class GameShardConfigFilter(IOptions<GameShardingOptions> options) : IProxyConfigFilter
{
    public ValueTask<ClusterConfig> ConfigureClusterAsync(ClusterConfig cluster, CancellationToken cancel)
    {
        var sharding = options.Value;
        if (cluster.ClusterId != GameShard.ClusterId || !sharding.Enabled)
            return ValueTask.FromResult(cluster);
        if (sharding.MaxShards is < 1 or > 256)
            throw new InvalidOperationException($"GameSharding:MaxShards must be 1..256, was {sharding.MaxShards}.");

        var destinations = Enumerable.Range(0, sharding.MaxShards).ToDictionary(
            GameShard.DestinationId,
            shard => new DestinationConfig { Address = sharding.AddressOf(shard) },
            StringComparer.OrdinalIgnoreCase);
        destinations[GameShard.AnyDestination] = new DestinationConfig { Address = sharding.AnyAddress };

        return ValueTask.FromResult(cluster with
        {
            Destinations = destinations,
            LoadBalancingPolicy = GameShard.PolicyName
        });
    }

    public ValueTask<RouteConfig> ConfigureRouteAsync(RouteConfig route, ClusterConfig? cluster, CancellationToken cancel) =>
        ValueTask.FromResult(route);
}

// Picks the pod named in the request's gameId. Without a gameId: "any" (the normal Service).
// A gameId naming a pod above MaxShards gets no destination, and YARP answers 503.
public sealed class GameShardPolicy : ILoadBalancingPolicy
{
    public string Name => GameShard.PolicyName;

    public DestinationState? PickDestination(HttpContext context, ClusterState cluster,
        IReadOnlyList<DestinationState> availableDestinations)
    {
        var wanted = GameShard.GameIdOf(context.Request) is { } gameId
            ? GameShard.DestinationId(GameShard.Of(gameId))
            : GameShard.AnyDestination;

        foreach (var destination in availableDestinations)
            if (string.Equals(destination.DestinationId, wanted, StringComparison.OrdinalIgnoreCase))
                return destination;
        return null;
    }
}

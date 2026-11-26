using System.Net;
using EdgeService.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;

namespace EdgeService.Api.Tests;

// ADR-0004: with GameSharding:ShardCount = 3, a request for a game goes to shard
// FNV-1a-32(lowercase gameId) % 3. Three fake GameService pods answer "shard-<k> <path>".
public sealed class GameShardingTests : IAsyncLifetime
{
    private const int Shards = 3;
    private readonly List<WebApplication> _pods = [];

    public async Task InitializeAsync()
    {
        for (var k = 0; k < Shards; k++)
        {
            var name = $"shard-{k}";
            var pod = WebApplication.CreateBuilder().Build();
            pod.Urls.Add("http://127.0.0.1:0");
            pod.Run(context => context.Response.WriteAsync($"{name} {context.Request.Path}"));
            await pod.StartAsync();
            _pods.Add(pod);
        }
    }

    public async Task DisposeAsync()
    {
        foreach (var pod in _pods)
        {
            await pod.StopAsync();
            await pod.DisposeAsync();
        }
    }

    private HttpClient Edge(int shardCount = Shards) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("GameSharding:ShardCount", shardCount.ToString());
            for (var k = 0; k < _pods.Count; k++)
                builder.UseSetting($"GameSharding:Addresses:{k}", _pods[k].Urls.First() + "/");
            // With one shard the cluster keeps its own destination: point it at pod 0.
            builder.UseSetting("ReverseProxy:Clusters:gameservice:Destinations:d1:Address", _pods[0].Urls.First() + "/");
        }).CreateClient();

    // Same reference values as GameService's GameShardTests (computed from the ADR in Python).
    [Theory]
    [InlineData("00000000-0000-0000-0000-000000000000", 3192360657u)]
    [InlineData("3F1C2A8E-0000-4000-8000-000000000001", 2145607093u)]
    public void Fnv1a32_MatchesTheContract(string gameId, uint expected) =>
        Assert.Equal(expected, GameShard.Fnv1a32(Guid.Parse(gameId)));

    [Fact]
    public async Task HubConnection_WithGameId_GoesToTheGamesShard()
    {
        using var edge = Edge();

        for (var i = 0; i < 30; i++)
        {
            var gameId = Guid.NewGuid();
            var response = await edge.PostAsync($"/api/gameservice/hubs/game/negotiate?gameId={gameId}&negotiateVersion=1", null);
            var body = await response.Content.ReadAsStringAsync();

            Assert.Equal($"shard-{GameShard.Of(gameId, Shards)} /api/hubs/game/negotiate", body);
        }
    }

    [Fact]
    public async Task RestCall_ForOneGame_GoesToTheGamesShard()
    {
        using var edge = Edge();

        for (var i = 0; i < 30; i++)
        {
            var gameId = Guid.NewGuid();
            var body = await edge.GetStringAsync($"/api/gameservice/games/{gameId}");

            Assert.Equal($"shard-{GameShard.Of(gameId, Shards)} /api/games/{gameId}", body);
        }
    }

    [Fact]
    public async Task UpperCaseGameId_GoesToTheSameShard()
    {
        using var edge = Edge();
        var gameId = Guid.NewGuid();

        var lower = await edge.GetStringAsync($"/api/gameservice/games/{gameId}");
        var upper = await edge.GetStringAsync($"/api/gameservice/games/{gameId.ToString().ToUpperInvariant()}");

        Assert.Equal(lower.Split(' ')[0], upper.Split(' ')[0]);
    }

    [Fact]
    public async Task WithoutGameId_AnyShardAnswers()
    {
        using var edge = Edge();

        var body = await edge.GetStringAsync("/api/gameservice/health");

        Assert.Matches(@"^shard-[0-2] /api/health$", body);
    }

    [Fact]
    public async Task OneShard_KeepsTheConfiguredDestination()
    {
        using var edge = Edge(shardCount: 1);

        var response = await edge.GetAsync($"/api/gameservice/games/{Guid.NewGuid()}?gameId={Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("shard-0 ", await response.Content.ReadAsStringAsync());
    }
}

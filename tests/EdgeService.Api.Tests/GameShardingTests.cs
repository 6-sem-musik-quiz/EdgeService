using System.Net;
using EdgeService.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;

namespace EdgeService.Api.Tests;

// ADR-0004 / gameservice.gamehub@1.4: the first two hex characters of the gameId name the GameService
// pod. Three fake pods answer "shard-<k> <path>", and a fake normal Service answers "any <path>".
public sealed class GameShardingTests : IAsyncLifetime
{
    private const int Pods = 3;
    private readonly List<WebApplication> _backends = [];
    private readonly List<string> _podAddresses = [];
    private string _anyAddress = "";

    public async Task InitializeAsync()
    {
        for (var k = 0; k <= Pods; k++)
        {
            var name = k < Pods ? $"shard-{k}" : "any";
            var backend = WebApplication.CreateBuilder().Build();
            backend.Urls.Add("http://127.0.0.1:0");
            backend.Run(context => context.Response.WriteAsync($"{name} {context.Request.Path}"));
            await backend.StartAsync();
            _backends.Add(backend);
            if (k < Pods) _podAddresses.Add(backend.Urls.First() + "/");
            else _anyAddress = backend.Urls.First() + "/";
        }
    }

    public async Task DisposeAsync()
    {
        foreach (var backend in _backends)
        {
            await backend.StopAsync();
            await backend.DisposeAsync();
        }
    }

    private HttpClient Edge(bool enabled = true, int maxShards = Pods) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("GameSharding:Enabled", enabled.ToString());
            builder.UseSetting("GameSharding:MaxShards", maxShards.ToString());
            builder.UseSetting("GameSharding:AnyAddress", _anyAddress);
            for (var k = 0; k < _podAddresses.Count; k++)
                builder.UseSetting($"GameSharding:Addresses:{k}", _podAddresses[k]);
            // Disabled: the cluster keeps its own destination; point it at the "any" backend.
            builder.UseSetting("ReverseProxy:Clusters:gameservice:Destinations:d1:Address", _anyAddress);
        }).CreateClient();

    private static Guid GameOn(int pod) => Guid.Parse(pod.ToString("x2") + Guid.NewGuid().ToString("D")[2..]);

    [Theory]
    [InlineData("00a7f3c1-5b2e-4d0a-9c1f-7e3b2a1d4c5e", 0)]
    [InlineData("02a7f3c1-5b2e-4d0a-9c1f-7e3b2a1d4c5e", 2)]
    [InlineData("1Fa7f3c1-5b2e-4d0a-9c1f-7e3b2a1d4c5e", 31)]
    [InlineData("ffa7f3c1-5b2e-4d0a-9c1f-7e3b2a1d4c5e", 255)]
    public void Of_ReadsTheFirstTwoHexCharacters(string gameId, int expected) =>
        Assert.Equal(expected, GameShard.Of(Guid.Parse(gameId)));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task HubConnection_GoesToThePodInTheGameId(int pod)
    {
        using var edge = Edge();
        var gameId = GameOn(pod);

        var response = await edge.PostAsync($"/api/gameservice/hubs/game/negotiate?gameId={gameId}&negotiateVersion=1", null);

        Assert.Equal($"shard-{pod} /api/hubs/game/negotiate", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task RestCall_ForOneGame_GoesToThePodInTheGameId(int pod)
    {
        using var edge = Edge();
        var gameId = GameOn(pod);

        Assert.Equal($"shard-{pod} /api/games/{gameId}", await edge.GetStringAsync($"/api/gameservice/games/{gameId}"));
    }

    [Fact]
    public async Task WithoutGameId_GoesToTheNormalService()
    {
        using var edge = Edge();

        Assert.Equal("any /api/games", await (await edge.PostAsync("/api/gameservice/games", null)).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task GameIdAboveMaxShards_Gets503()
    {
        using var edge = Edge(maxShards: Pods);

        var response = await edge.GetAsync($"/api/gameservice/games/{GameOn(Pods)}"); // pod 3 of 0..2

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Disabled_KeepsTheConfiguredDestination()
    {
        using var edge = Edge(enabled: false);

        Assert.StartsWith("any ", await edge.GetStringAsync($"/api/gameservice/games/{GameOn(2)}"));
    }
}

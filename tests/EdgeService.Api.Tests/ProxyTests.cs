using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;

namespace EdgeService.Api.Tests;

/// <summary>
/// Starts one fake backend per service on a random local port and points the
/// edge's clusters at them, so the tests check the real routing table.
/// </summary>
public sealed class ProxyTests : IAsyncLifetime
{
    private static readonly string[] Services =
        ["userservice", "lobbyservice", "gameservice", "musicservice", "statisticservice"];

    private readonly List<WebApplication> _backends = [];
    private readonly Dictionary<string, string> _addresses = [];

    public async Task InitializeAsync()
    {
        foreach (var service in Services)
        {
            var backend = WebApplication.CreateBuilder().Build();
            backend.Urls.Add("http://127.0.0.1:0");
            backend.UseWebSockets();
            backend.Run(async context =>
            {
                if (context.WebSockets.IsWebSocketRequest)
                {
                    // Sends "<service>" over the socket, like a SignalR hub would answer.
                    using var socket = await context.WebSockets.AcceptWebSocketAsync();
                    await socket.SendAsync(System.Text.Encoding.UTF8.GetBytes(service),
                        System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);
                    await socket.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                    return;
                }
                // Answers "<service> <path>" so a test can see where a request landed.
                await context.Response.WriteAsync($"{service} {context.Request.Path}");
            });
            await backend.StartAsync();
            _backends.Add(backend);
            _addresses[service] = backend.Urls.First() + "/";
        }
    }

    public async Task DisposeAsync()
    {
        foreach (var backend in _backends)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await backend.StopAsync(timeout.Token);
            await backend.DisposeAsync();
        }
    }

    private WebApplicationFactory<Program> Edge(int permitLimit = 100) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production"); // use appsettings.json, not the localhost ports
            builder.UseSetting("RateLimit:PermitLimit", permitLimit.ToString());
            foreach (var (service, address) in _addresses)
                builder.UseSetting($"ReverseProxy:Clusters:{service}:Destinations:d1:Address", address);
        });

    [Theory]
    [InlineData("/api/users/42", "userservice /api/users/42")]
    [InlineData("/api/users", "userservice /api/users")]
    [InlineData("/api/lobbies/abc/join", "lobbyservice /api/lobbies/abc/join")]
    [InlineData("/api/games/1", "gameservice /api/games/1")]
    [InlineData("/api/tracks/random", "musicservice /api/tracks/random")]
    [InlineData("/api/statistics/me", "statisticservice /api/statistics/me")]
    [InlineData("/hubs/lobby/negotiate", "lobbyservice /hubs/lobby/negotiate")]
    [InlineData("/hubs/game/negotiate", "gameservice /hubs/game/negotiate")]
    public async Task Route_ForwardsToTheRightService_WithThePathUnchanged(string path, string expected)
    {
        await using var edge = Edge();

        var response = await edge.CreateClient().GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expected, await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/api/unknown/1")]
    [InlineData("/swagger")]
    public async Task UnknownPath_Returns404(string path)
    {
        await using var edge = Edge();

        var response = await edge.CreateClient().GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Health_IsAnsweredByTheEdgeItself()
    {
        await using var edge = Edge();

        var response = await edge.CreateClient().GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/hubs/lobby", "lobbyservice")]
    [InlineData("/hubs/game", "gameservice")]
    public async Task Hub_WebSocketIsProxiedToTheRightService(string path, string expected)
    {
        // The in-memory test server cannot do a real HTTP upgrade, so this test runs the edge on Kestrel.
        await using var edge = Edge();
        edge.UseKestrel(0);
        edge.StartServer();
        var wsBase = new UriBuilder(edge.ClientOptions.BaseAddress) { Scheme = "ws" }.Uri;

        using var socket = new System.Net.WebSockets.ClientWebSocket();
        await socket.ConnectAsync(new Uri(wsBase, path), CancellationToken.None);
        var buffer = new byte[64];
        var result = await socket.ReceiveAsync(buffer, CancellationToken.None);

        Assert.Equal(expected, System.Text.Encoding.UTF8.GetString(buffer, 0, result.Count));

        // Finish the close handshake, or the servers wait for the socket when they shut down.
        await socket.ReceiveAsync(buffer, CancellationToken.None);
        await socket.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
    }

    [Fact]
    public async Task TooManyRequests_Returns429()
    {
        await using var edge = Edge(permitLimit: 2);
        var client = edge.CreateClient();

        await client.GetAsync("/api/users/1");
        await client.GetAsync("/api/users/1");
        var third = await client.GetAsync("/api/users/1");

        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
    }
}

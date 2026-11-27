using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using EdgeService.Api.Maintenance;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;
using StackExchange.Redis;

namespace EdgeService.Api.Tests;

// SCRUM-174/175/194: maintenance flags at the edge ("design to be disabled").
// No Redis connection string here, so the edge keeps the flags in memory. Backends are not running:
// a request the gate lets through ends in 502 from YARP, which is "not 503" for these tests.
public sealed class MaintenanceTests : IDisposable
{
    private const string Key = "test-signing-key-for-the-edge-at-least-32-bytes";
    private readonly WebApplicationFactory<Program> _edge = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
    {
        b.UseEnvironment("Production");
        b.UseSetting("Jwt:SigningKey", Key);
        b.UseSetting("ConnectionStrings:Redis", "");
        foreach (var s in MaintenanceServices.All.Keys) // nothing listens here
            b.UseSetting($"ReverseProxy:Clusters:{s}:Destinations:d1:Address", "http://127.0.0.1:9/");
    });

    public void Dispose() => _edge.Dispose();

    private static string Token(string role, string name = "asger")
    {
        var creds = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Key)), SecurityAlgorithms.HmacSha256);
        var jwt = new JwtSecurityToken("slopster-userservice", "slopster",
            [new Claim("sub", "1"), new Claim("unique_name", name), new Claim("role", role)],
            expires: DateTime.UtcNow.AddMinutes(5), signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(jwt);
    }

    private HttpClient Client(string? role = null)
    {
        var client = _edge.CreateClient();
        if (role is not null) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token(role));
        return client;
    }

    private Task<HttpResponseMessage> Switch(string service, bool off, string? message = null, string role = "Admin") =>
        Client(role).PutAsJsonAsync($"/api/edge/maintenance/{service}", new { off, message });

    private async Task<JsonElement[]> Flags() =>
        (await Client().GetFromJsonAsync<JsonElement[]>("/api/edge/maintenance"))!;

    [Fact]
    public async Task Flags_ListEveryService_AllOnAtFirst()
    {
        var flags = await Flags();

        Assert.Equal(["userservice", "lobbyservice", "gameservice", "musicservice", "statisticservice"],
            flags.Select(f => f.GetProperty("service").GetString()));
        Assert.All(flags, f => Assert.False(f.GetProperty("off").GetBoolean()));
    }

    [Fact]
    public async Task OnlyAnAdmin_CanSwitch()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client().PutAsJsonAsync("/api/edge/maintenance/lobbyservice", new { off = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Switch("lobbyservice", true, role: "Player")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Switch("lobbyservice", true, "Back at 20:00")).StatusCode);

        var lobby = (await Flags()).Single(f => f.GetProperty("service").GetString() == "lobbyservice");
        Assert.True(lobby.GetProperty("off").GetBoolean());
        Assert.Equal("Back at 20:00", lobby.GetProperty("message").GetString());
        Assert.Equal("asger", lobby.GetProperty("by").GetString());
    }

    [Fact]
    public async Task UnknownService_Is404()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await Switch("nosuchservice", true)).StatusCode);
    }

    [Fact]
    public async Task ASwitchedOffService_Is503_WithProblemDetails_AndRetryAfter()
    {
        await Switch("lobbyservice", true, "Back at 20:00");

        var response = await Client("Player").GetAsync("/api/lobbyservice/lobbies");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(TimeSpan.FromSeconds(MaintenanceGate.RetryAfterSeconds), response.Headers.RetryAfter?.Delta);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(MaintenanceGate.ProblemType, problem.GetProperty("type").GetString());
        Assert.Equal("Back at 20:00", problem.GetProperty("detail").GetString());
        Assert.Equal("lobbyservice", problem.GetProperty("service").GetString());
    }

    [Fact]
    public async Task OtherServices_AndSwitchingBackOn_AreNotAffected()
    {
        await Switch("lobbyservice", true);

        Assert.NotEqual(HttpStatusCode.ServiceUnavailable, (await Client().GetAsync("/api/gameservice/games/1")).StatusCode);

        await Switch("lobbyservice", false);
        Assert.NotEqual(HttpStatusCode.ServiceUnavailable, (await Client().GetAsync("/api/lobbyservice/lobbies")).StatusCode);
    }

    [Theory]
    [InlineData("/api/lobbyservice/hubs/lobby", "lobbyservice")]   // running lobbies keep their connection
    [InlineData("/api/gameservice/hubs/game", "gameservice")]      // running games too
    [InlineData("/api/musicservice/health", "musicservice")]
    [InlineData("/api/userservice/admin/users", "userservice")]
    [InlineData("/api/userservice/auth/login", "userservice")]     // the admin can still log in
    [InlineData("/api/userservice/auth/refresh", "userservice")]   // logged-in players keep playing
    public async Task Exceptions_PassTheGate(string path, string service)
    {
        await Switch(service, true);

        var response = await Client().PostAsync(path, null);

        Assert.NotEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/userservice/users")]   // registering
    [InlineData("/api/userservice/users/me")]
    public async Task UserService_IsBlocked_ExceptAuth(string path)
    {
        await Switch("userservice", true);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Client().PostAsync(path, null)).StatusCode);
    }

    [Fact]
    public async Task TheEdgesOwnEndpoints_AreNeverGated()
    {
        foreach (var s in MaintenanceServices.All.Keys) await Switch(s, true);

        Assert.Equal(HttpStatusCode.OK, (await Client().GetAsync("/api/edge/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Client().GetAsync("/api/edge/maintenance")).StatusCode);
    }

    [Fact]
    public async Task UnreachableRedis_FailsOpen()
    {
        var options = ConfigurationOptions.Parse("127.0.0.1:1");
        options.AbortOnConnectFail = false;
        options.ConnectTimeout = 200;
        options.AsyncTimeout = 200;
        await using var redis = await ConnectionMultiplexer.ConnectAsync(options);
        var store = new RedisMaintenanceStore(redis, TimeProvider.System, NullLogger<RedisMaintenanceStore>.Instance);

        Assert.Empty(await store.GetAllAsync()); // nothing is switched off
    }
}

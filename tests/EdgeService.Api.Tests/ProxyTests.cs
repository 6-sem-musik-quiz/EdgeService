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
            var builder = WebApplication.CreateBuilder();
            // Like MusicService's upload endpoint, the fake backends take any body size,
            // so a 413 in a test can only come from the edge.
            builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = null);
            var backend = builder.Build();
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
                await context.Request.Body.CopyToAsync(Stream.Null);
                // SCRUM-173: hands back the trace context the edge forwarded.
                context.Response.Headers["X-Seen-Traceparent"] = context.Request.Headers["traceparent"].ToString();
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

    private WebApplicationFactory<Program> Edge(int permitLimit = 100, int ipCeiling = 1000) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production"); // use appsettings.json, not the localhost ports
            builder.UseSetting("RateLimit:PermitLimit", permitLimit.ToString());
            builder.UseSetting("RateLimit:IpCeiling", ipCeiling.ToString());
            foreach (var (service, address) in _addresses)
                builder.UseSetting($"ReverseProxy:Clusters:{service}:Destinations:d1:Address", address);
        });

    [Theory]
    [InlineData("/api/userservice/users/42", "userservice /api/users/42")]
    [InlineData("/api/lobbyservice/lobbies/abc/join", "lobbyservice /api/lobbies/abc/join")]
    [InlineData("/api/gameservice/games/1", "gameservice /api/games/1")]
    [InlineData("/api/musicservice/tracks/random", "musicservice /api/tracks/random")]
    [InlineData("/api/statisticservice/statistics/me", "statisticservice /api/statistics/me")]
    [InlineData("/api/gameservice/hubs/game/negotiate", "gameservice /api/hubs/game/negotiate")]
    [InlineData("/api/userservice/example?id=1", "userservice /api/example")]
    public async Task Route_ForwardsToTheService_WithoutTheServicePrefix(string path, string expected)
    {
        await using var edge = Edge();

        var response = await edge.CreateClient().GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expected, await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/api/unknown/1")]
    [InlineData("/api/users/1")]
    [InlineData("/hubs/game")]
    [InlineData("/swagger")]
    public async Task UnknownPath_Returns404(string path)
    {
        await using var edge = Edge();

        var response = await edge.CreateClient().GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/edge/health")]
    [InlineData("/health")]
    public async Task Health_IsAnsweredByTheEdgeItself(string path)
    {
        await using var edge = Edge();

        var response = await edge.CreateClient().GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/api/lobbyservice/hubs/lobby", "lobbyservice")]
    [InlineData("/api/gameservice/hubs/game", "gameservice")]
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

    // Above Kestrel's default MaxRequestBodySize (30,000,000 bytes, ~28.6 MiB).
    private const int LargeBodyBytes = 31 * 1024 * 1024;

    [Theory]
    [InlineData("/api/musicservice/playlists/7/songs", "musicservice /api/playlists/7/songs")]
    [InlineData("/api/musicservice/playlists/3fa85f64-5717-4562-b3fc-2c963f66afa6/songs", "musicservice /api/playlists/3fa85f64-5717-4562-b3fc-2c963f66afa6/songs")]
    public async Task SongUpload_AcceptsBodiesOverTheDefaultLimit(string path, string expected)
    {
        // The in-memory test server has no body size limit, so this test runs the edge on Kestrel.
        await using var edge = Edge();
        edge.UseKestrel(0);
        edge.StartServer();

        var response = await edge.CreateClient().PostAsync(path, LargeBody());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expected, await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/api/userservice/users")]
    [InlineData("/api/musicservice/tracks")]
    [InlineData("/api/musicservice/playlists/7")]
    [InlineData("/api/musicservice/playlists/7/songs/extra")]
    public async Task OtherRoutes_KeepTheDefaultBodyLimit(string path)
    {
        await using var edge = Edge();
        edge.UseKestrel(0);
        edge.StartServer();

        // With "Expect: 100-continue" the client waits for the edge before sending the body, so it
        // reads the 413 instead of racing Kestrel closing the connection mid-upload.
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = LargeBody() };
        request.Headers.ExpectContinue = true;
        var response = await edge.CreateClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    private static ByteArrayContent LargeBody()
    {
        var content = new ByteArrayContent(new byte[LargeBodyBytes]);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/mpeg");
        return content;
    }

    // SCRUM-173: the edge is the first hop. The backend joins the caller's trace, with the edge's own span as parent.
    [Fact]
    public async Task ForwardedRequest_CarriesTheTrace_WithTheEdgeAsParent()
    {
        const string traceId = "4bf92f3577b34da6a3ce929d0e0e4736", callerSpan = "00f067aa0ba902b7";
        await using var edge = Edge();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/gameservice/games/1");
        request.Headers.Add("traceparent", $"00-{traceId}-{callerSpan}-01");

        var response = await edge.CreateClient().SendAsync(request);

        var seen = response.Headers.GetValues("X-Seen-Traceparent").Single().Split('-');
        Assert.Equal(traceId, seen[1]);
        Assert.NotEqual(callerSpan, seen[2]);
    }

    [Fact]
    public async Task TooManyRequests_Returns429()
    {
        await using var edge = Edge(permitLimit: 2);
        var client = edge.CreateClient();

        await client.GetAsync("/api/userservice/users/1");
        await client.GetAsync("/api/userservice/users/1");
        var third = await client.GetAsync("/api/userservice/users/1");

        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
    }

    // SCRUM-197: the limit is per user (JWT sub) when a token is sent, so 50 load-test bots
    // behind one IP each get their own quota.
    [Fact]
    public async Task TwoUsers_FromSameIp_HaveSeparateLimits()
    {
        await using var edge = Edge(permitLimit: 2);
        var client = edge.CreateClient();

        await Send(client, "/api/userservice/users/1", Jwt("1"));
        await Send(client, "/api/userservice/users/1", Jwt("1"));
        var otherUser = await Send(client, "/api/userservice/users/1", Jwt("2"));

        Assert.Equal(HttpStatusCode.OK, otherUser.StatusCode);
    }

    [Fact]
    public async Task SameUser_IsLimited()
    {
        await using var edge = Edge(permitLimit: 2);
        var client = edge.CreateClient();

        await Send(client, "/api/userservice/users/1", Jwt("1"));
        await Send(client, "/api/userservice/users/1", Jwt("1"));
        var third = await Send(client, "/api/userservice/users/1", Jwt("1"));

        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
    }

    [Fact]
    public async Task AccessTokenQuery_CountsAsUser()
    {
        await using var edge = Edge(permitLimit: 2);
        var client = edge.CreateClient();

        await client.GetAsync($"/api/gameservice/hubs/game/negotiate?access_token={Jwt("1")}");
        await client.GetAsync($"/api/gameservice/hubs/game/negotiate?access_token={Jwt("1")}");
        var otherUser = await Send(client, "/api/gameservice/hubs/game/negotiate", Jwt("2"));

        Assert.Equal(HttpStatusCode.OK, otherUser.StatusCode);
    }

    [Fact]
    public async Task GarbageToken_FallsBackToIp()
    {
        await using var edge = Edge(permitLimit: 2);
        var client = edge.CreateClient();

        var first = await Send(client, "/api/userservice/users/1", "not.a.jwt");
        await Send(client, "/api/userservice/users/1", "garbage");
        var third = await Send(client, "/api/userservice/users/1", "not.a.jwt");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
    }

    // Anyone can mint a token with a new sub per request (the edge does not check signatures), so a
    // per-IP ceiling caps an IP however many subs it invents. Login and sign-up stay protected.
    [Fact]
    public async Task ForgedSubs_FromOneIp_HitTheIpCeiling()
    {
        await using var edge = Edge(permitLimit: 100, ipCeiling: 3);
        var client = edge.CreateClient();

        for (var i = 1; i <= 3; i++)
            Assert.Equal(HttpStatusCode.OK, (await Send(client, "/api/userservice/auth/login", Jwt($"{i}"))).StatusCode);
        var fourth = await Send(client, "/api/userservice/auth/login", Jwt("4"));

        Assert.Equal(HttpStatusCode.TooManyRequests, fourth.StatusCode);
    }

    [Fact]
    public async Task Health_IsNotCountedAgainstTheIpCeiling()
    {
        await using var edge = Edge(ipCeiling: 1);
        var client = edge.CreateClient();

        await client.GetAsync("/health");
        var second = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
    }

    private static Task<HttpResponseMessage> Send(HttpClient client, string path, string bearer)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
        return client.SendAsync(request);
    }

    // A JWT with only a sub claim. The edge never checks the signature, so any signature will do.
    private static string Jwt(string sub)
    {
        static string B64(string json) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{B64("{\"alg\":\"HS256\",\"typ\":\"JWT\"}")}.{B64($"{{\"sub\":\"{sub}\"}}")}.c2lnbmF0dXJl";
    }
}

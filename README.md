# EdgeService

The API gateway for Slopster, built with [YARP](https://learn.microsoft.com/aspnet/core/fundamentals/servers/yarp/yarp-overview).
It is the only backend the internet reaches: Cloudflare Tunnel sends
`https://quiz.munkcloud.dk/api/*` and `/hubs/*` here, and the edge forwards each request
to a private service inside the cluster. The path is passed on unchanged.

## Routes

| Path | Service | In the cluster | Locally |
|---|---|---|---|
| `/api/users/**` | UserService | `http://userservice:8080` | `http://localhost:5001` |
| `/api/tracks/**` | MusicService | `http://musicservice:8080` | `http://localhost:5002` |
| `/api/statistics/**` | StatisticService | `http://statisticservice:8080` | `http://localhost:5003` |
| `/api/lobbies/**` | LobbyService | `http://lobbyservice:8080` | `http://localhost:5004` |
| `/hubs/lobby/**` | LobbyService (SignalR) | | |
| `/api/games/**` | GameService | `http://gameservice:8080` | `http://localhost:5005` |
| `/hubs/game/**` | GameService (SignalR) | | |

Anything else returns 404. `/health` is answered by the edge itself.

So each service puts its controllers under its own prefix, e.g. `[Route("api/users")]` in
UserService. A new route or prefix is a cross-service contract: agree on it in
`6-sem-musik-quiz/Platform` first, then add it in [`appsettings.json`](src/EdgeService.Api/appsettings.json).

## What the edge does

- **Routing** by path prefix (YARP, config in `appsettings.json` under `ReverseProxy`).
- **WebSockets** for SignalR on `/hubs/*`.
- **Rate limiting**: 100 requests per 10 seconds per client IP (`RateLimit` in
  `appsettings.json`), then `429 Too Many Requests`.
- **Forwarded headers**: the client's real IP and `https` from Cloudflare are passed on to the
  services as `X-Forwarded-For` / `X-Forwarded-Proto`.

Authentication is not done here yet.

## Run locally

```bash
dotnet run --project src/EdgeService.Api
```

The edge listens on `http://localhost:5000` and forwards to the services on their own
local ports (`appsettings.Development.json`). Start the services you need first.

## Build and test

```bash
dotnet build -c Release
dotnet test -c Release
```

The tests start fake backends and check every route, the WebSocket path, 404 and the rate limit.

## Deploy

CI pushes `ghcr.io/6-sem-musik-quiz/edgeservice:<run>-<sha>-<unix time>` on every push to
`main`, and Flux deploys it from `6-sem-musik-quiz/gitops` (`apps/edgeservice.yaml`, 2 replicas).

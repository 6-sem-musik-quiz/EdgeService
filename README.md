# EdgeService

The API gateway for Slopster, built with [YARP](https://learn.microsoft.com/aspnet/core/fundamentals/servers/yarp/yarp-overview).
It is the only backend the internet reaches. `https://quiz.munkcloud.dk/` is the game
(the frontend); Cloudflare Tunnel sends everything under `https://quiz.munkcloud.dk/api/`
here, and the edge forwards it to a private service inside the cluster.

## Routes

`/api/<service>/<rest>` goes to `http://<service>:8080/api/<rest>`: the edge removes the
service name, so a service keeps its normal `api/[controller]` routes and never knows its
public prefix.

| Public path | Service | Becomes | Locally |
|---|---|---|---|
| `/api/edge/health` | the edge itself | | `http://localhost:5000` |
| `/api/userservice/**` | UserService | `/api/**` on `userservice:8080` | `http://localhost:5001` |
| `/api/musicservice/**` | MusicService | `/api/**` on `musicservice:8080` | `http://localhost:5002` |
| `/api/statisticservice/**` | StatisticService | `/api/**` on `statisticservice:8080` | `http://localhost:5003` |
| `/api/lobbyservice/**` | LobbyService | `/api/**` on `lobbyservice:8080` | `http://localhost:5004` |
| `/api/gameservice/**` | GameService | `/api/**` on `gameservice:8080` | `http://localhost:5005` |

Examples:

- `GET /api/musicservice/tracks/random` → MusicService `GET /api/tracks/random`
- SignalR: a hub mapped at `/api/hubs/game` in GameService is reached at
  `/api/gameservice/hubs/game` (WebSockets work).

Anything else returns 404. `/health` (not public) is for the Kubernetes probes.

A new service or prefix is a cross-service contract: agree on it in
`6-sem-musik-quiz/Platform` first, then add it in [`appsettings.json`](src/EdgeService.Api/appsettings.json)
and a test case in `ProxyTests`.

## What the edge does

- **Routing** by service name (YARP, config in `appsettings.json` under `ReverseProxy`).
- **WebSockets** for SignalR hubs.
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

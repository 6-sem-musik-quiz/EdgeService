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
- **No special routing for GameService:** its games are in Redis
  ([ADR-0004](https://github.com/6-sem-musik-quiz/Platform/blob/main/docs/decisions/0004-gameservice-scaling.md)),
  so any GameService pod can serve any request, like the other services.
- **Rate limiting**: 100 requests per 10 seconds (`RateLimit` in `appsettings.json`), then
  `429 Too Many Requests`. Counted per user (the JWT's `sub`, from `Authorization: Bearer` or
  `?access_token=`) and per client IP for requests without a token (SCRUM-197, ADR-0005,
  `RateLimitKey.cs`). The token is only read, not validated; the services validate it. Because a
  client could invent a new `sub` per request, every client IP also has a ceiling of 1000 requests per
  10 seconds (`RateLimit:IpCeiling`), whatever the token says; `/health` is not counted.
- **Forwarded headers**: the client's real IP and `https` from Cloudflare are passed on to the
  services as `X-Forwarded-For` / `X-Forwarded-Proto`.
- **Request body size**: Kestrel's default limit (30,000,000 bytes, ~28.6 MiB, then
  `413 Payload Too Large`) applies to every route except admin song uploads,
  `POST /api/musicservice/playlists/{id}/songs`, which may send up to 200 MB
  (`SongUploadMaxBytes` in `Program.cs`). In production Cloudflare's free plan still caps
  every request body at **100 MB** before it reaches the tunnel, so that is the real
  maximum for an upload; it is not configurable on the free plan.

Authentication is not done here yet.

## Observability (SCRUM-173)

Traces, metrics and logs go over OTLP (`UseOtlpExporter`; `OTEL_EXPORTER_OTLP_ENDPOINT` and `OTEL_SERVICE_NAME`
come from the gitops chart). The edge is the first hop, so a request's trace starts here: the server span
(not `/health`), YARP's forwarding span (`Yarp.ReverseProxy`, tagged `proxy.route_id`) and the call to the backend
(`System.Net.Http`). The forwarded request carries `traceparent`, so the service behind joins the same trace.
Metrics: request duration per route, runtime, HttpClient and rate limiting (`Microsoft.AspNetCore.RateLimiting`,
including rejected requests).

## Run locally

```bash
dotnet run --project src/EdgeService.Api
```

The edge listens on `http://localhost:5000` and forwards to the services on their own
local ports (`appsettings.Development.json`). Start the services you need first.

## Testing a feature locally (frontend + edge + one service)

Run the frontend, the edge and the service you are changing in Docker. Every other service
stays the real one in the cluster. Each repo has its own compose file, and the containers
reach each other through your machine's ports (`host.docker.internal`).

```
browser -> Frontend :5006 -> edge :5000 -> your service :500x          (local override)
                                       \-> https://quiz.munkcloud.dk  (every other service, live data)
```

1. **The service you are changing**, in its own repo (it uses the real cluster database; see
   that repo's README for the one-time `.env` setup):
   ```powershell
   docker compose up --build          # e.g. UserService on :5001
   ```
2. **The edge**, here, with one override per service you run locally:
   ```powershell
   docker compose -f docker-compose.yml -f local/userservice.yml up --build
   ```
   Overrides stack, e.g. `-f local/userservice.yml -f local/musicservice.yml`.
   Without any override the edge forwards everything to the real cluster.
3. **The frontend**, in the Frontend repo:
   ```powershell
   docker compose -f docker-compose.yml -f docker-compose.local-edge.yml up --build
   ```
4. Open http://localhost:5006.

| Override | Sends `/api/<service>/*` to |
|---|---|
| `local/userservice.yml` | `localhost:5001` |
| `local/musicservice.yml` | `localhost:5002` |
| `local/statisticservice.yml` | `localhost:5003` |
| `local/lobbyservice.yml` | `localhost:5004` |
| `local/gameservice.yml` | `localhost:5005` |

Calls to services that are not overridden hit the **real** data through `https://quiz.munkcloud.dk`.

## Build and test

```bash
dotnet build -c Release
dotnet test -c Release
```

The tests start fake backends and check every route, the WebSocket path, 404, the rate limit
and the larger body limit on the song-upload route.

## Deploy

CI pushes `ghcr.io/6-sem-musik-quiz/edgeservice:<run>-<sha>-<unix time>` on every push to
`main`, and Flux deploys it from `6-sem-musik-quiz/gitops` (`apps/edgeservice.yaml`, 2 replicas).

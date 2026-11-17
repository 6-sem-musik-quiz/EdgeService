# AGENTS.md — Slopster / EdgeService

This .NET 10 repository is the YARP API gateway for the `6-sem-musik-quiz` exam project. Read `README.md` for the routing table, ports and commands. Cross-service approved knowledge lives in `6-sem-musik-quiz/Platform` (see `docs/drift.md` there for where it runs); the separate `AgentBlackboard` provides only transient coordination.

## Layout

- `src/EdgeService.Api`: `Program.cs` (YARP, rate limiting, forwarded headers, health) and `appsettings*.json` (routes and clusters).
- `tests/EdgeService.Api.Tests`: fake backends per service; every route needs a test case.

There is no Service/Repository/Domain layer: the edge holds no business logic and no database. Keep it that way; logic belongs in the services.

## Rules

- A new route or path prefix is a cross-service contract. Change it in a Platform PR first, then here, and add a test case in `ProxyTests`.
- Cluster addresses in `appsettings.json` are the Kubernetes service names (`http://<service>:8080/`); local ports are in `appsettings.Development.json`.
- Do not expose internal endpoints (e.g. `/swagger`) through the edge.

## Build and tests

```bash
dotnet build -c Release
dotnet test -c Release
```

## Four-agent workflow

1. Read the assigned Jira issue in project `SCRUM`, its acceptance criteria and blocking relationships; do not grab unrelated tickets.
2. Read the latest injected Platform contract/ADR context **before** planning. Treat the Platform mirror as read-only. Submit contract changes in a separate Platform PR before editing dependent services.
3. Declare affected paths using `RepoName:path/**`, and have the **worker** claim them against the latest blackboard before edits; if any claim overlaps, stop. An LLM statement is not a lock.
4. Keep changed files inside your claim. Do not edit another agent's active note.
5. Work on an isolated branch/worktree, run tests, and open a PR for human review. Do not push to `main`, auto-merge, or set Jira Done yourself.
6. Report test results honestly, including failures. Never commit secrets, private tokens or local environment files.

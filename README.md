# Code Evolver

Code Evolver is an event-driven service that accepts a repository, direction, scope, and target branch, then advances the request through scan, plan, implementation, review, gate, and pull-request phases.

## Run locally

Prerequisites: .NET 10 SDK, Node.js 22 or newer, and npm.

Start the API and web app together. The script opens the app after it is ready:

```powershell
.\start-code-evolver.ps1
```

Pass `-NoBrowser` to start both services without opening a browser. To run each service manually:

```powershell
dotnet run --project .\src\CodeEvolver.Api
npm run dev --prefix .\src\code-evolver-web
```

Open http://localhost:5173. Runtime state is stored in `src/CodeEvolver.Api/data/evolutions.json` by default. Install and authenticate the standalone GitHub Copilot CLI before starting the API. Copilot is the default agent and runs non-interactively in the selected repository, where it can edit files and run commands. The final phase attempts a commit, push, and pull request; Git credentials and repository hosting access must already be configured. The UI's Stop action prevents pending phases from starting, but cannot interrupt a CLI phase already in progress.

For UI development without autonomous repository changes, set `Agent__Provider=local` to use the lifecycle simulator explicitly. Set `Agent__Copilot__MaxAiCredits` only when a per-phase credit limit is required.

## Use Cassandra

Docker is only required for the Cassandra profile:

```powershell
docker compose -f .\docker-compose.cassandra.yml up -d
$env:Storage__Provider = "cassandra"
dotnet run --project .\src\CodeEvolver.Api
```

The service creates the `code_evolver` keyspace and `evolutions` table on startup. Contact points, port, and keyspace can be changed under `Storage:Cassandra` in `appsettings.json` or through standard ASP.NET Core environment variables.

## Validate

```powershell
dotnet test .\CodeEvolver.slnx
npm run build --prefix .\src\code-evolver-web
```

The API is at http://localhost:5278, with health status at `/api/health` and OpenAPI metadata at `/openapi/v1.json` in development.
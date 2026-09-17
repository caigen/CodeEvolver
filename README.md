# Code Evolver

Code Evolver is an event-driven service that turns either a human direction or a data-derived recommendation into autonomous repository work. It advances each evolution through scan, plan, implementation, review, gate, and pull-request phases while exposing live agent status, prompts, logs, results, errors, and quiet-time heartbeats in the UI.

## Product workflow

The workspace is organized into two horizontal areas:

```text
Analyzer  | Analyzer Team and Analysis Timeline
Evolution | Evolution Team and Event Timeline
```

The Analyzer accepts CSV or JSON files up to 5 MB. Purpose, Insight, Evolution Direction, and Summary agents run in order using a bounded profile of at most 1,000 rows and 100 columns. Raw row values are not sent to Copilot. The resulting direction and key points can be applied to the evolution form.

The Evolution area accepts a cloned repository path, direction, scope, and target branch. Use the native folder button to select an absolute repository path; the browser remembers the latest path locally. Selecting a saved evolution refills the form. After an evolution starts, its agents and timeline update with detailed GitHub Copilot activity until completion, failure, or a human Stop request.

## Run locally

Prerequisites: .NET 10 SDK, Node.js 22 or newer, and npm.

Install the web app dependencies once after cloning:

```powershell
npm ci --prefix .\src\code-evolver-web
```

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

For UI development without autonomous repository changes, set `Agent__Provider=local` to use the lifecycle simulator explicitly. Set `Agent__Copilot__MaxAiCredits` only when a per-phase credit limit is required; current Copilot CLI versions require at least 30 credits.

## Self-host safely

Run Code Evolver against its own isolated worktree with publishing disabled so the final phase does not commit, push, or open a pull request. The Copilot CLI must be installed and authenticated, and the worktree must not contain credentials.

In one PowerShell terminal:

```powershell
npm ci --prefix .\src\code-evolver-web
$env:Agent__Copilot__PublishChanges = "false"
$env:Agent__Copilot__ReadOnly = "true"
$env:Agent__Copilot__MaxAiCredits = "30"
$env:Agent__Copilot__SecretEnvironmentVariables = "GH_TOKEN,GITHUB_TOKEN,COPILOT_GITHUB_TOKEN"
dotnet run --project .\src\CodeEvolver.Api --no-launch-profile --urls http://127.0.0.1:5278
```

In a second terminal at the same worktree:

```powershell
$repository = (git rev-parse --show-toplevel)
$request = @{
    repositoryPath = $repository
    direction = "Perform a no-change self-hosting verification. Inspect README.md, plan exactly one verification work item, do not edit any file in any phase, and report whether the documented prerequisites and validation commands are coherent."
    scope = "README.md"
    targetBranch = "main"
} | ConvertTo-Json

$evolution = Invoke-RestMethod -Method Post -Uri http://127.0.0.1:5278/api/evolutions -ContentType application/json -Body $request
Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:5278/api/evolutions/$($evolution.id)/start"
```

Poll `GET /api/evolutions/{id}` until `status` is `completed` or `failed`. Runtime records remain in the ignored `src/CodeEvolver.Api/data/evolutions.json` file; a sibling lock file serializes mutations when multiple service instances target the same worktree. Read-only mode fingerprints tracked and untracked workspace state around every phase and fails the evolution if the agent changes it. The protected `HumanDesign` directory is fingerprinted for every run. No-publish mode disables built-in GitHub tools and denies the normal Git commit, push, and pull-request commands. Omit `Agent__Copilot__ReadOnly=true` when an evolution should edit files, and omit `Agent__Copilot__PublishChanges=false` only when the worktree is ready for Code Evolver to commit, push an `evolution/<id>` branch, and open a pull request.

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
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

## Record real agent execution

One-time setup (Node.js 22 or newer; Windows with an installed English desktop
text-to-speech voice for narration):

```powershell
npm ci --prefix .\src\code-evolver-web
npm run demo:install --prefix .\src\code-evolver-web
```

This command starts real Copilot agents and can consume
credits and modify web UI source in the separate `D:\work\CodeEvolver` clone
specified by `HumanDesign/DemoGuide.txt`:

```powershell
npm run demo:real --prefix .\src\code-evolver-web
```

This is the single recording, editing, and validation flow. It automatically
produces a four-section video of at most 60 seconds: purpose, Analyzer Agent
Team, Code Evolver Agent Team, and GitHub. Real agent execution and raw footage
can take longer; only the final exported video is limited to one minute.
Narration is measured before starting agents and rejected if it exceeds the
59-second edit budget. Both final exports are checked against the 60-second limit.

To reuse an existing real recording without running agents, use the same command:

```powershell
npm run demo:real --prefix .\src\code-evolver-web -- --from .\artifacts\demo\real-<timestamp>
```

Re-edits go into a new `edit-<timestamp>` subfolder, preserving previous exports.

Prerequisites: authenticated standalone `copilot.exe`, .NET 10 SDK, Node.js,
installed Playwright Chromium, and a Windows English desktop speech voice.
The D-drive clone must exist and have its frontend dependencies installed:
`npm ci --prefix D:\work\CodeEvolver\src\code-evolver-web`.
Set `DEMO_COPILOT_EXECUTABLE` to the full standalone executable path if needed.
The VS Code `copilot` wrapper is not used because it may return plain text
instead of the streamed JSON the API expects.

Narration is synthesized locally with Windows PowerShell and System.Speech.
Set `DEMO_VOICE` to choose an installed English desktop voice; otherwise the
first available English voice is selected. Dependencies include FFmpeg;
optionally set `FFMPEG_PATH` to a compatible executable. If MP4 narration is
silent in an editor preview, use the voiced WebM or a system media player.

The recorder builds and starts its own API on an available loopback port, with
isolated state under `artifacts/demo/real-<timestamp>/api`. Publishing is disabled;
agents are instructed not to stage, commit, push, create branches or pull
requests, or modify existing unrelated changes. Each invocation is limited to
30 AI credits and 15 minutes. These limits are per invocation, not the whole
run. The recording application runs from this checkout, but agent edits target
only the D-drive clone. Review that clone before running and review all resulting
changes afterward. Its before/after patches are retained with the video.

The story introduces direction or data as inputs, shows both agent teams, then
demonstrates actual analyzer and evolution results. Update `scripts/narration.json`
and `scripts/demo-feedback.json` first to revise the story. The current feedback
asks for user-flow bug fixes and a better experience. The closing scene links to
https://github.com/caigen/CodeEvolver.

The analyzer sees a bounded structural profile, not raw feedback row values.
Its actual recommendation is recorded, then the DemoGuide requirements are
explicitly entered as human direction. API responses and agent events are not
mocked. Hot reload is disabled while agents edit; the page reloads at the end.

Outputs include the original continuous silent recording in `raw/`, actual
`analysis.json` and `evolution.json`, before/after patches, captured frames, and
`code-evolver-real-demo.mp4` plus `code-evolver-real-demo-with-voice.webm`.
Final videos are labeled edited highlights: waits between captured scenes are
removed and scenes hold their last frame while local English narration plays.
They are not continuous real-time recordings. A failed final evolution is shown
as failed, exports its evidence when possible, and exits with a nonzero status.
Setup or analysis failures may leave raw footage and `failure.json` only.
All recorder-owned servers and browsers are stopped on exit.

To check UI filtering/export and browser audio using a saved run (a post-run
replay check, not additional live agent execution):

```powershell
node .\src\code-evolver-web\scripts\verify-real-demo.mjs .\artifacts\demo\real-<timestamp>
```

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
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

## Record an automated demo

Record the real web UI using isolated, simulated API responses. No API server,
Copilot authentication, real repository, or database is required. This is a
product walkthrough, not evidence of real agent execution or backend validation.

One-time setup (Node.js 22 or newer; Windows with an installed English desktop
text-to-speech voice for narration):

```powershell
npm ci --prefix .\src\code-evolver-web
npm run demo:install --prefix .\src\code-evolver-web
```

Record and edit a new video:

```powershell
npm run demo:record --prefix .\src\code-evolver-web
```

The script starts an isolated Vite server on an available local port, records
Chromium, and closes both automatically. Before recording, it uses Windows
PowerShell and System.Speech to synthesize seven English narration clips locally.
No speech API, credentials, or cloud upload is involved. Following
`HumanDesign/DemoGuide.txt`, it uploads `scripts/demo-feedback.json` requesting
GitHub-style colors and selection controls, applies the recommendation, and adds
a human request for searchable agent logs with CSV export. The evolution target
is the current repository root (resolved automatically), scoped to
`src/code-evolver-web`. It steps through simulated scan, plan, work, review,
gate, and completion states, then displays https://github.com/caigen/CodeEvolver.
These are proposed work items; the recording does not implement these features.
All API requests are intercepted; no files are changed by agents and no commits,
pushes, or pull requests are created. External requests are blocked; Google Fonts
uses the application's fallback fonts so recording does not require that service.

Each run creates a timestamped folder under `artifacts/demo/` (ignored by Git):

- `code-evolver-demo.mp4`: 1920 x 1080, 30 fps H.264 with a dedicated subtitle
    band, chapter captions, a persistent simulation label, fade in/out, and an
    English AAC narration track.
- `code-evolver-demo-with-voice.webm`: VP9 video with Opus narration, provided
    for editor and browser players that do not support AAC audio.
- `raw/`: original silent WebM recording, retained for further editing or
    debugging. Narration is added only to the final exports above.
- `narration/`: individual WAV clips and `narration.json` containing the voice,
  spoken text, and measured audio durations.
- `chapters.srt` and `chapters.json`: captions and recording timeline.
- `demo-feedback.json`: a copy of the uploaded feedback.
- `visit-code-evolver.html`: the closing screen with a clickable project link.
- Numbered PNG screenshots and `preview.png`: visual review evidence.

FFmpeg removes startup footage, encodes the final video, and checks that it can
be decoded. The recording also fails on unexpected API requests, browser errors,
missing expected UI states, missing audio, or silent narration. Each chapter is
held long enough for its narration to finish before the next chapter starts.
Timing depends on the selected voice and machine speed. Music is not included.

If the MP4 appears silent in an editor preview, try the final
`code-evolver-demo-with-voice.webm` or open the MP4 in a system media player.
Check that playback is unmuted. Do not use the silent file under `raw/` for
the narrated demo. The voiced WebM is not generated with `DEMO_NARRATION=off`.

The first available English desktop voice (sorted by name) is selected by
default. To select a particular installed voice:

```powershell
$env:DEMO_VOICE = 'Microsoft David Desktop'
npm run demo:record --prefix .\src\code-evolver-web
Remove-Item Env:DEMO_VOICE
```

Only voices exposed to Windows desktop System.Speech are supported. If none is
available, install an English Windows text-to-speech voice and rerun. The script
reports an error instead of silently producing an unvoiced video. For the
original silent workflow (also usable on non-Windows systems):

```powershell
$env:DEMO_NARRATION = 'off'
npm run demo:record --prefix .\src\code-evolver-web
Remove-Item Env:DEMO_NARRATION
```

Edit `src/code-evolver-web/scripts/record-demo.mjs` to change the sample data,
chapter text, actions, or pauses. Chapter text is English to match the UI.
Edit the seven `text` entries in `src/code-evolver-web/scripts/narration.json`
to change the spoken script. Keep its chapter titles in sync with the recorder;
audio durations and chapter holds are recalculated automatically on each run.
Dependencies include a local FFmpeg binary; optionally set `FFMPEG_PATH` to your
own executable built with `libx264` and the `subtitles` filter. Initial npm and
Chromium installation require internet access. If recording fails, fix the
reported issue and rerun; existing outputs are retained in their own folders.

## Record real agent execution

Unlike `demo:record`, this command starts real Copilot agents and can consume
credits and modify the current repository's web UI source:

```powershell
npm run demo:real --prefix .\src\code-evolver-web
```

Prerequisites: authenticated standalone `copilot.exe`, .NET 10 SDK, Node.js,
installed Playwright Chromium, and a Windows English desktop speech voice.
Set `DEMO_COPILOT_EXECUTABLE` to the full standalone executable path if needed.
The VS Code `copilot` wrapper is not used because it may return plain text
instead of the streamed JSON the API expects.

The recorder builds and starts its own API on an available loopback port, with
isolated state under `artifacts/demo/real-<timestamp>/api`. Publishing is disabled;
agents are instructed not to stage, commit, push, create branches or pull
requests, or modify existing unrelated changes. Each invocation is limited to
30 AI credits and 15 minutes. These limits are per invocation, not the whole
run. The target is the current checkout, not an isolated worktree; review your
worktree before running and review all resulting changes afterward.

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
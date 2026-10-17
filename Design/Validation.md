# Delivery acceptance evidence

Verification date: 2026-10-17 UTC (local Windows logs extend into 2026-10-18).

## Environment

- Windows; Git Bash terminal.
- Isolated user-level .NET SDK 10.0.402 / runtime 10.0.13 and Node 22.23.3.
- MySQL 8.4.11 test process with a new data directory and loopback port 13307.
- Existing MySQL84 service on port 3306 and its databases were not used or altered.
- Tests used LocalAgentRunner. No Copilot credits, repository edits by Copilot,
  commits by Copilot, pushes, PR creation or merges were performed.

## Results

| Verification | Result |
| --- | --- |
| Backend suite (xUnit/VSTest) | 51 passed / 0 failed / 0 skipped |
| MySQL independent-connection integration | Passed, including atomic claims and stale-owner fencing |
| 30-cycle and 50-cycle bounds | Passed; all historical work items retained |
| Pause/resume, stop, approvals, safety and retries | Passed |
| Interrupted writes and read-only backoff | Passed |
| Durable analysis, safe legacy import | Passed |
| API-only control-plane behavior and invalid Origin rejection | Passed |
| Worker compilation | Passed |
| TypeScript + Vite build / Oxlint | Passed |
| npm audit | 0 vulnerabilities |
| Edge desktop/mobile browser flow | Passed |
| UI closed + API stopped | Worker advanced persisted events **10 → 28** |
| Worker restart | Dispatch pause survived; checkpoint resumed to completion |
| Analyzer page reload | Four-stage saved result restored |
| PowerShell and recorder syntax | Passed |
| VS Code diagnostics | No errors found |
| HumanDesign diff | Unchanged |

The repeatable E2E script is
[verify-durable.mjs](../src/code-evolver-web/scripts/verify-durable.mjs).
It starts and owns its test API, Worker and Vite processes, uses a dedicated test
database, asserts progress while UI/API are absent, then cleans up those processes.
The separately created temporary MySQL instance was explicitly shut down afterward.

Local evidence (Git ignored): `.runtime/evidence/durable-e2e.json`, desktop/mobile
screenshots and test TRX files under the test project's TestResults directory.

## Honest acceptance boundaries

1. **30 implementation/review deliverables are not 30 live Copilot runs.** Runtime
   30/50-cycle coverage is simulated; the real CLI's credentials, costs and outputs
   need an account-specific smoke test before unattended use.
2. A failed quality gate blocks finalization. A parsed agent report is still an
   agent report, not independent proof that every repository test actually ran.
3. MySQL provides atomic metadata transitions and stale-owner fencing. Arbitrary
   file, Git and network side effects are not exactly-once or automatically undone.
4. Windows Service installation was not run because it requires a deliberately
   selected service identity and administrator action. The installer parses and
   the Worker builds; boot/logoff resilience depends on actual service deployment.
5. The local API is not a remotely authenticated service. Do not proxy/tunnel it
   to untrusted clients. Tool restrictions and fingerprint checks are not a sandbox.
6. Data is durable but aggregate storage reads whole histories; high-throughput
   distributed scheduling and unlimited log growth are not claimed.

## Starting real work after delivery

Use a dedicated local MySQL database, securely configure
`ConnectionStrings__CodeEvolver` for API and Worker, authenticate Copilot CLI under
the intended Worker account, and follow [README](../README.md). Start with a small
cycle budget and publishing disabled before enabling unattended multi-cycle work.
The local credentials file was neither inspected nor used; no secrets were added
to Git. All implementation commits are local; nothing was pushed remotely.
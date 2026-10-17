# Durable evolution redesign — 30 iterations

Goal: intermittent human control, continuous background collaboration, durable progress.
HumanDesign is human-owned and will not be modified. Local credentials are excluded.

Each numbered item is a bounded implementation/review iteration, not a claim that a
live Copilot agent has already performed 30 repository evolutions. Runtime cycles
are separately configurable (1–50). Verification results are recorded at the end.

| Round | Deliverable |
| --- | --- |
| 01 | Architecture boundaries and acceptance plan |
| 02 | Durable lifecycle, cycles, leases and validation models |
| 03 | Atomic store mutation contract and test-store concurrency |
| 04 | MySQL schema, versioned persistence and transactions |
| 05 | MySQL claims, repository reservations and lease recovery |
| 06 | Persisted worker controls and heartbeat records |
| 07 | Human commands with checkpoint-preserving transitions |
| 08 | Resumable phase execution and fenced progress writes |
| 09 | Continuous cycles, budgets, approvals and retry policy |
| 10 | Dedicated service host and shared registration |
| 11 | Durable data-analysis submission and history |
| 12 | Agent handoffs and phase-aware publishing policy |
| 13 | Service control API, readiness and error semantics |
| 14 | Evolution control API and paginated event access |
| 15 | UI service control and connectivity separation |
| 16 | UI lifecycle controls and continuous-run policy |
| 17 | UI history recovery, bounded polling and responsive styling |
| 18 | Independent launch and Windows service deployment |
| 19 | MySQL provisioning and legacy JSON import |
| 20 | Lifecycle regression and checkpoint tests |
| 21 | Concurrency, retry and lease-fencing tests |
| 22 | MySQL integration tests against an isolated database |
| 23 | API integration and validation tests |
| 24 | Frontend build and static checks |
| 25 | Browser control-plane smoke test |
| 26 | Background independence and restart verification |
| 27 | Security and failure-path review |
| 28 | Architecture and operational documentation |
| 29 | Full regression and final fixes |
| 30 | Acceptance review and delivery evidence |

## Acceptance criteria

- MySQL is the default and only supported production store; no silent JSON fallback.
- API/UI do not execute jobs. A separately supervised Worker consumes saved jobs.
- Closing UI or stopping API never cancels Worker; pausing dispatch is durable.
- Save state, prompts, attempts, logs, results, commands, approvals and cycle history.
- Atomic claims and fenced leases prevent stale workers from committing results.
- Resume retains history; crash recovery retries read-only phases automatically,
  but ambiguous write/publish phases require human acknowledgement.
- Per-repository reservation serializes agent roles; separate repositories can be
  handled by multiple workers. No claim of parallel writers in one checkout.
- Publishing is opt-in and requires a fresh approval for every cycle.
- Tests run in an isolated local database, never an existing application database.

## Verification — completed

Rounds 01–30 are delivered as the 30 bounded design/implementation/review items
above, grouped into 21 local Git commits (not 30 independent live Copilot runs).
Final acceptance details are in [Validation.md](Validation.md).

- .NET 10 solution/Worker compilation succeeded; final test build had no compiler warnings.
- 51 backend tests passed, 0 failed, 0 skipped, including real MySQL integration.
- 30-cycle and 50-cycle simulation tests retained all work history and respected budgets.
- Frontend TypeScript/Vite production build and Oxlint passed; npm audit reported 0 vulnerabilities.
- Browser E2E passed in Edge, including 390px mobile and 1440px desktop layouts.
- With the UI closed and API stopped, persisted event count advanced from 10 to 28.
- Worker restart preserved dispatch pause and checkpoints; the 10-cycle run completed.
- Uploaded Analyzer data completed four persisted roles and survived page reload.
- PowerShell launcher/installer and JavaScript recorder syntax checks passed.
- HumanDesign unchanged; local mysql.txt ignored and not read/committed; no remote push.

Not performed: actual Copilot-authenticated coding/publishing, Windows Service
installation under a dedicated account, or a full narrated real-demo recording.
These require the target account's credentials and are not implied by simulated tests.
Temporary API/Worker/browser/database test processes were stopped after verification.
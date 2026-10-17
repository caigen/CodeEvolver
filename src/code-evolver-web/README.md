# Code Evolver control console

React 19 + TypeScript + Vite 8. Requires Node.js 22.12 or later.

The UI is a disposable control client. It never owns a Copilot process or schedules
an evolution. Saved jobs are executed by the independent .NET Worker through MySQL.

- Development: `npm ci --ignore-scripts`, then `npm run dev` (127.0.0.1:5173).
- API proxy: `/api` → 127.0.0.1:5278. API and Worker must be started separately.
- Production build: `npm run build`; launcher copies `dist` into API static content.
- Validation: `npm run lint`, `npm run build`, `npm audit`.
- Optional build-time API override: `VITE_API_URL`.
- Durable E2E: [scripts/verify-durable.mjs](scripts/verify-durable.mjs); see repository
  [setup and test guide](../../README.md) for the isolated database prerequisites.

Console features: persisted dispatch pause/resume, heartbeat status, 1–50 cycle
budget, individual pause/resume/stop/retry/approval, paged saved evolution list,
restored analysis history, current-cycle agent roles and searchable/exportable logs.

Real-demo recording uses a dedicated MySQL database, separate Worker and one-cycle
budget. Set `DEMO_REPOSITORY` to a separate clone and provide the database through
the local environment. It consumes actual Copilot credits and is not part of the
simulated durable-service acceptance suite; full narration/CLI recording was not
rerun during this redesign.
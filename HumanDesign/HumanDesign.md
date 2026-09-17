# Code Evolver Human Design

Code Evolver is an autonomous AI worker that continuously develops and improves a cloned repository from a human-provided evolution direction.

## Human Workflow

### Repository Selection

1. The human selects a cloned repository folder or enters its absolute path.
2. The native folder picker must return the absolute local path.
3. The selected path is stored in browser local storage and restored on the next visit.

### Data Analyzer

Data analysis is an optional step before creating an evolution.

1. The human uploads a CSV or JSON file up to 5 MB.
2. The server profiles at most 1,000 rows and 100 columns.
3. Only bounded metadata and statistics are sent to AI. Raw row values are not sent.
4. A Data Analyzer Team runs four agents in order:
   - Purpose Agent determines the business or operational purpose of the data.
   - Insight Agent identifies data quality issues, distributions, anomalies, trends, and automation opportunities.
   - Evolution Direction Agent proposes one concrete software evolution based on the purpose and insights.
   - Summary Agent produces the final evolution direction and key points.
5. Analysis is asynchronous. Every agent reports queued, working, completed, or failed status through the existing event timeline model.
6. Detailed GitHub Copilot intent, tool, session, output, error, and heartbeat progress is visible while an agent works.
7. The human reviews the result and can apply it to the editable Evolution Direction field.

### Evolution Definition

One evolution contains:

1. Cloned Repository Path.
2. Evolution Direction text.
3. Evolution Scope, which may be a subfolder, component, or project.
4. Evolution Target Branch, used as the pull-request target branch.

Selecting an existing evolution refills these inputs so the human can reuse or edit it when creating an evolution.

### Start And Stop

The human selects an evolution and starts it. AI then handles the autonomous workflow.

The evolution stops only when the overall direction is finished or the human selects Stop. Stop prevents pending phases from starting. A GitHub Copilot CLI phase already in progress may finish before the evolution stops.

## User Experience

The main workspace has two horizontal areas:

```text
Analyzer  | Analyzer Team and Analysis Timeline
Evolution | Evolution Team and Event Timeline
```

### Analyzer Area

- Left: data upload, Analyze action, generated direction, key points, and Apply to Evolution action.
- Right: the four-agent analyzer roster and its live analysis timeline.

### Evolution Area

- Left: repository path, evolution direction, scope, target branch, create/edit controls, and saved evolution list.
- Right: selected evolution details, controls, metrics, agent team, daily plan, and live event timeline.

The layout must remain readable without horizontal overflow on desktop and mobile. On narrow screens, each row stacks its left side above its right side while Analyzer remains above Evolution.

## AI Agent Workflow

1. Scan Agent scans the current repository.
2. Planning Agent creates one Agent World Day of direction-related work containing no more than five work items.
3. Worker Agent implements each work item.
4. Reviewer Agent reviews each work item against the overall evolution direction.
5. Gate Agent verifies that the change builds and unit tests pass before merge or publication.
6. The final phase commits, pushes, and creates a pull request when publishing is enabled.

All agent activity must be observable in the UI. The timeline shows event status, timestamps, prompts, detailed logs, results, failures, and periodic quiet-time heartbeats. Unknown CLI events must be shown safely instead of silently discarded. A successful CLI run may obtain its final response from an assistant message, streamed response, or task-completion summary.

## Storage

Human input, evolution state, events, work items, logs, and results are persisted through the evolution store.

1. JSON file storage is available for local development.
2. Cassandra running in Docker is supported for persistent deployment storage.
3. Browser local storage is used only for the most recently selected repository path.
4. Data-analysis runs are pollable while active.

## Architecture

1. Storage Layer: JSON or Docker-hosted Cassandra.
2. Storage Service: CRUD and lifecycle persistence through a common evolution-store interface.
3. Event Generator: creates start, stop, scan, plan, work-item, implementation, review, gate, merge, pull-request, and data-analysis events.
4. Event Monitor: polls events and invokes the appropriate agent worker.
5. Agent Workers: handle one event type or analysis responsibility.
6. Data Service and API: expose repository selection, analysis, evolution lifecycle, status, and health operations.
7. Web UX: provides the Analyzer and Evolution areas and polls active work for live updates.

GitHub Copilot CLI is the default AI provider. It runs non-interactively in the selected repository and streams JSONL progress. Windows command wrappers must resolve to a launch form that preserves structured output.

## Safety

1. Credentials and configured secret environment variables must be redacted from logs.
2. Read-only mode detects and rejects repository changes.
3. No-publish mode prevents commit, push, and pull-request operations.
4. The `HumanDesign` directory is protected during autonomous evolution runs.
5. Existing user changes must not be reverted by autonomous work.

## Preferred Technologies

- .NET for the API and orchestration services.
- React and TypeScript for the web UX.
- Cassandra Query Language when Cassandra storage is enabled.
- Other technologies only when necessary.
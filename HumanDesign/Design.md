# Code Evolver

An autonomous AI worker that continuously develops and improves your repository with your direction.


# Code Evolver Logic flow

## Human Input of One Evolution Direction on Web Page UX
1. Cloned Repo Path
2. Evolution Direction Text
3. Evolution Scope: It can be sub folder, components or projects.
4. Evolution Target Branch: it will be used as target branch for auto generate pull request.

## Human Input Storage
The human input above can be saved to Cansandra DB on Docker.

## Start and Stop
Human can select one evolution direction and start to evolute.
Then all will be handled to AI Agent.

## AI Agent Flow
Scan and Plan Agent will start to scan current code and plan 1 day work for the direction.
Here 1 day is Agent World Day.

1 day work contains some （not greater than 5) direction related work items.
Worker Agent will focus on each workitem and create change for it.
Reviewer Agent will focus on the workitem and direction and review it.

Gate Agent will make sure the change can be built and unit test can pass and then change will be merged.

## When to Stop
Only When the overall direction is finished or Human click the stop button.



# Code Evolver Arch

## High Level Arch
1. Storage Layer: Docker Cansandra DB.
2. Storage Service: Support CRUD on Candandara DB.
3. Event Generator: Generate event on event DB.
    3.1 evolution start/stop event
    3.2 scan start and done event
    3.3 plan start and done event
    3.4 work items genereated and finished event
    3.6 code chang started and finished event
    3.7 review started and finished event
    3.8 gate check started and finished event.
    3.9 change merged event
4. Event Monitor: pull event and call event based Agent Worker.
5. Agent Worker: handle one type of event and work on it.
6. Data Service and API: provide operation interface
7. UX: use data service and API to finish the logic flow

## Detail Arch
For AI Agent, use Github Copilot CLI and wrap it for process work from logic.
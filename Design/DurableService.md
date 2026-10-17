# Decision: control plane and durable execution plane

The browser is a disposable control console. The ASP.NET Core API validates and
persists commands. A separate .NET Worker process owns Copilot CLI subprocesses.
MySQL 8.4/InnoDB is the source of truth. Neither browser polling nor API uptime is
required for execution. A service manager, not the web browser, starts the Worker.

## Persistence and concurrency

Store evolution aggregates as versioned JSON with indexed scheduling columns.
Use short InnoDB transactions and a scheduler mutex row to serialize state changes
and claims. Never hold a database transaction while running Copilot. This deliberate
single-scheduler design prioritizes correctness for a local coding service over
unbounded queue throughput. Progress mutations reload the latest aggregate inside
the transaction, preventing stop/pause commands from being overwritten.

Every claimed phase has an owner token, deadline, attempt count and heartbeat.
Mutations from an expired/replaced claim are rejected. Repository reservations last
across phases and pauses; stop/completion releases the reservation. All workers
must share the same host filesystem and canonical repository paths. This version
does not support distributed copies of a checkout or simultaneous writers in one
repository. A local file lock is an additional guard during CLI execution.

## Lifecycle

Draft → Running → Scan → Plan → Implement each item → Review → Gate → Approval →
Finalize → next cycle or Completed. Explicit budgets bound unattended operation.
Pause drains the current phase and retains queued work. Resume continues it.
Stop drains the current phase and cancels remaining work without deleting history.
Failed/ambiguous phases can be explicitly retried. No automatic reset of history.
Service dispatch pause stops new claims globally but lets current phases finish.

Read-only interrupted scan/plan phases may retry with bounded backoff. Interrupted
write, review, gate or publish phases require human reconciliation: arbitrary CLI
side effects cannot be made exactly-once by a database lease. No automatic push,
merge or approval. HumanDesign remains protected. This is trusted local automation,
not a sandbox for untrusted repositories or a public unauthenticated web service.
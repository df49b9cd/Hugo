# Design Workflow Visibility & Search

Capture `WorkflowExecutionContext` snapshots, persist them with a predictable schema, and expose queries that turn Hugo workflow metadata into actionable dashboards and alerts.

## Goal

Build a visibility pipeline that:

- Records workflow executions with canonical attributes.
- Persists snapshots in a store optimised for search and analytics.
- Supports operational queries (active workflows, replay counts, failure drill-downs).
- Aligns metrics, traces, and search results using the same metadata.

## Prerequisites

- Hugo workflow orchestration publishing `WorkflowExecutionContext` snapshots.
- Storage system (SQL, document DB, or data warehouse) for visibility records.
- Optional: OpenTelemetry collector or tracing backend if you want cross-channel dashboards.

## Step&nbsp;1 — Adopt the canonical schema

Persist the following fields exactly as emitted by `WorkflowVisibilityRecord`. Normalise names (lowercase, underscores) to avoid drift across services.

| Field | Source | Description | Example |
| --- | --- | --- | --- |
| `namespace` | `WorkflowVisibilityRecord.Namespace` | Logical owner or tenant for workflows. | `orders` |
| `workflow_id` | `WorkflowVisibilityRecord.WorkflowId` | Stable business identifier for the workflow. | `fulfillment-42` |
| `run_id` | `WorkflowVisibilityRecord.RunId` | Unique execution identifier (UUID). | `8f523de3-...` |
| `status` | `WorkflowVisibilityRecord.Status` | `Active`, `Completed`, `Failed`, `Canceled`, or `Terminated`. | `Active` |
| `task_queue` | `WorkflowVisibilityRecord.TaskQueue` | Queue responsible for commands. | `fulfillment-worker` |
| `schedule_id` | `WorkflowVisibilityRecord.ScheduleId` | Optional schedule that launched the run. | `cron/shipments/daily` |
| `schedule_group` | `WorkflowVisibilityRecord.ScheduleGroup` | Logical grouping for schedules. | `shipments` |
| `started_at` | `WorkflowVisibilityRecord.StartedAt` | UTC start timestamp. | `2025-10-15T08:27:34Z` |
| `completed_at` | `WorkflowVisibilityRecord.CompletedAt` | UTC completion timestamp, `null` while active. | `2025-10-15T08:28:14Z` |
| `logical_clock` | `WorkflowVisibilityRecord.LogicalClock` | Lamport clock for replay ordering. | `128` |
| `replay_count` | `WorkflowVisibilityRecord.ReplayCount` | Number of replays. Alert when this spikes. | `3` |
| `attributes` | `WorkflowVisibilityRecord.Attributes` | Custom key/value metadata. Prefix business keys with `workflow.`. | `{ "workflow.customer_id": "1309" }` |

> **Tip:** Use `WorkflowExecutionContext.SnapshotVisibility(attributes)` to merge your attributes into the canonical record while preserving system-generated dimensions.

## Step&nbsp;2 — Create storage indexes

Relational example:

```sql
create table workflow_visibility (
    namespace text not null,
    workflow_id text not null,
    run_id text not null,
    status text not null,
    task_queue text not null,
    schedule_id text null,
    schedule_group text null,
    started_at timestamptz not null,
    completed_at timestamptz null,
    logical_clock bigint not null,
    replay_count int not null,
    attributes jsonb not null default '{}'::jsonb,
    primary key (namespace, workflow_id, run_id)
);

create index workflow_visibility_active_idx
    on workflow_visibility (namespace, task_queue)
    where completed_at is null;

create index workflow_visibility_duration_idx
    on workflow_visibility ((completed_at - started_at))
    where completed_at is not null;
```

Guidelines:

- Partition document stores (Cosmos DB, DynamoDB) by `namespace` or `(namespace, task_queue)`.
- Materialise heavily queried metadata (for example `workflow.customer_id`) into dedicated columns; keep the rest in `attributes`.
- Configure TTLs for completed rows when you only need recent history.

## Step&nbsp;3 — Capture snapshots from the workflow runner

Call `SnapshotVisibility` whenever the workflow advances state, then upsert the record.

```csharp
var record = workflowContext.SnapshotVisibility(new Dictionary<string, string>
{
    ["workflow.customer_id"] = command.CustomerId,
    ["workflow.intent"] = command.Intent
});

await visibilityWriter.UpsertAsync(record, cancellationToken);
```

On completion, prefer `workflowContext.Complete(status, error, attributes)` so metrics and traces finish with the same metadata before you persist the final record.

## Step&nbsp;4 — Build operational queries

1. **Active workflows older than a threshold**

    ```sql
    select workflow_id, run_id, started_at,
           now() - started_at as age
    from workflow_visibility
    where namespace = :namespace
      and completed_at is null
      and now() - started_at > interval '10 minutes'
    order by age desc;
    ```

2. **Top workflows by replay count**

    ```sql
    select workflow_id, run_id, replay_count
    from workflow_visibility
    where namespace = :namespace
    order by replay_count desc
    limit 20;
    ```

3. **Failures grouped by task queue and customer**

    ```sql
    select task_queue,
           attributes->>'workflow.customer_id' as customer_id,
           count(*) as failures
    from workflow_visibility
    where namespace = :namespace
      and status = 'Failed'
      and started_at >= now() - interval '1 day'
    group by task_queue, customer_id
    order by failures desc;
    ```

4. **Latest run summary for a workflow**

    ```sql
    select status,
           started_at,
           completed_at,
           replay_count,
           attributes
    from workflow_visibility
    where namespace = :namespace
      and workflow_id = :workflowId
    order by started_at desc
    limit 1;
    ```

Mirror these filters in tracing dashboards by querying on the same tags (`workflow.namespace`, `workflow.task_queue`, `workflow.status`, `workflow.replay_count`, custom `workflow.*` attributes).

## Step&nbsp;5 — Align metrics, traces, and search

- Configure `GoDiagnostics` (or `AddHugoDiagnostics`) so metrics share the same dimensions as visibility records.
- Promote `workflow.*` attributes to indexed properties in your observability backend (Grafana, Application Insights, Lightstep, etc.).
- Use `workflow.logical_clock` and `workflow.replay_count` to correlate metric spikes with replay storms.
- Emit structured logs enriched with the visibility metadata (`logger.LogInformation("{@visibility}", record)`).

## Operational playbook

- **Heartbeat snapshots:** Schedule periodic snapshots for long-lived workflows so dashboards stay current even when no commands run.
- **Index hygiene:** Drop JSON indexes once you materialise a frequently queried metadata field into a column.
- **Alerting:** Combine visibility queries with `GoDiagnostics` metrics; for example, alert when `workflow.replay.count` p95 increases and visibility records show matching runs with high `replay_count`.
- **Access control:** Use the `namespace` column for row-level security so teams only see their own workflows while sharing infrastructure.

## Troubleshooting

- **Missing attributes:** Ensure you pass custom metadata to `SnapshotVisibility` every time; subsequent calls replace the document.
- **Stale dashboards:** Verify snapshot jobs run after each meaningful workflow event (command processed, timer fired, completion, cancellation).
- **Inconsistent IDs:** Never reuse `workflow_id` for unrelated business entities; derive the ID from domain data (order ID, invoice ID, etc.).
- **Collector discrepancies:** Confirm your OpenTelemetry exporter preserves `workflow.*` tags—some collectors drop custom attributes unless configured explicitly.

## Related guides

- [Publish metrics and traces to OpenTelemetry](observe-with-opentelemetry.md) for end-to-end telemetry wiring.
- [Apply timeout, retry, and cancellation playbooks](playbook-templates.md) to limit replay storms that appear in visibility dashboards.
- [`Deterministic coordination reference`](../reference/deterministic-coordination.md) for deeper insight into how version gates and effect stores interact with visibility metadata.

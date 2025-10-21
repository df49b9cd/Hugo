# Design Workflow Visibility & Search

Use this playbook to capture `WorkflowExecutionContext` snapshots, store them with a predictable attribute schema, and expose query patterns that turn Hugo's workflow metadata into actionable visibility dashboards.

## Canonical attribute schema

`WorkflowExecutionContext.SnapshotVisibility` and `WorkflowVisibilityRecord` emit the following fields. Persist them verbatim so metrics, traces, and search queries stay aligned.

| Field | Source | Description | Example |
| --- | --- | --- | --- |
| `namespace` | `WorkflowVisibilityRecord.Namespace` | Logical owner for workflows. Use it as the partition or tenant key. | `orders` |
| `workflow_id` | `WorkflowVisibilityRecord.WorkflowId` | Stable business identifier; never reuse between workflows. | `fulfillment-42` |
| `run_id` | `WorkflowVisibilityRecord.RunId` | Unique execution identifier. Combine with `workflow_id` for point lookups. | `8f523de3-...` |
| `status` | `WorkflowVisibilityRecord.Status` | Current lifecycle state (`Active`, `Completed`, `Failed`, `Canceled`, `Terminated`). | `Active` |
| `task_queue` | `WorkflowVisibilityRecord.TaskQueue` | Queue responsible for commands; group dashboards by queue to spot hotspots. | `fulfillment-worker` |
| `schedule_id` | `WorkflowVisibilityRecord.ScheduleId` | Optional schedule the run originates from. | `cron/shipments/daily` |
| `schedule_group` | `WorkflowVisibilityRecord.ScheduleGroup` | Optional logical grouping for schedules. | `shipments` |
| `started_at` | `WorkflowVisibilityRecord.StartedAt` | UTC start timestamp. | `2025-10-15T08:27:34Z` |
| `completed_at` | `WorkflowVisibilityRecord.CompletedAt` | UTC completion timestamp, `null` while active. | `2025-10-15T08:28:14Z` |
| `logical_clock` | `WorkflowVisibilityRecord.LogicalClock` | Latest Lamport clock emitted by the workflow. Highlights ordering skew. | `128` |
| `replay_count` | `WorkflowVisibilityRecord.ReplayCount` | Number of replays observed. Alert when this grows unexpectedly. | `3` |
| `attributes` | `WorkflowVisibilityRecord.Attributes` | Free-form key/value map for business context. Prefix keys with `workflow.` to avoid clashes (`workflow.customer_id`). | `{ "workflow.customer_id": "1309" }` |

When you call `WorkflowExecutionContext.SnapshotVisibility(attributes)` the supplied `attributes` are merged into the record. The same keys propagate to `Activity` tags as `workflow.metadata.*`, keeping traces and search in sync.

## Storing visibility data

### Recommended layout (relational example)

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

- **Partition** by `namespace` (or `namespace` + `task_queue`) in distributed stores such as Cosmos DB or DynamoDB to localize tenant traffic.
- **TTL** completed rows after your audit window by running periodic cleanup or using native TTL policies. Active runs stay until they finish.
- **Attributes**: materialize frequently queried keys into dedicated columns (for example `workflow.customer_id`) while leaving less common metadata in the JSON column.
- **Trace parity**: configure OpenTelemetry exporters to promote `workflow.*` tags into your tracing backend so you can correlate spans with the same filters described below.

## Ingest snapshot examples

Capture snapshots wherever you advance workflow state—such as after command processing or periodic heartbeats.

```csharp
var record = workflowContext.SnapshotVisibility(new Dictionary<string, string>
{
    ["workflow.customer_id"] = command.CustomerId,
    ["workflow.intent"] = command.Intent
});

await visibilityWriter.UpsertAsync(record, cancellationToken);
```

For completions, prefer `workflowContext.Complete(status, error, attributes)` which returns the final `WorkflowVisibilityRecord` and ensures metrics/traces are finalized before storage.

## Query patterns

1. **Active workflows older than a threshold**

   ```sql
   select workflow_id, run_id, started_at, now() - started_at as age
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

Mirror these queries in tracing backends (for example, Grafana LGTM or Application Insights) by filtering on the same attributes exported as span tags: `workflow.namespace`, `workflow.task_queue`, `workflow.status`, `workflow.replay_count`, and any custom `workflow.*` metadata.

## Operational guidance

- **Heartbeat pipelines**: schedule a lightweight job that snapshots active workflows periodically. This keeps dashboards fresh even when no commands arrive.
- **Index hygiene**: drop covering indexes on `attributes` once you promote a frequently used key to a first-class column; this avoids redundant storage.
- **Alerting**: combine visibility queries with `GoDiagnostics` metrics. For example, trigger alerts when `workflow.replay.count` p95 rises and the visibility table shows matching entries with high `replay_count`.
- **Access control**: use the `namespace` column for row-level security so teams only access their workflows while still sharing infrastructure.

By standardizing on this schema and query surface, you can layer OpenTelemetry dashboards, SQL analytics, and search tooling on top of Hugo's workflow metadata without per-application customization.

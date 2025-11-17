# Hugo Agent Handbook
- Public APIs must expose XML documentation comments, guard clauses, cancellation tokens and return `Result<T>` instead of throwing exceptions.
- Place braces on their own lines and prefer expression clarity over terseness.
- Use descriptive test names (e.g. `ErrGroupTests.WaitAsync_ShouldReturnSuccess`).

Commit messages follow the **Conventional Commits** specification (`feat:`, `fix:`, `docs:` etc.), remain focused and update the `CHANGELOG.md` under an `[Unreleased]` section. Before opening a pull request, ensure that builds pass on .NET 10, that all unit, integration and deterministic tests succeed, and that coverage has been collected. Fill out the PR template, link issues (`Fixes #123`), summarise behavioural changes and respond promptly. Squash merge is the default strategy.

## Core Concepts

### Concurrency Primitives

Hugo exposes a set of primitives under the `Hugo.Go` namespace:

**WaitGroup** – Tracks asynchronous operations and delays shutdown until tasks complete. Use `WaitGroup.Go` to schedule work, `WaitGroup.Add` to increment counters, `WaitGroup.Done` to signal completion and `WaitGroup.WaitAsync` to await completion【25089511936501†L18-L33】. Cancelling a wait group surfaces `Error.Canceled`, and metrics such as `waitgroup.additions` and `waitgroup.completions` are emitted when diagnostics are configured【25089511936501†L48-L53】.

**Mutex** and **RwMutex** – Provide mutual exclusion for exclusive or shared access. Asynchronous locks honour cancellation tokens and must be released via `await using`【25089511936501†L55-L74】.

**Channels** – Use `MakeChannel<T>` to create bounded or unbounded channels for message passing. Prioritised channels allow multiple priority levels, and DI‑friendly builders register channel readers and writers automatically【25089511936501†L100-L139】. Bounded channels respect full modes (`DropOldest`, `Wait`, etc.), and `TryComplete` propagates faults to readers【25089511936501†L120-L124】.

**TaskQueue<T>** – Builds cooperative leasing semantics on top of channels: producers enqueue work items, workers lease them for a configurable duration and can heartbeat, complete or fail each lease【25089511936501†L145-L188】. The queue automatically requeues expired leases and supports draining/restore operations for rolling upgrades【25089511936501†L194-L212】. Task queue health checks and backpressure monitors integrate with ASP.NET Core health probes and rate limiters【25089511936501†L213-L274】.

**Select and Fan‑In Helpers** – Await whichever channel case becomes ready first (`SelectAsync`), or merge multiple channels into one (`FanInAsync`). These helpers capture attempts, completions, latency, timeouts and cancellations in diagnostics【25089511936501†L346-L383】. See the [fan‑in how‑to guide](docs/how-to/fan-in-channels.md) for a worked example【592713217241589†L20-L29】.

### Result Pipelines

The `Result<T>` type models success or failure explicitly and supports functional composition. Use `Result.Ok`/`Go.Ok` to wrap a value and `Result.Fail`/`Go.Err` to create failures【560699870928889†L19-L27】. Inspect state with `IsSuccess`, `IsFailure`, `TryGetValue` and `TryGetError` and extract values with `Switch`/`Match` or `ValueOr`【560699870928889†L34-L41】.

Compose pipelines using combinators:

- **Synchronous**: `Then`, `Map`, `Tap`, `Ensure`, `Recover` and `Finally` orchestrate synchronous flows【560699870928889†L45-L56】.
- **Asynchronous**: `ThenAsync`, `MapAsync`, `TapAsync`, `RecoverAsync` and `EnsureAsync` accept `CancellationToken` and normalise cancellations to `Error.Canceled`【560699870928889†L58-L67】. `ValueTask` variants avoid extra allocations when delegates already return `ValueTask<Result<T>>`.
- **Collections and streaming**: Helpers such as `Result.Sequence`, `Result.Traverse`, `MapStreamAsync` and `FanInAsync` aggregate or transform streams of results【560699870928889†L69-L88】.
- **Parallel orchestration and retries**: Use `Result.WhenAll`, `Result.WhenAny` and `Result.RetryWithPolicyAsync` to execute operations concurrently, aggregate errors, or apply exponential backoff policies【560699870928889†L99-L108】. Tiered fallbacks let you define multiple strategies and switch when one fails【560699870928889†L112-L134】.

Errors carry metadata dictionaries and well‑known codes generated at compile time. You can attach custom metadata (for example `age`, `userId`) and extract it when logging【560699870928889†L173-L193】. Cancellation is represented by `Error.Canceled` and includes the originating token under the `cancellationToken` key【560699870928889†L195-L199】.

### Deterministic Coordination

Long‑running workflows often need to avoid repeating side effects when retries or replays occur. Hugo’s deterministic coordination primitives persist decisions externally:

1. **VersionGate** records an immutable version decision per change identifier using optimistic inserts. Concurrent writers that lose the compare‑and‑swap return an `error.version.conflict` so callers can retry or fallback【418667664692459†L15-L19】.
2. **DeterministicEffectStore** captures idempotent side effects keyed by change, version and step. Replays reuse the stored result instead of re‑executing the side effect【418667664692459†L15-L20】.
3. **DeterministicGate** combines the two to execute code paths safely across replays. The simplest overload allows you to define upgraded and legacy delegates and specify a version range; repeated executions reuse the persisted result【418667664692459†L21-L37】.

For richer coordination, the workflow builder lets you declare branches based on predicates, exact versions or ranges and supply a fallback【418667664692459†L39-L59】. Inside branches, use `DeterministicWorkflowContext` to capture side effects and access metadata (version, changeId, stepId). Missing branches or unsupported versions surface `error.version.conflict`, and cancellations or exceptions are converted to structured errors【418667664692459†L62-L87】. Instrumentation emits `workflow.*` metrics and activity tags so deterministic replays are observable【418667664692459†L90-L96】.

### Diagnostics and Observability

Configuring `GoDiagnostics` registers **System.Diagnostics.Metrics** instruments for wait groups, result pipelines, channel selectors, task queues and workflow execution. Emitted metrics include counters, up/down counters and histograms for operations such as wait‑group additions/completions, channel select attempts/timeouts, task queue leases/enqueues and workflow durations【849096974485540†L19-L63】【849096974485540†L66-L78】. When using the `Hugo.Diagnostics.OpenTelemetry` package, you can register meters, activity sources and exporters in one call (see *Observability in One Call* above) and configure service names, OTLP endpoints or Prometheus exporters【849096974485540†L83-L87】.

## Contributing and Support

Hugo welcomes contributions. To contribute:

1. Fork the repository and create a feature branch.
2. Review `CONTRIBUTING.md` for environment setup, coding standards and workflow expectations【112808351243944†L45-L46】.
3. Run `dotnet build` and `dotnet test` across all test projects (unit, integration, feature and deterministic suites) and ensure they pass.
4. Collect code coverage with `dotnet test --collect:"XPlat Code Coverage"` to match CI coverage gates.
5. Follow Conventional Commits and update the changelog. Link issues in your PR description and include context for behaviour changes.

For questions or bug reports, open an issue on GitHub. For security disclosures, contact the maintainer privately before filing a public issue【646460679641518†L247-L255】. Hugo is licensed under the MIT License【646460679641518†L258-L260】.

## Useful Tips and Best Practices

- **Configure diagnostics early.** Register meters and activity sources before creating channels, wait groups or pipelines so all metrics are emitted【25089511936501†L16-L17】【849096974485540†L15-L16】.
- **Prefer deterministic stores in production.** Use durable implementations of `IDeterministicStateStore` (e.g. SQL Server, Cosmos DB, Redis) to persist workflow versions and effects. The in‑memory store is suitable only for testing【418667664692459†L13-L20】.
- **Enforce backpressure.** Always specify capacities for channels and task queues and tune full modes/lease durations to match your workload. Use backpressure monitors and health checks to ensure service stability【25089511936501†L145-L212】.
- **Keep error metadata simple.** Stick to primitive types, records or known DTOs when populating `Error.Metadata` so the linker/AOT compiler can preserve them during trimming【646460679641518†L51-L61】.
- **Write deterministic tests.** Use fake time providers (`Microsoft.Extensions.TimeProvider.Testing`) and structure tests to exercise success, failure and cancellation paths. Do not rely on wall‑clock timers; Hugo’s primitives integrate with `TimeProvider` for deterministic scheduling.
- **Use value‑task overloads for performance.** When your delegates already return `ValueTask<Result<T>>`, call `ThenValueTaskAsync`, `MapValueTaskAsync`, etc., to avoid allocations【646460679641518†L28-L30】.
- **Emit structured logs.** Attach `Result<T>.Error.Metadata` to log scopes so downstream observability pipelines can slice failures by change/version, user, region and other dimensions【560699870928889†L173-L193】.

By following the guidelines in this handbook and exploring the examples and references in the documentation, you can leverage Hugo’s concurrency primitives, result pipelines and deterministic coordination to build robust, observable and testable workflows in .NET.

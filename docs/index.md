# Hugo Documentation

Hugo brings Go-inspired concurrency primitives, deterministic workflows, and functional result pipelines to .NET. The documentation is organised using the Divio system so that every task—learning, doing, auditing, or understanding—has a dedicated home.

## How these docs are organised

| Doc type | Purpose | Start here |
| --- | --- | --- |
| **Tutorials** | Follow-along introductions that assume no prior knowledge. Finish with a working sample. | [Getting started with channels and results](tutorials/getting-started.md) |
| **How-to guides** | Copy-paste recipes for well-defined tasks. Each guide lists prerequisites, steps, validation checks, and troubleshooting tips. | [Coordinate fan-in workflows](how-to/fan-in-channels.md) |
| **Reference** | Authoritative API catalogues and behavioural notes. Use when you already know what you are looking for. | [Concurrency primitives](reference/concurrency-primitives.md) |
| **Explanation** | Design rationale and architectural decisions. Helps you understand trade-offs and evolution. | [Design principles](explanation/design-principles.md) |
| **Meta** | Roadmap, project status, and audit trackers. | [Roadmap](meta/roadmap.md) |

> **Tip:** Need the big picture first? Read the [README](../README.md) for a product overview, then return here when you are ready to dive deeper.

## Choose your path

- **Just installed Hugo?** Start with the [tutorial](tutorials/getting-started.md), then wire observability via [Publish metrics to OpenTelemetry](how-to/observe-with-opentelemetry.md).
- **Shipping a feature?** Follow the relevant how-to: timeout/retry playbooks, fan-in coordination, workflow visibility, [TaskQueue diagnostics streaming](how-to/taskqueue-diagnostics.md), or profiling.
- **Auditing behaviour?** Jump to the reference for `WaitGroup`, `SelectAsync`, result pipelines, deterministic gate helpers, or diagnostics instruments.
- **Explaining trade-offs to your team?** Point them to [Design principles](explanation/design-principles.md) and the linked blog-inspired commentary.

## Navigation & search tips

- DocFX search (⌘/Ctrl + K in the published site) indexes headings, code samples, and metadata. Prefix with `how-to:` or `ref:` to narrow queries.
- Every page ends with “Related topics” or “Next steps” to keep you moving. Use those cross-links to jump between Divio categories.
- API surfaces reference namespaces and class names verbatim so IDE quick actions (Go to Definition) line up with documentation anchors.

## Reference map

- **Concurrency**: [Wait groups, mutexes, timers, channels, select helpers](reference/concurrency-primitives.md)
- **Result pipelines**: [Factories, combinators, streaming, retries, fallbacks](reference/result-pipelines.md)
- **Deterministic workflows**: [Version gates, effect stores, workflow builder](reference/deterministic-coordination.md)
- **Diagnostics**: [Meters, histograms, activity sources, configuration](reference/diagnostics.md)
- **Complete catalogue**: [API reference](reference/api-reference.md)

## Learn by doing

- **Samples**: Explore `samples/Hugo.WorkerSample` for a hosted worker that combines task queues, telemetry, and deterministic workflows. Deterministic worker variants (in-memory, SQL Server, Azure queue) live under `samples/Hugo.DeterministicWorkerSample*`; see [Deterministic persistence providers](deterministic-persistence-providers.md) for configuration guidance.
- **Benchmarks**: Run `benchmarks/Hugo.Benchmarks` to compare Hugo primitives with baseline .NET constructs.
- **Tooling**: The [profiling toolkit guide](how-to/profiling-toolkit.md) walks through capturing repeatable performance baselines.

## Stay in sync

- Track progress and upcoming work in the [roadmap](meta/roadmap.md) and [async API audit](async-api-audit.md).
- For contribution guidelines, coding standards, and CI expectations, see [CONTRIBUTING.md](../CONTRIBUTING.md).

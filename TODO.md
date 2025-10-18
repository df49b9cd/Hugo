# Project TODO

- [x] Solidify docs: create a top-level README with overview, install instructions, quickstart, and links into deeper API docs.
- [x] Expand testing: add coverage for edge cases in concurrency primitives (WaitGroup cancellation, Mutex contention, channel closure) and Result/Functional pipelines (Ensure error metadata, Recover branches); consider property tests for determinism.
- [x] Ship-ready automation: add CI (GitHub Actions) running dotnet build/dotnet test, enable code coverage reporting, wire package versioning, and prepare dotnet pack flows.
- [x] Developer experience: bundle sample projects showing Go-style patterns in ASP.NET worker/background services, plus benchmarks comparing primitives vs. native Task constructs.
- [x] Roadmap grooming: capture missing primitives (e.g., select over channels, timers), cancellation semantics audit, and community contribution guidelines in TODO/Issues.

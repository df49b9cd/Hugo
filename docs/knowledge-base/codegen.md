# Code Generation

Purpose: outline the limited codegen/serialization helpers and where they live.

## Source map
- `src/Hugo/Deterministic/DeterministicJsonContext.cs` - System.Text.Json source-gen context for deterministic payloads in the core package.
- `src/Hugo.Deterministic.{Cosmos,Redis,SqlServer}/**JsonContext.cs` - provider-specific STJ contexts to keep deterministic stores trimming/AOT friendly.
- `src/Hugo.TaskQueues.Replication/TaskQueueReplicationMetadataContext.cs` - source-gen context for replication metadata DTOs.
- `src/Hugo/ErrorJsonConverter.cs` - custom converter that preserves `Error` metadata round-trips.
- `docs/deterministic-persistence-providers.md` - guidance on serializing effect payloads and registering contexts.
- `docfx.json` - DocFX configuration for generating the published docs site.

## Responsibilities
- Keep runtime serialization allocation-light and linker-safe via source-generated metadata contexts.
- Ensure `Error` and deterministic payloads survive trimming/AOT by registering known types explicitly.
- Keep documentation generation (DocFX) reproducible and incremental-friendly.

## Backlog references
- When adding new payload types to deterministic workflows or replication, extend the relevant `JsonContext` and update the provider tests.
- If new public error codes are introduced, ensure `ErrorJsonConverter` continues to serialize metadata correctly and document codes in reference docs.
- DocFX pipeline changes should update `docfx.json` and `docs/index.md` links; verify site build with `docfx build` when feasible.

## Cross-cutting relationships
- Deterministic stores and task queue replication rely on the same STJ contexts; keep them aligned to avoid runtime trim warnings.
- Samples and tests that serialize payloads should import the generated contexts to avoid reflection-based serialization.
- Perf guidelines apply: source-gen is chosen to cut reflection costs; avoid reintroducing reflection-based serializers without measurement.

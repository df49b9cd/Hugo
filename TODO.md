# Project TODO

## Audit of Async APIs

- [ ] Create a tracking markdown file which contains each API and its status (Pending, In Progress, Completed) for the audit. The file should be structured for easy updates and reviews. And include the following checklist for each API:
- [ ] Review all async APIs to ensure they follow best practices for error handling and cancellation.
  - [ ] Ensure proper use of async/await patterns.
  - [ ] Verify that all async methods are properly documented.
  - [ ] Check for potential memory leaks in async operations.
  - [ ] Ensure that all async operations respect the provided cancellation tokens.
  - [ ] Identify any APIs that may lead to deadlocks or performance bottlenecks.
  - [ ] Document findings and propose improvements.

## Determinism & observability support

- [ ] Plan visibility/search attribute strategy leveraging new diagnostics for advanced querying.

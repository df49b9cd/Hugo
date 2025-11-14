# Hugo.TaskQueueDiagnosticsHostSample

This ASP.NET Core sample exercises `TaskQueueDiagnosticsHost` by:

- Creating a `TaskQueue<int>` with a paired backpressure monitor and replication source.
- Registering the diagnostics host to stream metrics, backpressure events, and replication metadata.
- Pushing the unified event stream through a `/diagnostics/taskqueue` SSE endpoint that CLI tools can `curl`/`watch`.

Run the sample and browse to `http://localhost:5000/diagnostics/taskqueue` to observe the JSON events emitted when the simulated workload trips backpressure, completes leases, and requeues failures.

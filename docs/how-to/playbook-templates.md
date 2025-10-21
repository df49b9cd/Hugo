# Timeout, Retry, and Cancellation Playbooks

Use these playbooks when you need consistent timeout, retry, and cancellation handling across distributed workloads. The guidance aligns with .NET 10 cancellation semantics and the Hugo primitives so you can wire in diagnostics, compensation, and deterministic tests.

## Quick reference

| Template | Recommended deadline | Retry profile | Cancellation guidance |
| --- | --- | --- | --- |
| Idempotent HTTP call | 10 s overall / 3 s per attempt | 3 attempts exponential (200 ms start, ×2, capped at 2 s) | Link a per-request CTS to the caller token; cancel attempt CTS after per-attempt timeout |
| Queue message handler | 45 s lease / 20 s per attempt | 5 attempts fixed delay (2 s) with lease extension | Propagate caller token; cancel processing when lease budget elapsed and release message |
| Saga step orchestration | 2 min workflow | 4 attempts exponential (1 s start, ×2, max 8 s) + compensation | Link workflow token; register compensations before leaving each step |

## Idempotent HTTP call (client → service)

Use this when invoking an idempotent REST or gRPC endpoint that can be retried safely.

### Recommended defaults (HTTP)

- Establish an overall request deadline that is comfortably lower than your upstream SLO (10 seconds is a good starting point).
- Keep per-attempt timeouts short (2–3 seconds) to surface stalled connections quickly.
- Record retry metadata on the outbound request so the remote side can detect duplicates.
- Treat empty or malformed bodies as validation errors and surface `error.validation` for quick diagnosis.

### Implementation template (HTTP)

```csharp
using System.Globalization;
using System.Net.Http.Json;
using Hugo;
using Hugo.Policies;
using static Hugo.Go;

static async ValueTask<Result<OrderState>> FetchOrderAsync(
    HttpClient client,
    string orderId,
    CancellationToken cancellationToken)
{
    var overallDeadline = TimeSpan.FromSeconds(10);
    var perAttemptTimeout = TimeSpan.FromSeconds(3);

    var retryPolicy = ResultExecutionBuilders.ExponentialRetryPolicy(
        attempts: 3,
        initialDelay: TimeSpan.FromMilliseconds(200),
        multiplier: 2.0,
        maxDelay: TimeSpan.FromSeconds(2));

    using var overallCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    overallCts.CancelAfter(overallDeadline);

    var attempt = 0;

    return await Result.RetryWithPolicyAsync(async (_, ct) =>
    {
        var currentAttempt = ++attempt;

        using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        attemptCts.CancelAfter(perAttemptTimeout);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"orders/{orderId}");
        request.Headers.TryAddWithoutValidation("x-hugo-attempt", currentAttempt.ToString(CultureInfo.InvariantCulture));

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, attemptCts.Token).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var failure = Error
                .From("Remote service returned a non-success status.", "error.remote.http")
                .WithMetadata("orderId", orderId)
                .WithMetadata("statusCode", (int)response.StatusCode);

            return Result.Fail<OrderState>(failure);
        }

        var body = await response.Content.ReadFromJsonAsync<OrderState>(cancellationToken: attemptCts.Token).ConfigureAwait(false);
        if (body is null)
        {
            return Result.Fail<OrderState>(
                Error.From("HTTP response body was empty.", ErrorCodes.Validation)
                    .WithMetadata("orderId", orderId));
        }

        return Result.Ok(body);
    }, retryPolicy, overallCts.Token).ConfigureAwait(false);
}
```

> **Operational note:** If the upstream exposes a retry-after header, inspect it inside the delegate and adjust `perAttemptTimeout` or short-circuit with `Result.Fail` to avoid hammering a saturated dependency.

## Queue message handler (at-least-once)

Apply this template for queue or bus consumers where the transport supplies a visibility timeout/lease and you need bounded retries.

### Recommended defaults (Queue)

- Start with a lease slightly longer than your worst-case processing time (45 seconds) and cancel 5–10 seconds before expiry to release the message.
- Retry 5 times with a fixed 2 second delay to keep latency predictable; escalate to a dead-letter path after the final attempt.
- Re-hydrate payloads using `Result<T>` helpers so schema issues bubble up as `error.validation`.
- Extend or release the lease before returning failure to avoid duplicate consumers working on the same payload.

### Implementation template (Queue)

```csharp
using Hugo;
using Hugo.Policies;
using static Hugo.Go;

static async ValueTask<Result<Unit>> ProcessMessageAsync(
    QueueEnvelope envelope,
    IQueueClient queue,
    IMessageDispatcher dispatcher,
    CancellationToken cancellationToken)
{
    var leaseDuration = TimeSpan.FromSeconds(45);
    var perAttemptBudget = TimeSpan.FromSeconds(20);

    var retryPolicy = ResultExecutionBuilders.FixedRetryPolicy(
        attempts: 5,
        delay: TimeSpan.FromSeconds(2));

    using var leaseCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    leaseCts.CancelAfter(leaseDuration);

    return await Result.RetryWithPolicyAsync(async (_, ct) =>
    {
        using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        attemptCts.CancelAfter(perAttemptBudget);

        var decoded = Deserialize(envelope);
        if (decoded.IsFailure)
        {
            var failure = (decoded.Error ?? Error.Unspecified())
                .WithMetadata("messageId", envelope.Id);

            return Result.Fail<Unit>(failure);
        }

        var execution = await dispatcher.HandleAsync(decoded.Value, attemptCts.Token).ConfigureAwait(false);
        if (execution.IsFailure)
        {
            await queue.ReleaseAsync(envelope, attemptCts.Token).ConfigureAwait(false);

            var failure = (execution.Error ?? Error.Unspecified())
                .WithMetadata("messageId", envelope.Id);

            return Result.Fail<Unit>(failure);
        }

        await queue.CompleteAsync(envelope, attemptCts.Token).ConfigureAwait(false);
        return Result.Ok(Unit.Value);
    }, retryPolicy, leaseCts.Token).ConfigureAwait(false);
}
```

> **Operational note:** When the queue exposes lease extension APIs, invoke them after each successful processing milestone (for example after deserialization and before external RPCs) to keep the message leased while long-running work completes.

## Saga step orchestration (workflow)

Use this playbook to orchestrate multi-service work with compensations by combining `ResultExecutionBuilders.CreateSaga` and retry-aware steps.

### Recommended defaults (Saga)

- Bound the overall workflow to 2 minutes unless the business process explicitly tolerates longer coordination.
- Retry side-effecting steps (such as charging a card) 3–4 times, but apply idempotency keys so compensations remain safe.
- Register compensations immediately after a success to guarantee cleanup when later steps fail.
- Use saga state to share data between steps; gate on `ResultSagaState.TryGet` instead of assuming previous steps succeeded.

### Implementation template (Saga)

```csharp
using Hugo;
using Hugo.Policies;
using Hugo.Sagas;
using static Hugo.Go;

static async Task<Result<ResultSagaState>> RunCheckoutSagaAsync(
    CheckoutCommand command,
    IInventoryService inventory,
    IPaymentGateway payments,
    IEventBus eventBus,
    CancellationToken cancellationToken)
{
    var policy = ResultExecutionBuilders.ExponentialRetryPolicy(
        attempts: 4,
        initialDelay: TimeSpan.FromSeconds(1),
        multiplier: 2.0,
        maxDelay: TimeSpan.FromSeconds(8));

    var saga = ResultExecutionBuilders.CreateSaga(
        policy,
        builder => builder.AddStep(
            name: "reserve-inventory",
            operation: async (context, ct) =>
            {
                var result = await inventory.ReserveAsync(command.OrderId, ct).ConfigureAwait(false);
                if (result.IsSuccess)
                {
                    context.RegisterCompensation(result.Value, async (reservation, token) =>
                    {
                        await inventory.ReleaseAsync(reservation, token).ConfigureAwait(false);
                    });
                }

                return result;
            }),
        builder => builder.AddStep(
            name: "charge-payment",
            operation: async (context, ct) =>
            {
                if (!context.State.TryGet<InventoryReservation>("reserve-inventory", out var reservation))
                {
                    return Result.Fail<PaymentReceipt>(
                        Error.From("Reservation missing from saga state.", ErrorCodes.Validation));
                }

                var result = await payments.ChargeAsync(command.PaymentId, reservation, ct).ConfigureAwait(false);
                if (result.IsSuccess)
                {
                    context.RegisterCompensation(result.Value, async (receipt, token) =>
                    {
                        await payments.RefundAsync(receipt, token).ConfigureAwait(false);
                    });
                }

                return result;
            }),
        builder => builder.AddStep(
            name: "emit-events",
            operation: (context, ct) =>
            {
                return Result.TryAsync(async token =>
                {
                    await eventBus.PublishAsync(context.State.Data, token).ConfigureAwait(false);
                    return Unit.Value;
                }, ct);
            }));

    using var workflowCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    workflowCts.CancelAfter(TimeSpan.FromMinutes(2));

    return await saga.ExecuteAsync(policy, workflowCts.Token).ConfigureAwait(false);
}
```

> **Operational note:** When you need per-step observability, inspect `ResultSagaStepContext.StepName` inside each operation and attach it to logging scopes or spans so distributed traces reflect the retry history.

## Adapting the templates

- Align deadlines with the slowest downstream dependency and reserve 20–30% of the budget for compensation or cleanup.
- Surface cancellation to callers by returning `Error.Canceled` from your outermost pipeline; let Hugo propagate it up the call chain.
- Use `TimeProvider` in tests to simulate timeouts deterministically; pass a custom provider into the helper when unit testing.
- Capture telemetry by wiring `GoDiagnostics` and recording retry counts per operation name; this highlights hot spots that need tuning.

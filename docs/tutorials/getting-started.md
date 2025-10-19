# Getting Started with Channels and Results

This tutorial walks you through building a minimal console app that coordinates work with channels, wait groups, and `Result<T>`. Follow each step in order; when you finish you will have a worker that produces and consumes messages safely.

## Prerequisites

- .NET 9 or 10 SDK installed (`dotnet --version` should return `9.*` or `10.*`).
- An empty folder for your sample project.

## 1. Create the project

```bash
dotnet new console -n HugoQuickstart
cd HugoQuickstart
dotnet add package Hugo
```

The package restore should succeed before you continue.

## 2. Import Hugo helpers

Replace `Program.cs` with the following skeleton:

```csharp
using Hugo;
using static Hugo.Go;

var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
var channel = MakeChannel<string>(capacity: 4);
var workers = new WaitGroup();
```

This prepares a cancellation token, a bounded channel, and a wait group to track background tasks.

## 3. Produce work

Add a producer task that writes a few messages to the channel and then completes the writer:

```csharp
workers.Go(async () =>
{
    using var complete = Defer(() => channel.Writer.TryComplete());

    await channel.Writer.WriteAsync("hello", cts.Token);
    await channel.Writer.WriteAsync("world", cts.Token);
});
```

`Defer` guarantees that the channel completes even if an exception occurs.

## 4. Consume and process values

Append the following loop after the producer:

```csharp
var messages = new List<string>();

while (await channel.Reader.WaitToReadAsync(cts.Token))
{
    if (!channel.Reader.TryRead(out var value))
    {
        continue;
    }

    var result = Ok(value)
        .Ensure(static text => !string.IsNullOrWhiteSpace(text))
        .Map(static text => text.ToUpperInvariant());

    if (result.IsSuccess)
    {
        messages.Add(result.Value);
    }
    else
    {
        Console.WriteLine($"skipped: {result.Error!.Message}");
    }
}
```

Here the `Result<T>` pipeline validates that each message is non-empty and projects it to uppercase before storing it.

## 5. Wait for completion and print output

Finish the program by waiting for the workers and printing the merged messages:

```csharp
await workers.WaitAsync(cts.Token);
Console.WriteLine(string.Join(' ', messages));
```

Run the sample:

```bash
dotnet run
```

You should see `HELLO WORLD` printed within five seconds. If you cancel the token earlier (for example by shortening the timeout), the loop exits gracefully and `WaitAsync` surfaces `Error.Canceled` instead of throwing exceptions.

## Next steps

- Explore [Coordinate fan-in workflows](../how-to/fan-in-channels.md) to merge more than one channel.
- Measure latency with [Publish metrics to OpenTelemetry](../how-to/observe-with-opentelemetry.md).

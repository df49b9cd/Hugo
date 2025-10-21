# Hugo Documentation

Welcome to Hugo, a library bringing Go-style concurrency primitives and functional result pipelines to .NET 9/10.

## Quick Links

- [Getting Started Tutorial](docs/tutorials/getting-started.md)
- [API Reference](api/index.md)
- [GitHub Repository](https://github.com/df49b9cd/Hugo)
- [NuGet Package](https://www.nuget.org/packages/Hugo)

## What is Hugo?

Hugo provides:

- **Go-inspired primitives**: `WaitGroup`, `Mutex`, `RwMutex`, channels, and select operations
- **Railway-oriented programming**: `Result<T>` pipelines with functional combinators
- **Deterministic workflows**: Replay-safe orchestration with version gates and effect stores
- **Observability**: Built-in OpenTelemetry integration with schema-aware metrics
- **Task queues**: Cooperative leasing with heartbeats and automatic requeuing

## Documentation Structure

### [Tutorials](docs/index.md#tutorials)

Learning-oriented guides that teach Hugo concepts through hands-on examples.

### [How-to Guides](docs/index.md#how-to-guides)

Task-oriented recipes for specific scenarios like fan-in workflows, retry policies, and observability setup.

### [Reference](docs/index.md#reference)

Comprehensive API documentation including concurrency primitives, result pipelines, and diagnostics.

### [Explanation](docs/index.md#explanation)

Understanding-oriented discussions about design decisions and architectural principles.

## Installation

```bash
dotnet add package Hugo
dotnet add package Hugo.Diagnostics.OpenTelemetry
```

## Example

```csharp
using Hugo;
using static Hugo.Go;

var channel = MakeChannel<string>(capacity: 10);
var workers = new WaitGroup();

workers.Go(async () =>
{
    using var _ = Defer(() => channel.Writer.TryComplete());
    await channel.Writer.WriteAsync("hello", cancellationToken);
});

await foreach (var value in channel.Reader.ReadAllAsync(cancellationToken))
{
    var result = Ok(value)
        .Ensure(text => !string.IsNullOrWhiteSpace(text))
        .Map(text => text.ToUpperInvariant());
        
    if (result.IsSuccess)
        Console.WriteLine(result.Value);
}

await workers.WaitAsync(cancellationToken);
```

## Support

- [Open an Issue](https://github.com/df49b9cd/Hugo/issues)
- [Contributing Guide](https://github.com/df49b9cd/Hugo/blob/main/CONTRIBUTING.md)
- [Security Policy](https://github.com/df49b9cd/Hugo/blob/main/SECURITY.md)

# Contributing to Hugo

Thank you for your interest in contributing to Hugo! This guide will help you get started with development, testing, and submitting contributions.

## Table of Contents

- [Code of Conduct](#code-of-conduct)
- [Getting Started](#getting-started)
- [Development Workflow](#development-workflow)
- [Code Style](#code-style)
- [Testing](#testing)
- [Documentation](#documentation)
- [Submitting Changes](#submitting-changes)
- [Review Process](#review-process)

## Code of Conduct

By participating in this project, you agree to maintain a respectful and inclusive environment. Be professional, constructive, and collaborative in all interactions.

## Getting Started

### Prerequisites

- **.NET 9 SDK** and **.NET 10 preview SDK** installed side-by-side
  - Verify with `dotnet --list-sdks`
- **Git** for version control
- **IDE**: Rider, Visual Studio 2022, or VS Code with C# Dev Kit

### Clone and Build

```bash
git clone https://github.com/df49b9cd/Hugo.git
cd Hugo
dotnet build Hugo.slnx
dotnet test tests/Hugo.Tests/Hugo.Tests.csproj
```

If all tests pass, you're ready to contribute!

## Development Workflow

### 1. Discuss First

For non-trivial changes:

- Open an issue describing the problem and your proposed solution
- Include an API sketch if adding new public types or methods
- Wait for maintainer feedback before implementing

### 2. Create a Feature Branch

```bash
git checkout -b feature/your-feature-name
```

Use clear branch names:

- `feature/add-buffered-select` for new features
- `fix/mutex-deadlock` for bug fixes
- `docs/improve-result-reference` for documentation

### 3. Make Your Changes

- Keep commits focused and atomic
- Write clear commit messages following [Conventional Commits](https://www.conventionalcommits.org/)
- Ensure code compiles for both `net9.0` and `net10.0`

### 4. Test Your Changes

```bash
# Run all tests
dotnet test tests/Hugo.Tests/Hugo.Tests.csproj

# Collect coverage
dotnet test tests/Hugo.Tests/Hugo.Tests.csproj --collect:"XPlat Code Coverage"

# Run benchmarks (optional but recommended for performance-sensitive changes)
dotnet run --project benchmarks/Hugo.Benchmarks/Hugo.Benchmarks.csproj -c Release
```

## Code Style

### General Principles

- **Nullability**: All reference types use nullable annotations (`<Nullable>enable</Nullable>`)
- **Guard clauses**: Validate public API arguments early with `ArgumentNullException.ThrowIfNull`
- **ConfigureAwait**: Always use `.ConfigureAwait(false)` in library code
- **Cancellation tokens**: All async methods must accept `CancellationToken` (default to `default`)

### Naming Conventions

- **Public APIs**: Use clear, intention-revealing names (`WaitAsync`, `SelectBuilder`)
- **Private helpers**: Prefix with descriptive verbs (`CollectSources`, `RemoveCompletedReaders`)
- **Constants**: PascalCase for static readonly fields
- **Local functions**: PascalCase to distinguish from lambdas

### Example

```csharp
public static async Task<Result<T>> TryAsync<T>(
    Func<CancellationToken, Task<T>> operation,
    CancellationToken cancellationToken = default,
    Func<Exception, Error?>? errorFactory = null)
{
    ArgumentNullException.ThrowIfNull(operation);

    try
    {
        var value = await operation(cancellationToken).ConfigureAwait(false);
        return Result.Ok(value);
    }
    catch (OperationCanceledException oce)
    {
        return Result.Fail<T>(Error.Canceled(token: oce.CancellationToken));
    }
    catch (Exception ex)
    {
        var error = errorFactory?.Invoke(ex) ?? Error.FromException(ex);
        return Result.Fail<T>(error);
    }
}
```

### XML Documentation

All public APIs require XML docs:

```csharp
/// <summary>
/// Executes an asynchronous operation and captures exceptions as <see cref="Error"/> values.
/// </summary>
/// <param name="operation">The async operation to execute.</param>
/// <param name="cancellationToken">Token to observe for cancellation.</param>
/// <param name="errorFactory">Optional factory to customize error creation from exceptions.</param>
/// <returns>A result containing the value on success or error on failure.</returns>
```

## Testing

### Test Organization

- **Unit tests**: Fast, isolated tests in `tests/Hugo.Tests/`
- **Test collections**: Use xUnit collections for tests requiring serialization (e.g., timing-sensitive tests)
- **Fake time providers**: Use `Microsoft.Extensions.TimeProvider.Testing` for deterministic timing

### Writing Tests

```csharp
[Fact]
public async Task WaitGroup_WaitAsync_ShouldReturnFalseOnTimeout()
{
    var wg = new WaitGroup();
    wg.Add(1); // Never completes

    var completed = await wg.WaitAsync(
        timeout: TimeSpan.FromMilliseconds(100),
        cancellationToken: TestContext.Current.CancellationToken);

    Assert.False(completed);
}
```

### Test Requirements

- **Cancellation tokens**: Always use `TestContext.Current.CancellationToken`
- **Timeouts**: Keep test timeouts short (50-500ms) using fake time providers when possible
- **No flaky tests**: Tests must pass consistently; use serialized collections if needed
- **Coverage**: New features require tests covering success, failure, and edge cases

## Documentation

### Documentation Structure

Hugo follows the [Diátaxis framework](https://diataxis.fr/):

- **Tutorials** (`docs/tutorials/`): Learning-oriented, step-by-step guides
- **How-to guides** (`docs/how-to/`): Task-oriented recipes
- **Reference** (`docs/reference/`): Information-oriented API catalogs
- **Explanation** (`docs/explanation/`): Understanding-oriented discussions

### When to Update Docs

- **New public APIs**: Add to relevant reference documentation
- **Behavior changes**: Update how-to guides and tutorials
- **New features**: Consider adding a tutorial or how-to guide
- **Breaking changes**: Update CHANGELOG.md and migration guides

### Example Documentation Update

If you add a new retry policy:

1. Update `docs/reference/result-pipelines.md` with API signature
2. Add example to `docs/how-to/playbook-templates.md`
3. Update CHANGELOG.md under `[Unreleased]` → `### Added`

## Submitting Changes

### Before Submitting

- [ ] All tests pass locally
- [ ] Code compiles for both .NET 9 and .NET 10
- [ ] Coverage collected and reviewed
- [ ] Documentation updated (if applicable)
- [ ] CHANGELOG.md updated under `[Unreleased]`
- [ ] Commit messages follow Conventional Commits

### Pull Request Process

1. **Push your branch**:

   ```bash
   git push origin feature/your-feature-name
   ```

2. **Open a Pull Request** on GitHub

3. **Fill out the PR template**:
   - Link related issues (`Fixes #123`, `Closes #456`)
   - Describe what changed and why
   - Note breaking changes (if any)
   - Include test results summary

4. **Respond to feedback**:
   - Address reviewer comments promptly
   - Push additional commits to your branch
   - Request re-review when ready

### PR Title Format

Use Conventional Commits format:

- `feat: add tiered fallback support`
- `fix: prevent mutex deadlock on cancellation`
- `docs: improve result pipeline examples`
- `test: add coverage for select timeout`
- `refactor: extract SelectBuilder to separate file`

## Review Process

### What Reviewers Look For

1. **Correctness**: Does the code do what it claims?
2. **Performance**: Are there obvious performance issues?
3. **API design**: Is the API intuitive and consistent with existing patterns?
4. **Tests**: Are edge cases covered?
5. **Documentation**: Can users understand how to use the feature?

### Approval and Merge

- PRs require approval from a maintainer
- CI must pass (build + tests)
- No unresolved conversations
- Squash-merge is preferred for clean history

## Design Guidelines

### Cancellation-First

Every async API must:

- Accept a `CancellationToken` parameter
- Propagate cancellation consistently
- Surface cancellation as `Error.Canceled` in result pipelines

### Deterministic Timing

- Use `TimeProvider` for all time-based operations
- Never use `Task.Delay` or `DateTime.Now` directly
- Support fake time providers for testing

### Structured Errors

- Use `Error` type instead of throwing exceptions in happy-path logic
- Attach metadata with `WithMetadata` for observability
- Set appropriate error codes from `ErrorCodes`

### Observability

- Emit metrics via `GoDiagnostics` for new primitives
- Use activity sources for distributed tracing
- Document instruments in `docs/reference/diagnostics.md`

## Need Help?

- **Questions**: Open a GitHub issue with the `question` label
- **Bugs**: Open a GitHub issue with detailed reproduction steps
- **Feature requests**: Open a GitHub issue describing the use case

## License

By contributing, you agree that your contributions will be licensed under the MIT License.

# Security Policy

## Supported Versions

The following versions of Hugo are currently being supported with security updates:

| Version | Supported          |
| ------- | ------------------ |
| 1.0.x   | :white_check_mark: |
| < 1.0   | :x:                |

## Reporting a Vulnerability

The Hugo team takes security bugs seriously. We appreciate your efforts to responsibly disclose your findings.

### How to Report

**Do not open a public GitHub issue for security vulnerabilities.**

Instead, please report security vulnerabilities by emailing:

**security@[maintainer-domain]** (Replace with actual security contact)

Or use GitHub's private vulnerability reporting feature:

1. Navigate to the [Security tab](https://github.com/df49b9cd/Hugo/security)
2. Click "Report a vulnerability"
3. Fill out the vulnerability details

### What to Include

To help us triage and fix the issue quickly, please include:

- **Description**: A clear description of the vulnerability
- **Impact**: What an attacker could do with this vulnerability
- **Reproduction**: Step-by-step instructions to reproduce the issue
- **Version**: The version(s) of Hugo affected
- **Environment**: .NET version, OS, and other relevant details
- **Suggested Fix**: If you have ideas on how to fix it (optional)

### Response Timeline

- **Initial Response**: Within 48 hours
- **Status Update**: Within 5 business days
- **Resolution Target**: Within 30 days for high-severity issues

### Disclosure Policy

- We will confirm receipt of your report within 48 hours
- We will provide a more detailed response within 5 business days
- We will work with you to understand and validate the vulnerability
- We will develop and test a fix
- We will coordinate disclosure timing with you
- We will credit you in the security advisory (unless you prefer to remain anonymous)

### Security Advisory Process

Once a fix is ready:

1. We will create a security advisory on GitHub
2. We will release a patched version
3. We will publish the advisory with details and credits
4. We will update the CHANGELOG with security fix information

### Public Disclosure

We ask that you:

- Give us reasonable time to fix the vulnerability before public disclosure
- Avoid exploiting the vulnerability beyond what is necessary to demonstrate it
- Avoid accessing, modifying, or deleting data without explicit permission

In return, we commit to:

- Respond promptly to your report
- Keep you informed of our progress
- Credit you for the discovery (if desired)
- Work with you on disclosure timing

## Security Best Practices

When using Hugo in production:

### 1. Keep Dependencies Updated

```bash
dotnet list package --outdated
dotnet add package Hugo
```

### 2. Use Structured Error Handling

Avoid leaking sensitive information in error messages:

```csharp
// Good
return Result.Fail<Order>(Error.From("Order validation failed", ErrorCodes.Validation));

// Bad - leaks details
return Result.Fail<Order>(Error.From($"Invalid card number: {cardNumber}", ErrorCodes.Validation));
```

### 3. Sanitize Metadata

Remove sensitive data before attaching to errors:

```csharp
var error = Error.FromException(ex)
    .WithMetadata("userId", userId)
    .WithMetadata("timestamp", DateTimeOffset.UtcNow);
// Don't include passwords, tokens, or PII
```

### 4. Validate Cancellation Tokens

Always propagate caller-provided cancellation tokens:

```csharp
public async Task<Result<T>> ProcessAsync(CancellationToken cancellationToken)
{
    return await Result.TryAsync(async ct =>
    {
        await DoWorkAsync(ct);
        return result;
    }, cancellationToken);
}
```

### 5. Limit Observability Cardinality

Avoid unbounded metric dimensions:

```csharp
// Good - bounded dimension
GoDiagnostics.RecordWithTag("endpoint", "/api/orders");

// Bad - could create millions of time series
GoDiagnostics.RecordWithTag("userId", userId);
```

## Known Security Considerations

### Concurrency Primitives

- **WaitGroup**: Unbounded task additions can cause memory exhaustion; enforce limits in production
- **Mutex**: Holding locks across async boundaries can cause deadlocks; prefer short critical sections
- **Channels**: Unbounded channels can grow without limit; use bounded channels with appropriate capacity

### Result Pipelines

- **Exception Handling**: `Result.Try` catches all non-cancellation exceptions; ensure sensitive exceptions don't leak
- **Error Metadata**: Metadata dictionaries are observable; sanitize before attaching credentials or secrets

### Task Queues

- **Lease Expiration**: Expired leases are automatically requeued; ensure idempotent processing to avoid duplicate side effects
- **Dead Letter**: Failed items are dead-lettered after max attempts; implement secure dead-letter storage

## Security-Related Changes

Security-related changes are documented in [CHANGELOG.md](CHANGELOG.md) with a `[SECURITY]` prefix.

## Contact

For non-security related issues, please use [GitHub Issues](https://github.com/df49b9cd/Hugo/issues).

For security concerns, use the private reporting methods described above.

---

**Thank you for helping keep Hugo and its users safe!**

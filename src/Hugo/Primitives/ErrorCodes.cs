using System.Collections.Generic;

namespace Hugo;

/// <summary>
/// Provides well-known error codes emitted by the library together with descriptive metadata.
/// </summary>
public static partial class ErrorCodes
{
    private static readonly IReadOnlyDictionary<string, ErrorDescriptor> DescriptorMap = CreateDescriptors();

    /// <summary>
    /// Gets a read-only view of all known error descriptors keyed by error code.
    /// </summary>
    public static IReadOnlyDictionary<string, ErrorDescriptor> Descriptors => DescriptorMap;

    /// <summary>
    /// Attempts to resolve descriptive metadata for a well-known error code.
    /// </summary>
    /// <param name="code">The error code to look up.</param>
    /// <param name="descriptor">When this method returns <see langword="true"/>, contains the resolved descriptor.</param>
    /// <returns><see langword="true"/> when the code is known; otherwise <see langword="false"/>.</returns>
    public static bool TryGetDescriptor(string code, out ErrorDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(code);
        return DescriptorMap.TryGetValue(code, out descriptor);
    }

    /// <summary>
    /// Resolves descriptive metadata for a well-known error code.
    /// </summary>
    /// <param name="code">The error code to look up.</param>
    /// <returns>The resolved descriptor.</returns>
    /// <exception cref="KeyNotFoundException">Thrown when the code is not recognized.</exception>
    public static ErrorDescriptor GetDescriptor(string code)
    {
        ArgumentNullException.ThrowIfNull(code);

        if (!DescriptorMap.TryGetValue(code, out ErrorDescriptor descriptor))
        {
            throw new KeyNotFoundException($"Unknown Hugo error code '{code}'.");
        }

        return descriptor;
    }

    internal static partial IReadOnlyDictionary<string, ErrorDescriptor> CreateDescriptors();
}

/// <summary>
/// Describes a well-known error emitted by Hugo.
/// </summary>
/// <param name="Name">The symbolic name exposed as a constant on <see cref="ErrorCodes"/>.</param>
/// <param name="Code">The string value returned with <see cref="Error.Code"/>.</param>
/// <param name="Description">A human-readable description of the error.</param>
/// <param name="Category">A coarse-grained category used for grouping related errors.</param>
public readonly record struct ErrorDescriptor(string Name, string Code, string Description, string Category);

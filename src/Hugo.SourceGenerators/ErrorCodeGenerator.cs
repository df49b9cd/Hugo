using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace Hugo.SourceGenerators;

[Generator(LanguageNames.CSharp)]
public sealed class ErrorCodeGenerator : IIncrementalGenerator
{
    private const string CatalogSuffix = ".hugo.errorcodes.json";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var catalogs = context.AdditionalTextsProvider
            .Where(static file => file.Path.EndsWith(CatalogSuffix, StringComparison.OrdinalIgnoreCase))
            .Select(static (text, cancellationToken) => ParseCatalog(text, cancellationToken))
            .Collect();

        context.RegisterSourceOutput(catalogs, static (spc, results) =>
        {
            if (results.IsDefaultOrEmpty)
            {
                return;
            }

            var combinedDiagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
            var entries = ImmutableArray.CreateBuilder<ErrorCodeEntry>();

            foreach (CatalogParseResult result in results)
            {
                combinedDiagnostics.AddRange(result.Diagnostics);
                if (!result.Entries.IsDefaultOrEmpty)
                {
                    entries.AddRange(result.Entries);
                }
            }

            foreach (Diagnostic diagnostic in combinedDiagnostics)
            {
                spc.ReportDiagnostic(diagnostic);
            }

            if (entries.Count == 0)
            {
                return;
            }

            EmitSource(spc, entries.ToImmutable());
        });
    }

    private static CatalogParseResult ParseCatalog(AdditionalText text, CancellationToken cancellationToken)
    {
        SourceText? sourceText = text.GetText(cancellationToken);
        if (sourceText is null)
        {
            return CatalogParseResult.Empty;
        }

        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        var entries = ImmutableArray.CreateBuilder<ErrorCodeEntry>();

        try
        {
            using JsonDocument document = JsonDocument.Parse(sourceText.ToString());

            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                diagnostics.Add(Diagnostic.Create(DiagnosticDescriptors.InvalidJsonShape, Location.None, text.Path));
                return new CatalogParseResult(entries.ToImmutable(), diagnostics.ToImmutable());
            }

            foreach (JsonElement element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                {
                    diagnostics.Add(Diagnostic.Create(DiagnosticDescriptors.InvalidJsonShape, Location.None, text.Path));
                    continue;
                }

                string? name = element.TryGetProperty("name", out JsonElement nameElement) && nameElement.ValueKind == JsonValueKind.String
                    ? nameElement.GetString()
                    : null;

                string? code = element.TryGetProperty("code", out JsonElement codeElement) && codeElement.ValueKind == JsonValueKind.String
                    ? codeElement.GetString()
                    : null;

                string? description = element.TryGetProperty("description", out JsonElement descriptionElement) && descriptionElement.ValueKind == JsonValueKind.String
                    ? descriptionElement.GetString()
                    : null;

                string category = element.TryGetProperty("category", out JsonElement categoryElement) && categoryElement.ValueKind == JsonValueKind.String
                    ? categoryElement.GetString() ?? "General"
                    : "General";

                if (string.IsNullOrWhiteSpace(name))
                {
                    diagnostics.Add(Diagnostic.Create(DiagnosticDescriptors.MissingField, Location.None, text.Path, "name"));
                    continue;
                }

                if (!IsValidIdentifier(name!))
                {
                    diagnostics.Add(Diagnostic.Create(DiagnosticDescriptors.InvalidIdentifier, Location.None, text.Path, name));
                    continue;
                }

                if (string.IsNullOrWhiteSpace(code))
                {
                    diagnostics.Add(Diagnostic.Create(DiagnosticDescriptors.MissingField, Location.None, text.Path, "code"));
                    continue;
                }

                if (string.IsNullOrWhiteSpace(description))
                {
                    diagnostics.Add(Diagnostic.Create(DiagnosticDescriptors.MissingField, Location.None, text.Path, "description"));
                    continue;
                }

                string nameValue = name!;
                string codeValue = code!;
                string descriptionValue = description!;

                entries.Add(new ErrorCodeEntry(nameValue, codeValue, descriptionValue, category));
            }
        }
        catch (JsonException ex)
        {
            diagnostics.Add(Diagnostic.Create(DiagnosticDescriptors.InvalidJson, Location.None, text.Path, ex.Message));
        }

        return new CatalogParseResult(entries.ToImmutable(), diagnostics.ToImmutable());
    }

    private static bool IsValidIdentifier(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        if (!SyntaxFacts.IsIdentifierStartCharacter(value[0]))
        {
            return false;
        }

        for (int i = 1; i < value.Length; i++)
        {
            if (!SyntaxFacts.IsIdentifierPartCharacter(value[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static void EmitSource(SourceProductionContext context, ImmutableArray<ErrorCodeEntry> entries)
    {
        var byName = new Dictionary<string, ErrorCodeEntry>(StringComparer.Ordinal);
        var byCode = new Dictionary<string, ErrorCodeEntry>(StringComparer.Ordinal);

        foreach (ErrorCodeEntry entry in entries)
        {
            if (byName.TryGetValue(entry.Name, out ErrorCodeEntry existingByName))
            {
                context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.DuplicateName, Location.None, entry.Name, existingByName.Code, entry.Code));
                continue;
            }

            if (byCode.TryGetValue(entry.Code, out ErrorCodeEntry existingByCode))
            {
                context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.DuplicateCode, Location.None, entry.Code, existingByCode.Name, entry.Name));
                continue;
            }

            byName[entry.Name] = entry;
            byCode[entry.Code] = entry;
        }

        if (byName.Count == 0)
        {
            return;
        }

        var ordered = byName.Values.OrderBy(entry => entry.Name, StringComparer.Ordinal).ToArray();

        var builder = new StringBuilder();
        builder.AppendLine("// <auto-generated/>");
        builder.AppendLine("#nullable enable");
        builder.AppendLine("namespace Hugo;");
        builder.AppendLine();
        builder.AppendLine("public static partial class ErrorCodes");
        builder.AppendLine("{");

        foreach (ErrorCodeEntry entry in ordered)
        {
            builder.Append("    /// <summary>");
            builder.Append(EscapeXml(entry.Description));
            builder.AppendLine("</summary>");
            builder.Append("    public const string ");
            builder.Append(entry.Name);
            builder.Append(" = ");
            builder.Append(SymbolDisplay.FormatLiteral(entry.Code, quote: true));
            builder.AppendLine(";");
            builder.AppendLine();
        }

        builder.AppendLine("    internal static partial global::System.Collections.Generic.IReadOnlyDictionary<string, global::Hugo.ErrorDescriptor> CreateDescriptors()");
        builder.AppendLine("    {");
        builder.AppendLine("        var descriptors = new global::System.Collections.Generic.Dictionary<string, global::Hugo.ErrorDescriptor>(global::System.StringComparer.Ordinal)");
        builder.AppendLine("        {");

        foreach (ErrorCodeEntry entry in ordered)
        {
            builder.Append("            [");
            builder.Append(SymbolDisplay.FormatLiteral(entry.Code, quote: true));
            builder.Append("] = new global::Hugo.ErrorDescriptor(");
            builder.Append(SymbolDisplay.FormatLiteral(entry.Name, quote: true));
            builder.Append(", ");
            builder.Append(SymbolDisplay.FormatLiteral(entry.Code, quote: true));
            builder.Append(", ");
            builder.Append(SymbolDisplay.FormatLiteral(entry.Description, quote: true));
            builder.Append(", ");
            builder.Append(SymbolDisplay.FormatLiteral(entry.Category, quote: true));
            builder.AppendLine("),");
        }

        builder.AppendLine("        };");
        builder.AppendLine("        return descriptors;");
        builder.AppendLine("    }");
        builder.AppendLine("}");

        context.AddSource("ErrorCodes.g.cs", SourceText.From(builder.ToString(), Encoding.UTF8));
    }

    private static string EscapeXml(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);

        foreach (char ch in value)
        {
            switch (ch)
            {
                case '&':
                    builder.Append("&amp;");
                    break;
                case '<':
                    builder.Append("&lt;");
                    break;
                case '>':
                    builder.Append("&gt;");
                    break;
                case '"':
                    builder.Append("&quot;");
                    break;
                case '\'':
                    builder.Append("&apos;");
                    break;
                default:
                    builder.Append(ch);
                    break;
            }
        }

        return builder.ToString();
    }

    private readonly struct ErrorCodeEntry
    {
        public ErrorCodeEntry(string name, string code, string description, string category)
        {
            Name = name;
            Code = code;
            Description = description;
            Category = category;
        }

        public string Name { get; }
        public string Code { get; }
        public string Description { get; }
        public string Category { get; }
    }

    private readonly struct CatalogParseResult
    {
        public CatalogParseResult(ImmutableArray<ErrorCodeEntry> entries, ImmutableArray<Diagnostic> diagnostics)
        {
            Entries = entries;
            Diagnostics = diagnostics;
        }

        public ImmutableArray<ErrorCodeEntry> Entries { get; }
        public ImmutableArray<Diagnostic> Diagnostics { get; }

        public static CatalogParseResult Empty { get; } = new CatalogParseResult(
            ImmutableArray<ErrorCodeEntry>.Empty,
            ImmutableArray<Diagnostic>.Empty);
    }
}

file static class DiagnosticDescriptors
{
    public static DiagnosticDescriptor InvalidJson { get; } = new(
        id: "HUGO001",
        title: "Invalid error catalog JSON",
        messageFormat: "The error catalog '{0}' could not be parsed: {1}",
        category: "Hugo.SourceGeneration",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static DiagnosticDescriptor InvalidJsonShape { get; } = new(
        id: "HUGO002",
        title: "Invalid error catalog format",
        messageFormat: "The error catalog '{0}' must be a JSON array of objects.",
        category: "Hugo.SourceGeneration",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static DiagnosticDescriptor MissingField { get; } = new(
        id: "HUGO003",
        title: "Missing error catalog field",
        messageFormat: "The error catalog '{0}' is missing required field '{1}'.",
        category: "Hugo.SourceGeneration",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static DiagnosticDescriptor InvalidIdentifier { get; } = new(
        id: "HUGO004",
        title: "Invalid error name",
        messageFormat: "The error name '{0}' is not a valid C# identifier.",
        category: "Hugo.SourceGeneration",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static DiagnosticDescriptor DuplicateName { get; } = new(
        id: "HUGO005",
        title: "Duplicate error name",
        messageFormat: "The error name '{0}' is defined more than once (codes '{1}' and '{2}').",
        category: "Hugo.SourceGeneration",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static DiagnosticDescriptor DuplicateCode { get; } = new(
        id: "HUGO006",
        title: "Duplicate error code value",
        messageFormat: "The error code '{0}' is assigned to multiple names ('{1}' and '{2}').",
        category: "Hugo.SourceGeneration",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);
}

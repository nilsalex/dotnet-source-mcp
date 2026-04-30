namespace DotnetSourceResolver.Core.Models;

/// <summary>
/// Input to the resolution pipeline.  Only <see cref="Symbol"/> is required;
/// the remaining fields refine version-accuracy when provided.
/// </summary>
public record SymbolRequest(
    /// <summary>Fully or partially qualified symbol (type, method, property, …).</summary>
    string Symbol,
    /// <summary>NuGet package ID, e.g. "System.Text.Json".</summary>
    string? PackageId = null,
    /// <summary>NuGet package version, e.g. "8.0.5".</summary>
    string? PackageVersion = null,
    /// <summary>Assembly name when different from the package ID.</summary>
    string? AssemblyName = null,
    /// <summary>Target framework moniker, e.g. "net10.0".</summary>
    string? TargetFramework = null,
    /// <summary>Whether to include source code snippets in the result.</summary>
    bool IncludeSnippets = true,
    /// <summary>Maximum number of lines per snippet (default 80).</summary>
    int MaxSnippetLines = 80
);

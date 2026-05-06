namespace DotnetSourceResolver.Core.Models.NuGet;

/// <summary>
/// Parsed Source Link JSON embedded in a Portable PDB.
/// Maps local source path patterns to downloadable URL patterns.
/// </summary>
public sealed record SourceLinkDocument(
    /// <summary>
    /// Maps local path glob patterns (e.g. "C:\src\*") to URL patterns
    /// (e.g. "https://raw.githubusercontent.com/org/repo/SHA/*").
    /// </summary>
    IReadOnlyDictionary<string, string> Documents
);

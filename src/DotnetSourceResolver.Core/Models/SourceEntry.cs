namespace DotnetSourceResolver.Core.Models;

/// <summary>
/// A resolved source location for a symbol.
/// </summary>
public record SourceEntry(
    /// <summary>Human-readable kind, e.g. "github", "source.dot.net", "docs".</summary>
    string Kind,
    /// <summary>Repository root URL, e.g. "https://github.com/dotnet/runtime".</summary>
    string Repository,
    /// <summary>Git commit SHA, or empty string when not known.</summary>
    string Commit,
    /// <summary>File path relative to the repository root.</summary>
    string Path,
    /// <summary>Direct permalink URL to the source file (optionally with line anchor).</summary>
    string Url,
    /// <summary>First line of the relevant range (1-based).</summary>
    int StartLine,
    /// <summary>Last line of the relevant range (1-based, inclusive).</summary>
    int EndLine
);

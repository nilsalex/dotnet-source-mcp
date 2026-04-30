namespace DotnetSourceResolver.Core.Models;

/// <summary>
/// A slice of source code extracted for a resolved symbol.
/// </summary>
public record SnippetEntry(
    /// <summary>File path relative to the repository root.</summary>
    string Path,
    /// <summary>First line included in <see cref="Code"/> (1-based).</summary>
    int StartLine,
    /// <summary>Last line included in <see cref="Code"/> (1-based, inclusive).</summary>
    int EndLine,
    /// <summary>The raw source code text.</summary>
    string Code
);

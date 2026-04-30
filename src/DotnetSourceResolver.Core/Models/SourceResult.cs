using DotnetSourceResolver.Core;

namespace DotnetSourceResolver.Core.Models;

/// <summary>
/// The output of the resolution pipeline.
/// </summary>
public record SourceResult(
    bool Resolved,
    string CanonicalSymbol,
    ResolutionKind ResolutionKind,
    ResolutionConfidence Confidence,
    IReadOnlyList<SourceEntry> Sources,
    IReadOnlyList<SnippetEntry> Snippets,
    IReadOnlyList<string> Diagnostics,
    string ResolverVersion
)
{
    /// <summary>
    /// Factory for a failed resolution result.
    /// </summary>
    public static SourceResult Unresolved(string symbol, IEnumerable<string> diagnostics) =>
        new(
            Resolved: false,
            CanonicalSymbol: symbol,
            ResolutionKind: ResolutionKind.Unresolved,
            Confidence: ResolutionConfidence.Low,
            Sources: [],
            Snippets: [],
            Diagnostics: diagnostics.ToList().AsReadOnly(),
            ResolverVersion: ResolverVersionProvider.Version
        );
}

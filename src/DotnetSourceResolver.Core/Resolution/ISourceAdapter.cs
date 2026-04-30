using DotnetSourceResolver.Core.Models;

namespace DotnetSourceResolver.Core.Resolution;

/// <summary>
/// A single source backend in the resolution pipeline.
/// Returns <c>null</c> when the adapter cannot handle the request or finds no result.
/// </summary>
public interface ISourceAdapter
{
    Task<SourceResult?> TryResolveAsync(SymbolRequest request, CancellationToken ct);
}

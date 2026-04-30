using DotnetSourceResolver.Core.Models;
using Microsoft.Extensions.Logging;

namespace DotnetSourceResolver.Core.Resolution;

/// <summary>
/// Orchestrates the adapter chain.  Adapters are tried in the order they are
/// supplied; the first non-null result wins.  If every adapter returns null the
/// resolver returns an <see cref="SourceResult.Unresolved"/> result containing
/// all diagnostics collected along the way.
/// </summary>
public sealed class DotNetSourceResolver
{
    private readonly IReadOnlyList<ISourceAdapter> _adapters;
    private readonly ILogger<DotNetSourceResolver> _logger;

    public DotNetSourceResolver(
        IEnumerable<ISourceAdapter> adapters,
        ILogger<DotNetSourceResolver> logger
    )
    {
        _adapters = adapters.ToList().AsReadOnly();
        _logger = logger;
    }

    public async Task<SourceResult> ResolveAsync(
        SymbolRequest request,
        CancellationToken ct = default
    )
    {
        var diagnostics = new List<string>();

        foreach (var adapter in _adapters)
        {
            ct.ThrowIfCancellationRequested();

            var adapterName = adapter.GetType().Name;
            try
            {
                _logger.LogDebug(
                    "Trying adapter {Adapter} for symbol {Symbol}",
                    adapterName,
                    request.Symbol
                );
                var result = await adapter.TryResolveAsync(request, ct);
                if (result is not null)
                {
                    _logger.LogInformation(
                        "Symbol {Symbol} resolved by {Adapter}",
                        request.Symbol,
                        adapterName
                    );
                    return result;
                }

                var msg = $"{adapterName}: no result";
                _logger.LogDebug("{Message}", msg);
                diagnostics.Add(msg);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                var msg = $"{adapterName}: {ex.GetType().Name}: {ex.Message}";
                _logger.LogWarning(
                    ex,
                    "Adapter {Adapter} threw an exception for symbol {Symbol}",
                    adapterName,
                    request.Symbol
                );
                diagnostics.Add(msg);
            }
        }

        _logger.LogWarning("Symbol {Symbol} could not be resolved by any adapter", request.Symbol);
        return SourceResult.Unresolved(request.Symbol, diagnostics);
    }
}

using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using DotnetSourceResolver.Core.Models;
using DotnetSourceResolver.Core.Resolution;
using ModelContextProtocol.Server;

namespace DotnetSourceResolver.McpServer.Tools;

[McpServerToolType]
public static class DotnetTools
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    // -------------------------------------------------------------------------
    // resolve_dotnet_source
    // -------------------------------------------------------------------------

    [McpServerTool(Name = "resolve_dotnet_source")]
    [Description(
        "Resolve a .NET symbol (type, method, property, …) to its exact source location. "
            + "Returns source links, GitHub permalink, code snippet, and confidence level. "
            + "Covers BCL, ASP.NET Core, and Microsoft.Extensions.* libraries."
    )]
    public static async Task<string> ResolveDotnetSource(
        DotNetSourceResolver resolver,
        [Description(
            "Fully or partially qualified symbol, e.g. 'System.Text.Json.JsonSerializer.Serialize'"
        )]
            string symbol,
        [Description("NuGet package ID, e.g. 'System.Text.Json'. Optional but improves accuracy.")]
            string? packageId = null,
        [Description("NuGet package version, e.g. '8.0.5'. Optional.")]
            string? packageVersion = null,
        [Description("Assembly name when different from the package ID. Optional.")]
            string? assemblyName = null,
        [Description("Target framework moniker, e.g. 'net10.0'. Optional.")]
            string? targetFramework = null,
        [Description("Whether to include source code snippets. Default true.")]
            bool includeSnippets = true,
        [Description("Maximum lines per snippet. Default 80.")] int maxSnippetLines = 80,
        CancellationToken cancellationToken = default
    )
    {
        var request = new SymbolRequest(
            Symbol: symbol,
            PackageId: packageId,
            PackageVersion: packageVersion,
            AssemblyName: assemblyName,
            TargetFramework: targetFramework,
            IncludeSnippets: includeSnippets,
            MaxSnippetLines: maxSnippetLines
        );

        var result = await resolver.ResolveAsync(request, cancellationToken);
        return JsonSerializer.Serialize(result, JsonOptions);
    }

    // -------------------------------------------------------------------------
    // explain_dotnet_implementation
    // NOTE: [McpServerTool] intentionally omitted — not announced to clients
    // until the synthesis step is implemented. The method is kept for future use.
    // -------------------------------------------------------------------------

    [Description(
        "Answer a question about the implementation of a .NET symbol. "
            + "Resolves the symbol to its source, then returns the relevant code snippets "
            + "together with an explanation grounded in the retrieved source. "
            + "Covers BCL, ASP.NET Core, and Microsoft.Extensions.* libraries."
    )]
    public static async Task<string> ExplainDotnetImplementation(
        DotNetSourceResolver resolver,
        [Description(
            "Fully or partially qualified symbol to explain, e.g. 'System.Collections.Generic.Dictionary'"
        )]
            string symbol,
        [Description(
            "The question to answer about the implementation, e.g. 'How is the internal hash table resized?'"
        )]
            string question,
        [Description("NuGet package ID. Optional.")] string? packageId = null,
        [Description("NuGet package version. Optional.")] string? packageVersion = null,
        [Description("Target framework moniker. Optional.")] string? targetFramework = null,
        CancellationToken cancellationToken = default
    )
    {
        var request = new SymbolRequest(
            Symbol: symbol,
            PackageId: packageId,
            PackageVersion: packageVersion,
            TargetFramework: targetFramework,
            IncludeSnippets: true,
            MaxSnippetLines: 80
        );

        var resolveResult = await resolver.ResolveAsync(request, cancellationToken);

        var explanation = new ExplanationResult(
            Symbol: symbol,
            Question: question,
            Resolved: resolveResult.Resolved,
            Confidence: resolveResult.Confidence,
            Evidence: resolveResult
                .Sources.Zip(
                    resolveResult
                        .Snippets.Cast<object?>()
                        .Concat(Enumerable.Repeat<object?>(null, int.MaxValue))
                )
                .Select(
                    (pair, _) =>
                    {
                        var (src, snip) = pair;
                        return new EvidenceEntry(
                            Url: src.Url,
                            Path: src.Path,
                            StartLine: src.StartLine,
                            EndLine: src.EndLine,
                            Code: snip is SnippetEntry s ? s.Code : null
                        );
                    }
                )
                .ToList(),
            Answer: resolveResult.Resolved
                ? $"Retrieved {resolveResult.Sources.Count} source location(s) for '{symbol}'. "
                    + $"See the Evidence field for the source code relevant to: {question}"
                : $"Could not resolve '{symbol}': {string.Join("; ", resolveResult.Diagnostics)}",
            Caveats: resolveResult.Resolved
                ?
                [
                    $"Resolution via {resolveResult.ResolutionKind}; confidence {resolveResult.Confidence}.",
                ]
                : [],
            ResolverVersion: resolveResult.ResolverVersion
        );

        return JsonSerializer.Serialize(explanation, JsonOptions);
    }

    // -------------------------------------------------------------------------
    // Output shapes
    // -------------------------------------------------------------------------

    private sealed record ExplanationResult(
        string Symbol,
        string Question,
        bool Resolved,
        ResolutionConfidence Confidence,
        IReadOnlyList<EvidenceEntry> Evidence,
        string Answer,
        IReadOnlyList<string> Caveats,
        string ResolverVersion
    );

    private sealed record EvidenceEntry(
        string Url,
        string Path,
        int StartLine,
        int EndLine,
        string? Code
    );
}

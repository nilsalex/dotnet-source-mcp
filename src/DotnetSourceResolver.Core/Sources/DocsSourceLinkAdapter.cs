using System.Text.RegularExpressions;
using DotnetSourceResolver.Core.Models;
using DotnetSourceResolver.Core.Resolution;
using Microsoft.Extensions.Logging;

namespace DotnetSourceResolver.Core.Sources;

/// <summary>
/// Resolves symbols via the Microsoft docs pages on
/// <c>learn.microsoft.com/dotnet/api/{symbol}</c>.
///
/// Many API pages include a "Source:" link that points directly to a versioned
/// GitHub blob (with commit SHA and optional line anchor).  This adapter
/// extracts that link and delegates file fetching to <see cref="GitHubAdapter"/>.
/// </summary>
public sealed class DocsSourceLinkAdapter : ISourceAdapter
{
    private const string DocsBaseUrl = "https://learn.microsoft.com/en-us/dotnet/api/";

    // Matches: <dt>Source:</dt><dd><a href="https://github.com/.../blob/{sha}/{path}">
    // The line anchor (#L42-L80) is optional.
    private static readonly Regex SourceLinkRegex = new(
        @"<dt>Source:</dt><dd><a href=""(https://github\.com/([^/]+)/([^/]+)/blob/([0-9a-f]{40})/([^""#]+)(?:#L(\d+)(?:-L(\d+))?)?)""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    private readonly HttpClient _http;
    private readonly GitHubAdapter _github;
    private readonly ILogger<DocsSourceLinkAdapter> _logger;

    public DocsSourceLinkAdapter(
        HttpClient http,
        GitHubAdapter github,
        ILogger<DocsSourceLinkAdapter> logger
    )
    {
        _http = http;
        _github = github;
        _logger = logger;
    }

    public async Task<SourceResult?> TryResolveAsync(SymbolRequest request, CancellationToken ct)
    {
        var docsUrl = DocsBaseUrl + NormaliseSymbol(request.Symbol);
        _logger.LogDebug("Fetching docs page {Url}", docsUrl);

        string html;
        try
        {
            using var response = await _http.GetAsync(docsUrl, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug(
                    "HTTP {Status} fetching docs for {Symbol}",
                    (int)response.StatusCode,
                    request.Symbol
                );
                return null;
            }
            html = await response.Content.ReadAsStringAsync(ct);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "HTTP error fetching docs for {Symbol}", request.Symbol);
            return null;
        }

        var sourceLink = ExtractBestSourceLink(html, request.TargetFramework);
        if (sourceLink is null)
        {
            _logger.LogDebug("No source link found on docs page for {Symbol}", request.Symbol);
            return null;
        }

        _logger.LogDebug("Found source link: {Url}", sourceLink.Url);

        var rawUrl =
            $"https://raw.githubusercontent.com/{sourceLink.Owner}/{sourceLink.Repo}/{sourceLink.Commit}/{sourceLink.Path}";

        // Prefer the line hint from the link; fall back to showing the whole start of the file
        var startLine = sourceLink.StartLine > 0 ? sourceLink.StartLine : 1;
        var endLine =
            sourceLink.EndLine > 0
                ? sourceLink.EndLine
                : startLine + Math.Min(request.MaxSnippetLines, 80) - 1;

        // Clamp snippet to MaxSnippetLines
        if (endLine - startLine + 1 > request.MaxSnippetLines)
            endLine = startLine + request.MaxSnippetLines - 1;

        SnippetEntry? snippet = null;
        if (request.IncludeSnippets)
        {
            try
            {
                snippet = await _github.FetchSnippetAsync(rawUrl, startLine, endLine, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch snippet for {Url}", rawUrl);
            }
        }

        var repoUrl = $"https://github.com/{sourceLink.Owner}/{sourceLink.Repo}";
        var permalinkUrl =
            $"{repoUrl}/blob/{sourceLink.Commit}/{sourceLink.Path}#L{startLine}-L{endLine}";

        var sourceEntry = new SourceEntry(
            Kind: "docs",
            Repository: repoUrl,
            Commit: sourceLink.Commit,
            Path: sourceLink.Path,
            Url: permalinkUrl,
            StartLine: startLine,
            EndLine: endLine
        );

        return new SourceResult(
            Resolved: true,
            CanonicalSymbol: request.Symbol,
            ResolutionKind: ResolutionKind.Docs,
            Confidence: ResolutionConfidence.Medium,
            Sources: [sourceEntry],
            Snippets: snippet is not null ? [snippet] : [],
            Diagnostics: [],
            ResolverVersion: ResolverVersionProvider.Version
        );
    }

    // -------------------------------------------------------------------------
    // Internal helpers
    // -------------------------------------------------------------------------

    internal static string NormaliseSymbol(string symbol)
    {
        // Strip generic arity and convert to lowercase:
        //   System.Collections.Generic.Dictionary<TKey,TValue> → system.collections.generic.dictionary-2
        //   System.Collections.Generic.Dictionary`2            → system.collections.generic.dictionary-2
        var name = symbol
            .Replace('<', '-')
            .Replace('>', ' ')
            .Trim()
            .Replace('`', '-')
            .ToLowerInvariant();

        // Strip type parameters (everything after the arity marker or generic bracket)
        name = Regex.Replace(name, @"-\d+\s.*", m => m.Value.Split(' ')[0]);
        name = Regex.Replace(name, @"\s+.*", "");

        return name;
    }

    internal static ParsedSourceLink? ExtractBestSourceLink(string html, string? targetFramework)
    {
        var matches = SourceLinkRegex.Matches(html);
        if (matches.Count == 0)
            return null;

        // If a TFM is specified, prefer links whose path contains the TFM string
        if (!string.IsNullOrEmpty(targetFramework))
        {
            foreach (Match m in matches)
            {
                if (m.Groups[5].Value.Contains(targetFramework, StringComparison.OrdinalIgnoreCase))
                    return ParseMatch(m);
            }
        }

        // Prefer dotnet/runtime over dotnet/dotnet (aggregated repo)
        foreach (Match m in matches)
        {
            if (m.Groups[2].Value == "dotnet" && m.Groups[3].Value == "runtime")
                return ParseMatch(m);
        }

        // Fall back to last match (most recent framework version tends to be last)
        return ParseMatch(matches[^1]);
    }

    private static ParsedSourceLink ParseMatch(Match m)
    {
        _ = int.TryParse(m.Groups[6].Value, out var startLine);
        _ = int.TryParse(m.Groups[7].Value, out var endLine);
        return new ParsedSourceLink(
            Url: m.Groups[1].Value,
            Owner: m.Groups[2].Value,
            Repo: m.Groups[3].Value,
            Commit: m.Groups[4].Value,
            Path: m.Groups[5].Value,
            StartLine: startLine,
            EndLine: endLine
        );
    }

    internal sealed record ParsedSourceLink(
        string Url,
        string Owner,
        string Repo,
        string Commit,
        string Path,
        int StartLine,
        int EndLine
    );
}

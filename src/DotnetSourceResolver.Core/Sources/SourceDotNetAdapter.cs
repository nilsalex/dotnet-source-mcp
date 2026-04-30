using System.Text.RegularExpressions;
using DotnetSourceResolver.Core.Models;
using DotnetSourceResolver.Core.Resolution;
using Microsoft.Extensions.Logging;

namespace DotnetSourceResolver.Core.Sources;

/// <summary>
/// Resolves symbols using <c>source.dot.net</c> — the Roslyn-backed source
/// browser for dotnet/runtime, ASP.NET Core, and Microsoft.Extensions.*.
///
/// Resolution flow:
/// 1. Search  GET /api/symbols/?symbol={symbol}  → HTML with result links
/// 2. Pick best result → extract project name + symbol hash
/// 3. Fetch  /{project}/A{firstHashChar}.html   → map hash → file path
/// 4. Fetch  /{project}/{file}.cs.html           → extract GitHub URL (with SHA)
/// 5. Delegate raw file + snippet fetch to <see cref="GitHubAdapter"/>
/// </summary>
public sealed class SourceDotNetAdapter : ISourceAdapter
{
    private const string BaseUrl = "https://source.dot.net";

    // Matches one full result block: href + kind + description in order.
    // Each result in the HTML looks like:
    //   <a href="/Project/A.html#hash" ...>
    //     <div class="resultKind">class</div>
    //     ...
    //     <div class="resultDescription">Fully.Qualified.Name</div>
    //   </a>
    private static readonly Regex ResultBlockRegex = new(
        @"href=""/([\w.]+)/A\.html#([0-9a-f]{16})""[^>]*>.*?<div class=""resultKind"">(\w+)</div>.*?<div class=""resultDescription"">([^<]+)</div>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline
    );

    // Matches: m["7hexchars"]=f[N];
    private static readonly Regex MapEntryRegex = new(
        @"m\[""([0-9a-f]{7})""\]=f\[(\d+)\];",
        RegexOptions.Compiled
    );

    // Matches file list:  "some/path/File.cs",
    private static readonly Regex FileListRegex = new(@"""([^""]+\.cs)""", RegexOptions.Compiled);

    // Matches GitHub tree URL in file page header
    private static readonly Regex GitHubTreeUrlRegex = new(
        @"href=""(https://github\.com/[^/]+/[^/]+/tree/([0-9a-f]{40})/([^""]+))""",
        RegexOptions.Compiled
    );

    private readonly HttpClient _http;
    private readonly GitHubAdapter _github;
    private readonly ILogger<SourceDotNetAdapter> _logger;

    public SourceDotNetAdapter(
        HttpClient http,
        GitHubAdapter github,
        ILogger<SourceDotNetAdapter> logger
    )
    {
        _http = http;
        _github = github;
        _logger = logger;
    }

    public async Task<SourceResult?> TryResolveAsync(SymbolRequest request, CancellationToken ct)
    {
        // Step 1: Search
        var searchHtml = await FetchTextAsync(
            $"{BaseUrl}/api/symbols/?symbol={Uri.EscapeDataString(request.Symbol)}",
            ct
        );
        if (searchHtml is null)
            return null;

        // Step 2: Pick best result
        var (project, symbolHash) = PickBestResult(searchHtml, request.Symbol);
        if (project is null || symbolHash is null)
        {
            _logger.LogDebug("No results for symbol {Symbol}", request.Symbol);
            return null;
        }

        _logger.LogDebug("Best match: project={Project} hash={Hash}", project, symbolHash);

        // Step 3: Map hash → file path
        var filePath = await ResolveFilePathAsync(project, symbolHash, ct);
        if (filePath is null)
        {
            _logger.LogDebug("Could not map hash {Hash} to file path", symbolHash);
            return null;
        }

        _logger.LogDebug("Resolved file: {Path}", filePath);

        // Step 4: Fetch source page → extract GitHub URL + line number
        var filePageHtml = await FetchTextAsync($"{BaseUrl}/{project}/{filePath}.html", ct);
        if (filePageHtml is null)
            return null;

        var gitHubInfo = ExtractGitHubInfo(filePageHtml);
        if (gitHubInfo is null)
        {
            _logger.LogDebug("Could not extract GitHub URL from source page for {File}", filePath);
            return null;
        }

        var (repoUrl, commit, repoFilePath) = gitHubInfo.Value;
        var symbolLine = ExtractSymbolLine(filePageHtml, symbolHash);

        // Step 5: Fetch snippet via GitHubAdapter
        var startLine = Math.Max(1, symbolLine - 2);
        var endLine = startLine + Math.Min(request.MaxSnippetLines, 80) - 1;
        var rawUrl =
            $"https://raw.githubusercontent.com/{RepoOwnerAndName(repoUrl)}/{commit}/{repoFilePath}";

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

        var permalinkUrl =
            $"https://github.com/{RepoOwnerAndName(repoUrl)}/blob/{commit}/{repoFilePath}#L{startLine}-L{endLine}";
        var sourceEntry = new SourceEntry(
            Kind: "source.dot.net",
            Repository: repoUrl,
            Commit: commit,
            Path: repoFilePath,
            Url: permalinkUrl,
            StartLine: startLine,
            EndLine: endLine
        );

        var confidence = string.IsNullOrEmpty(commit)
            ? ResolutionConfidence.Medium
            : ResolutionConfidence.High;

        return new SourceResult(
            Resolved: true,
            CanonicalSymbol: request.Symbol,
            ResolutionKind: ResolutionKind.SourceDotNet,
            Confidence: confidence,
            Sources: [sourceEntry],
            Snippets: snippet is not null ? [snippet] : [],
            Diagnostics: [],
            ResolverVersion: ResolverVersionProvider.Version
        );
    }

    // -------------------------------------------------------------------------
    // Internal helpers (internal for testing)
    // -------------------------------------------------------------------------

    // Kind preference: type declarations rank above members.
    private static readonly IReadOnlyDictionary<string, int> KindScore = new Dictionary<
        string,
        int
    >(StringComparer.OrdinalIgnoreCase)
    {
        ["class"] = 3,
        ["struct"] = 3,
        ["interface"] = 3,
        ["enum"] = 3,
        ["delegate"] = 3,
        ["method"] = 2,
        ["property"] = 1,
        ["field"] = 1,
        ["event"] = 1,
    };

    internal static (string? project, string? hash) PickBestResult(string html, string symbol)
    {
        var blocks = ResultBlockRegex.Matches(html);
        if (blocks.Count == 0)
            return (null, null);

        // Normalise the requested symbol for comparison:
        //   "System.Collections.Generic.Dictionary" → normalised prefix to match against description
        var symbolNorm = symbol.Trim();

        string? bestProject = null;
        string? bestHash = null;
        int bestScore = -1;

        foreach (Match block in blocks)
        {
            var project = block.Groups[1].Value;
            var hash = block.Groups[2].Value;
            var kind = block.Groups[3].Value;
            var desc = block.Groups[4].Value; // fully-qualified, may contain &lt; entities

            // Decode the two HTML entities that appear in generic descriptions
            desc = desc.Replace("&lt;", "<").Replace("&gt;", ">").Replace("&amp;", "&");

            int score = ScoreResult(desc, kind, symbolNorm);
            if (score > bestScore)
            {
                bestScore = score;
                bestProject = project;
                bestHash = hash;
            }
        }

        return (bestProject, bestHash);
    }

    /// <summary>
    /// Scores a single search result against the requested symbol.
    /// Higher is better; 0 means "no useful signal" (will still win if nothing better exists).
    /// </summary>
    internal static int ScoreResult(string description, string kind, string symbol)
    {
        // Strip generic parameters for comparison: "Dictionary<TKey, TValue>" → "Dictionary"
        var descBase = description.Contains('<')
            ? description[..description.IndexOf('<')]
            : description;

        var symbolBase = symbol.Contains('<') ? symbol[..symbol.IndexOf('<')] : symbol;

        int kindPriority = KindScore.TryGetValue(kind, out var k) ? k : 0;

        // Score 10: exact full description match (e.g. "System.Collections.Generic.Dictionary")
        if (descBase.Equals(symbolBase, StringComparison.OrdinalIgnoreCase))
            return 10 + kindPriority;

        // Score 6: description starts with the full symbol (catches "Symbol.NestedType")
        // but only when the char after is a dot — avoids "DictionaryExtensions" matching "Dictionary"
        if (
            descBase.StartsWith(symbolBase, StringComparison.OrdinalIgnoreCase)
            && descBase.Length > symbolBase.Length
            && descBase[symbolBase.Length] == '.'
        )
            return 6 + kindPriority;

        // Score 3+kind: description ends with the short name AND is a type declaration
        var symbolShort = symbolBase.Contains('.')
            ? symbolBase[(symbolBase.LastIndexOf('.') + 1)..]
            : symbolBase;

        if (descBase.EndsWith(symbolShort, StringComparison.OrdinalIgnoreCase))
            return kindPriority; // 0-3 depending on kind

        return 0;
    }

    internal static string? ResolveFilePathFromBucket(string bucketHtml, string symbolHash)
    {
        // Build file list
        var fileMatches = FileListRegex.Matches(bucketHtml);
        var files = fileMatches.Select(m => m.Groups[1].Value).ToList();
        if (files.Count == 0)
            return null;

        // Build the hash→file map
        // Keys are 7 hex chars (hash chars 1–7, i.e. skip first char which is the bucket char)
        var key = symbolHash[1..8]; // 7 chars starting at index 1
        var mapMatches = MapEntryRegex.Matches(bucketHtml);
        foreach (Match m in mapMatches)
        {
            if (m.Groups[1].Value == key)
            {
                var fileIndex = int.Parse(m.Groups[2].Value);
                if (fileIndex < files.Count)
                    return files[fileIndex];
            }
        }

        return null;
    }

    internal static (string repo, string commit, string path)? ExtractGitHubInfo(
        string filePageHtml
    )
    {
        var m = GitHubTreeUrlRegex.Match(filePageHtml);
        if (!m.Success)
            return null;

        var commit = m.Groups[2].Value;
        var path = m.Groups[3].Value;

        // Derive repo URL by stripping /tree/{sha}/{path}
        var fullUrl = m.Groups[1].Value;
        var repoUrl = fullUrl[..fullUrl.IndexOf("/tree/", StringComparison.OrdinalIgnoreCase)];

        return (repoUrl, commit, path);
    }

    internal static int ExtractSymbolLine(string filePageHtml, string symbolHash)
    {
        // The source is in <pre id="code">…</pre>; each \n-separated line corresponds to a source line.
        const string codeStart = "<pre id=\"code\">";
        var codeIdx = filePageHtml.IndexOf(codeStart, StringComparison.Ordinal);
        if (codeIdx < 0)
            return 1;

        var codeSection = filePageHtml[(codeIdx + codeStart.Length)..];
        var symIdx = codeSection.IndexOf($"id=\"{symbolHash}\"", StringComparison.Ordinal);
        if (symIdx < 0)
            return 1;

        // Count newlines before the symbol
        return codeSection[..symIdx].Count(c => c == '\n') + 1;
    }

    private static string RepoOwnerAndName(string repoUrl)
    {
        // https://github.com/dotnet/runtime → dotnet/runtime
        var uri = new Uri(repoUrl);
        return uri.AbsolutePath.TrimStart('/');
    }

    private async Task<string?> FetchTextAsync(string url, CancellationToken ct)
    {
        _logger.LogDebug("GET {Url}", url);
        try
        {
            using var response = await _http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("HTTP {Status} for {Url}", (int)response.StatusCode, url);
                return null;
            }
            return await response.Content.ReadAsStringAsync(ct);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "HTTP error fetching {Url}", url);
            return null;
        }
    }

    private async Task<string?> ResolveFilePathAsync(
        string project,
        string symbolHash,
        CancellationToken ct
    )
    {
        // Bucket file: A{firstChar}.html — e.g. for hash "d35..." → "Ad.html"
        var bucketChar = symbolHash[0];
        var bucketUrl = $"{BaseUrl}/{project}/A{bucketChar}.html";
        var bucketHtml = await FetchTextAsync(bucketUrl, ct);
        if (bucketHtml is null)
            return null;

        return ResolveFilePathFromBucket(bucketHtml, symbolHash);
    }
}

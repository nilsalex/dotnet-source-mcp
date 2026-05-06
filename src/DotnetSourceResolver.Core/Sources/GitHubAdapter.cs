using DotnetSourceResolver.Core.Models;
using DotnetSourceResolver.Core.Resolution;
using Microsoft.Extensions.Logging;

namespace DotnetSourceResolver.Core.Sources;

/// <summary>
/// Fetches raw source files from GitHub given an explicit permalink URL.
/// Also used as a shared fetch primitive by the other adapters.
///
/// As a standalone <see cref="ISourceAdapter"/> it only acts on requests that
/// already carry a pre-resolved <see cref="GitHubRequest"/> via
/// <see cref="GitHubRequest.From"/>.  Other adapters call
/// <see cref="FetchSnippetAsync"/> directly.
/// </summary>
public sealed class GitHubAdapter : ISourceAdapter
{
    private readonly HttpClient _http;
    private readonly ILogger<GitHubAdapter> _logger;

    // Injected by the DI container; HttpClientFactory sets the auth header.
    public GitHubAdapter(HttpClient http, ILogger<GitHubAdapter> logger)
    {
        _http = http;
        _logger = logger;
    }

    // -------------------------------------------------------------------------
    // ISourceAdapter — only handles GitHubRequest-carrying SymbolRequests
    // -------------------------------------------------------------------------

    public async Task<SourceResult?> TryResolveAsync(SymbolRequest request, CancellationToken ct)
    {
        var ghReq = GitHubRequest.From(request);
        if (ghReq is null)
            return null;

        var snippet = await FetchSnippetAsync(ghReq.RawUrl, ghReq.StartLine, ghReq.EndLine, ct);
        if (snippet is null)
            return null;

        var url = BuildPermalink(
            ghReq.Repository,
            ghReq.Commit,
            ghReq.Path,
            ghReq.StartLine,
            ghReq.EndLine
        );
        var entry = new SourceEntry(
            Kind: "github",
            Repository: ghReq.Repository,
            Commit: ghReq.Commit,
            Path: ghReq.Path,
            Url: url,
            StartLine: snippet.StartLine,
            EndLine: snippet.EndLine
        );

        return new SourceResult(
            Resolved: true,
            CanonicalSymbol: request.Symbol,
            ResolutionKind: ResolutionKind.GitHub,
            Confidence: string.IsNullOrEmpty(ghReq.Commit)
                ? ResolutionConfidence.Medium
                : ResolutionConfidence.High,
            Sources: [entry],
            Snippets: request.IncludeSnippets ? [snippet] : [],
            Diagnostics: [],
            ResolverVersion: ResolverVersionProvider.Version
        );
    }

    // -------------------------------------------------------------------------
    // Public primitive used by other adapters
    // -------------------------------------------------------------------------

    /// <summary>
    /// Downloads a raw GitHub file and extracts <paramref name="startLine"/>…
    /// <paramref name="endLine"/> (1-based, inclusive).
    /// Returns <c>null</c> on 404.
    /// Throws <see cref="HttpRequestException"/> on 401/403/429 and other errors.
    /// </summary>
    public async Task<SnippetEntry?> FetchSnippetAsync(
        string rawUrl,
        int startLine,
        int endLine,
        CancellationToken ct
    )
    {
        _logger.LogDebug("Fetching {Url}", rawUrl);

        using var response = await _http.GetAsync(rawUrl, ct);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            _logger.LogDebug("404 for {Url}", rawUrl);
            return null;
        }

        if ((int)response.StatusCode == 401 || (int)response.StatusCode == 403)
        {
            throw new HttpRequestException(
                $"GitHub returned {(int)response.StatusCode} for {rawUrl}. "
                    + "Set GITHUB_TOKEN to authenticate.",
                null,
                response.StatusCode
            );
        }

        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync(ct);
        return ExtractSnippet(rawUrl, content, startLine, endLine);
    }

    // -------------------------------------------------------------------------
    // Low-level HTTP (used by NuGetAdapter for URL validation)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Sends a raw HTTP request using this adapter's <see cref="HttpClient"/>
    /// (which carries the GitHub auth token if configured).
    /// </summary>
    public Task<HttpResponseMessage> SendRawAsync(
        HttpRequestMessage request,
        CancellationToken ct
    ) => _http.SendAsync(request, ct);

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    internal static SnippetEntry ExtractSnippet(
        string path,
        string fileContent,
        int startLine,
        int endLine
    )
    {
        var lines = fileContent.Split('\n');

        // Clamp to actual file length (1-based → 0-based index)
        var clampedStart = Math.Max(1, Math.Min(startLine, lines.Length));
        var clampedEnd = Math.Max(clampedStart, Math.Min(endLine, lines.Length));

        var slice = lines[(clampedStart - 1)..clampedEnd];
        var code = string.Join('\n', slice);

        // Derive a relative path from the raw URL for display purposes.
        var displayPath = TryExtractPathFromRawUrl(path) ?? path;

        return new SnippetEntry(displayPath, clampedStart, clampedEnd, code);
    }

    internal static string BuildPermalink(
        string repo,
        string commit,
        string filePath,
        int startLine,
        int endLine
    )
    {
        var repoBase = repo.TrimEnd('/');
        var sha = string.IsNullOrEmpty(commit) ? "HEAD" : commit;
        return $"{repoBase}/blob/{sha}/{filePath.TrimStart('/')}#L{startLine}-L{endLine}";
    }

    private static string? TryExtractPathFromRawUrl(string rawUrl)
    {
        // raw.githubusercontent.com/{owner}/{repo}/{ref}/{path}
        const string host = "raw.githubusercontent.com/";
        var idx = rawUrl.IndexOf(host, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            return null;

        var rest = rawUrl[(idx + host.Length)..];
        // skip owner/repo/ref — three segments
        var segments = rest.Split('/', 4);
        return segments.Length >= 4 ? segments[3] : null;
    }
}

// ---------------------------------------------------------------------------
// Small value type used to carry pre-resolved GitHub coordinates through a
// SymbolRequest.  Adapters embed this by stashing it in a custom subclass.
// ---------------------------------------------------------------------------

/// <summary>
/// Carries pre-resolved GitHub coordinates that tell <see cref="GitHubAdapter"/>
/// exactly what to fetch without any further discovery.
/// </summary>
public sealed record GitHubRequest(
    string Repository,
    string Commit,
    string Path,
    string RawUrl,
    int StartLine,
    int EndLine
)
{
    /// <summary>Returns the embedded <see cref="GitHubRequest"/> if present.</summary>
    public static GitHubRequest? From(SymbolRequest request) =>
        request is GitHubSymbolRequest ghReq ? ghReq.GitHub : null;
}

/// <summary>
/// A <see cref="SymbolRequest"/> that carries pre-resolved GitHub coordinates.
/// Used by other adapters to delegate file fetching to <see cref="GitHubAdapter"/>.
/// </summary>
public sealed record GitHubSymbolRequest(
    string Symbol,
    GitHubRequest GitHub,
    string? PackageId = null,
    string? PackageVersion = null,
    string? AssemblyName = null,
    string? TargetFramework = null,
    bool IncludeSnippets = true,
    int MaxSnippetLines = 80
)
    : SymbolRequest(
        Symbol,
        PackageId,
        PackageVersion,
        AssemblyName,
        TargetFramework,
        IncludeSnippets,
        MaxSnippetLines
    );

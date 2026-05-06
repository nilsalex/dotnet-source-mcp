using DotnetSourceResolver.Core.Models;
using DotnetSourceResolver.Core.Models.NuGet;
using DotnetSourceResolver.Core.NuGet;
using DotnetSourceResolver.Core.Resolution;
using Microsoft.Extensions.Logging;

namespace DotnetSourceResolver.Core.Sources;

/// <summary>
/// Resolves .NET symbols from NuGet packages by:
/// 1. Fetching .nuspec metadata to discover the repository URL + commit (fast, ~1 HTTP request).
/// 2. Downloading the .nupkg, extracting the DLL, and reading the embedded Portable PDB
///    to obtain Source Link JSON (slow, ~2 HTTP requests, result cached).
/// 3. Mapping the symbol name to a source file via Source Link patterns.
/// 4. If the heuristic URL 404s, searching the GitHub tree by filename as a fallback.
/// 5. Optionally fetching a code snippet from GitHub.
///
/// Falls back to Phase 1 (repository root, Medium confidence) if all phases fail.
/// Returns null if no <c>packageId</c> or <c>packageVersion</c> are provided.
/// </summary>
public class NuGetAdapter : ISourceAdapter
{
    private readonly NuSpecRepository _nuspec;
    private readonly NuGetPackageDownloader _downloader;
    private readonly SourceLinkExtractor _extractor;
    private readonly SourceLinkMatcher _matcher;
    private readonly GitHubFileLocator _locator;
    private readonly GitHubAdapter _github;
    private readonly ILogger<NuGetAdapter> _logger;

    public NuGetAdapter(
        NuSpecRepository nuspec,
        NuGetPackageDownloader downloader,
        SourceLinkExtractor extractor,
        SourceLinkMatcher matcher,
        GitHubFileLocator locator,
        GitHubAdapter github,
        ILogger<NuGetAdapter> logger
    )
    {
        _nuspec = nuspec;
        _downloader = downloader;
        _extractor = extractor;
        _matcher = matcher;
        _locator = locator;
        _github = github;
        _logger = logger;
    }

    public async Task<SourceResult?> TryResolveAsync(SymbolRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.PackageId))
            return null;

        if (string.IsNullOrWhiteSpace(request.PackageVersion))
        {
            _logger.LogWarning(
                "NuGetAdapter requires PackageVersion for {PackageId}",
                request.PackageId
            );
            return null;
        }

        // Phase 1: Repository discovery (nuspec metadata)
        var repoMeta = await _nuspec.GetRepositoryMetadataAsync(
            request.PackageId,
            request.PackageVersion,
            ct
        );

        if (repoMeta?.Url is null)
        {
            _logger.LogWarning(
                "No repository URL for {PackageId} {Version}",
                request.PackageId,
                request.PackageVersion
            );
            return null;
        }

        // Phase 2: Download package, extract DLL, read embedded PDB + Source Link
        var assemblyPath = await _downloader.DownloadAndExtractAssemblyAsync(
            request.PackageId,
            request.PackageVersion,
            request.TargetFramework,
            ct
        );

        if (assemblyPath is null)
        {
            _logger.LogInformation(
                "Could not extract assembly for {PackageId} {Version}, attempting no-Source-Link fallback",
                request.PackageId,
                request.PackageVersion
            );
            var noAssemblyFallback = await TryLocateWithoutSourceLinkAsync(request, repoMeta, ct);
            if (noAssemblyFallback is not null)
                return await BuildResultWithSnippetAsync(request, noAssemblyFallback, ct);
            return BuildRepoRootResult(request, repoMeta, ["NuGetAdapter: no assembly extracted"]);
        }

        var sourceLink = await _extractor.ExtractAsync(assemblyPath, ct);

        if (sourceLink is null)
        {
            _logger.LogInformation(
                "No Source Link in {AssemblyPath}, attempting no-Source-Link fallback",
                assemblyPath
            );
            var noSourceLinkFallback = await TryLocateWithoutSourceLinkAsync(request, repoMeta, ct);
            if (noSourceLinkFallback is not null)
                return await BuildResultWithSnippetAsync(request, noSourceLinkFallback, ct);
            return BuildRepoRootResult(
                request,
                repoMeta,
                ["NuGetAdapter: no Source Link in assembly"]
            );
        }

        // Phase 3a: Try direct PDB type lookup (exact file path + line number)
        SourceFileLocation? location = TryLocateViaTypeTable(
            assemblyPath,
            request.Symbol,
            sourceLink,
            repoMeta.Url,
            repoMeta.Commit ?? string.Empty
        );

        // Phase 3b: Fall back to heuristic namespace→path matching
        if (location is null)
        {
            location = _matcher.Match(
                request.Symbol,
                sourceLink,
                repoMeta.Url,
                repoMeta.Commit ?? string.Empty
            );
        }

        if (location is null)
        {
            _logger.LogInformation(
                "Source Link pattern did not match symbol {Symbol}, falling back to repo root",
                request.Symbol
            );
            return BuildRepoRootResult(
                request,
                repoMeta,
                ["NuGetAdapter: Source Link pattern did not match symbol"]
            );
        }

        // Phase 4: Validate the heuristic URL; if it 404s, search the GitHub tree by filename
        location = await ValidateOrFallbackAsync(location, repoMeta, request.Symbol, ct);

        // If tree search also failed (empty RawUrl), fall back to repo root
        if (string.IsNullOrEmpty(location.RawUrl))
        {
            _logger.LogInformation(
                "Tree search exhausted for {Symbol}, falling back to repository root",
                request.Symbol
            );
            return BuildRepoRootResult(
                request,
                repoMeta,
                [
                    $"NuGetAdapter: could not locate {Path.GetFileName(location.FilePath)} in repository tree",
                ]
            );
        }

        return await BuildResultWithSnippetAsync(request, location, ct);
    }

    // -------------------------------------------------------------------------
    // PDB type table lookup (Phase 3a)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Uses the PDB type table to find the exact source file path and line number for
    /// the symbol, then resolves it through the Source Link URL patterns.
    /// Returns null if the type is not found in the PDB or Source Link can't resolve the path.
    /// </summary>
    private SourceFileLocation? TryLocateViaTypeTable(
        string assemblyPath,
        string symbol,
        SourceLinkDocument sourceLink,
        string repositoryUrl,
        string commit
    )
    {
        var found = _extractor.FindTypeInPdb(assemblyPath, symbol);
        if (found is null)
        {
            _logger.LogDebug("PDB type table: {Symbol} not found in {Path}", symbol, assemblyPath);
            return null;
        }

        var (localPath, startLine) = found.Value;
        _logger.LogDebug(
            "PDB type table: {Symbol} → {LocalPath}:{StartLine}",
            symbol,
            localPath,
            startLine
        );

        // Resolve the local PDB path through Source Link patterns to get the raw URL
        var normalised = localPath.Replace('\\', '/');
        var rawUrl = SourceLinkMatcher.ResolveSourceLinkPattern(normalised, sourceLink.Documents);
        if (rawUrl is null)
        {
            _logger.LogDebug("Source Link pattern did not resolve PDB path {LocalPath}", localPath);
            return null;
        }

        // Parse repo/commit/filePath from raw URL
        var (repo, resolvedCommit, filePath) = ParseRawGitHubUrl(rawUrl);
        return new SourceFileLocation(
            Repository: repo ?? repositoryUrl,
            Commit: resolvedCommit ?? commit,
            FilePath: filePath ?? normalised,
            RawUrl: rawUrl,
            StartLine: startLine
        );
    }

    private static (string? repo, string? commit, string? filePath) ParseRawGitHubUrl(string rawUrl)
    {
        if (
            !rawUrl.StartsWith(
                "https://raw.githubusercontent.com/",
                StringComparison.OrdinalIgnoreCase
            )
        )
            return (null, null, null);

        var rest = rawUrl["https://raw.githubusercontent.com/".Length..];
        var parts = rest.Split('/', 4);
        if (parts.Length < 4)
            return (null, null, null);

        return ($"https://github.com/{parts[0]}/{parts[1]}", parts[2], parts[3]);
    }

    // -------------------------------------------------------------------------
    // URL validation with tree-search fallback
    // -------------------------------------------------------------------------

    /// <summary>
    /// Issues a HEAD request against the heuristic raw URL. If the server returns 404
    /// and we have a GitHub repo + commit, searches the tree for the file by name.
    /// Returns the original location if the URL is valid or if the fallback also fails.
    /// </summary>
    private async Task<SourceFileLocation> ValidateOrFallbackAsync(
        SourceFileLocation location,
        RepositoryMetadata repoMeta,
        string symbol,
        CancellationToken ct
    )
    {
        // Only bother checking raw.githubusercontent.com URLs
        if (
            !location.RawUrl.StartsWith(
                "https://raw.githubusercontent.com/",
                StringComparison.OrdinalIgnoreCase
            )
        )
            return location;

        // Try HEAD to check existence cheaply
        bool exists;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Head, location.RawUrl);
            using var resp = await _github.SendRawAsync(req, ct);
            exists = resp.IsSuccessStatusCode;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // If we can't even HEAD, assume valid and let downstream handle it
            return location;
        }

        if (exists)
            return location;

        _logger.LogDebug(
            "Heuristic URL 404'd ({Url}), searching GitHub tree for {FileName}",
            location.RawUrl,
            Path.GetFileName(location.FilePath)
        );

        // Extract owner/repo/commit from the repository URL
        if (
            repoMeta.Url is null
            || !TryParseGitHubRepoUrl(repoMeta.Url, out var owner, out var repo)
        )
            return location;

        var commit = repoMeta.Commit;
        if (string.IsNullOrEmpty(commit))
            return location;

        var fileName = Path.GetFileName(location.FilePath);
        // Pass a preferred sub-path hint to bias toward source (not test) directories.
        // Derive it from the Source Link URL pattern if available — the URL prefix before
        // the wildcard gives us the repo-relative source root.
        var foundPath = await _locator.FindFileAsync(owner!, repo!, commit, fileName, ct);

        if (foundPath is null)
        {
            _logger.LogDebug(
                "GitHub tree search found no file named {FileName} in {Owner}/{Repo}@{Commit}",
                fileName,
                owner,
                repo,
                commit
            );
            // Signal caller that we found nothing by returning null RawUrl
            return location with { RawUrl = string.Empty };
        }

        _logger.LogInformation(
            "GitHub tree search found {FileName} at {Path}",
            fileName,
            foundPath
        );

        var rawUrl = $"https://raw.githubusercontent.com/{owner}/{repo}/{commit}/{foundPath}";

        return location with
        {
            FilePath = foundPath,
            RawUrl = rawUrl,
        };
    }

    private async Task<SourceFileLocation?> TryLocateWithoutSourceLinkAsync(
        SymbolRequest request,
        RepositoryMetadata repoMeta,
        CancellationToken ct
    )
    {
        if (string.IsNullOrEmpty(repoMeta.Url) || string.IsNullOrEmpty(repoMeta.Commit))
            return null;

        if (!TryParseGitHubRepoUrl(repoMeta.Url, out var owner, out var repo))
            return null;

        var commit = repoMeta.Commit;
        var candidates = SourceLinkMatcher.GuessFilePathsFromSymbol(request.Symbol).ToList();

        foreach (var candidate in candidates)
        {
            var rawUrl = $"https://raw.githubusercontent.com/{owner}/{repo}/{commit}/{candidate}";

            bool exists;
            try
            {
                using var headReq = new HttpRequestMessage(HttpMethod.Head, rawUrl);
                using var headResp = await _github.SendRawAsync(headReq, ct);
                exists = headResp.IsSuccessStatusCode;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                continue;
            }

            if (!exists)
                continue;

            _logger.LogInformation("No-Source-Link fallback: HEAD confirmed {Url}", rawUrl);

            return new SourceFileLocation(
                Repository: repoMeta.Url,
                Commit: commit,
                FilePath: candidate,
                RawUrl: rawUrl
            );
        }

        _logger.LogDebug(
            "No-Source-Link fallback: all HEAD requests failed for {Symbol}, trying tree search",
            request.Symbol
        );

        var shortName = candidates
            .Select(p => Path.GetFileName(p))
            .FirstOrDefault();

        if (shortName is null)
            return null;

        var foundPath = await _locator.FindFileAsync(owner, repo, commit, shortName, ct);

        if (foundPath is null)
        {
            _logger.LogDebug(
                "No-Source-Link fallback: tree search found no file named {FileName} in {Owner}/{Repo}@{Commit}",
                shortName,
                owner,
                repo,
                commit
            );
            return null;
        }

        _logger.LogInformation(
            "No-Source-Link fallback: tree search found {FileName} at {Path}",
            shortName,
            foundPath
        );

        var treeRawUrl = $"https://raw.githubusercontent.com/{owner}/{repo}/{commit}/{foundPath}";

        return new SourceFileLocation(
            Repository: repoMeta.Url,
            Commit: commit,
            FilePath: foundPath,
            RawUrl: treeRawUrl
        );
    }

    private static bool TryParseGitHubRepoUrl(string repoUrl, out string? owner, out string? repo)
    {
        owner = null;
        repo = null;

        // https://github.com/{owner}/{repo}[/...]
        if (!repoUrl.Contains("github.com", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!Uri.TryCreate(repoUrl, UriKind.Absolute, out var uri))
            return false;

        var segments = uri.AbsolutePath.Trim('/').Split('/');
        if (segments.Length < 2)
            return false;

        owner = segments[0];
        repo = segments[1];
        return true;
    }

    // -------------------------------------------------------------------------
    // Result builders
    // -------------------------------------------------------------------------

    private static SourceResult BuildRepoRootResult(
        SymbolRequest request,
        RepositoryMetadata repoMeta,
        IReadOnlyList<string> diagnostics
    )
    {
        var repoUrl = repoMeta.Url ?? string.Empty;
        var url = repoMeta.Commit is not null
            ? $"{repoUrl.TrimEnd('/')}/tree/{repoMeta.Commit}"
            : repoUrl;

        var isGitHub = repoUrl.Contains("github.com", StringComparison.OrdinalIgnoreCase);
        var confidence = isGitHub ? ResolutionConfidence.Medium : ResolutionConfidence.Low;

        var entry = new SourceEntry(
            Kind: "nuget",
            Repository: repoUrl,
            Commit: repoMeta.Commit ?? string.Empty,
            Path: string.Empty,
            Url: url,
            StartLine: 0,
            EndLine: 0
        );

        return new SourceResult(
            Resolved: true,
            CanonicalSymbol: request.Symbol,
            ResolutionKind: ResolutionKind.NuGet,
            Confidence: confidence,
            Sources: [entry],
            Snippets: [],
            Diagnostics: diagnostics,
            ResolverVersion: ResolverVersionProvider.Version
        );
    }

    private static SourceResult BuildFileResult(SymbolRequest request, SourceFileLocation location)
    {
        var startLine = location.StartLine ?? 1;
        var endLine = location.EndLine ?? startLine;

        var url = GitHubAdapter.BuildPermalink(
            location.Repository,
            location.Commit,
            location.FilePath,
            startLine,
            endLine
        );

        var entry = new SourceEntry(
            Kind: "nuget",
            Repository: location.Repository,
            Commit: location.Commit,
            Path: location.FilePath,
            Url: url,
            StartLine: startLine,
            EndLine: endLine
        );

        return new SourceResult(
            Resolved: true,
            CanonicalSymbol: request.Symbol,
            ResolutionKind: ResolutionKind.NuGet,
            Confidence: string.IsNullOrEmpty(location.Commit)
                ? ResolutionConfidence.Medium
                : ResolutionConfidence.High,
            Sources: [entry],
            Snippets: [],
            Diagnostics: [],
            ResolverVersion: ResolverVersionProvider.Version
        );
    }

    private async Task<SourceResult> BuildResultWithSnippetAsync(
        SymbolRequest request,
        SourceFileLocation location,
        CancellationToken ct
    )
    {
        if (request.IncludeSnippets)
        {
            var ghReq = new GitHubSymbolRequest(
                Symbol: request.Symbol,
                GitHub: new GitHubRequest(
                    Repository: location.Repository,
                    Commit: location.Commit,
                    Path: location.FilePath,
                    RawUrl: location.RawUrl,
                    StartLine: location.StartLine ?? 1,
                    EndLine: location.EndLine ?? int.MaxValue
                ),
                PackageId: request.PackageId,
                PackageVersion: request.PackageVersion,
                AssemblyName: request.AssemblyName,
                TargetFramework: request.TargetFramework,
                IncludeSnippets: true,
                MaxSnippetLines: request.MaxSnippetLines
            );

            try
            {
                var ghResult = await _github.TryResolveAsync(ghReq, ct);
                if (ghResult is not null)
                    return ghResult with { ResolutionKind = ResolutionKind.NuGet };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "GitHub snippet fetch failed for {Url}", location.RawUrl);
            }
        }

        return BuildFileResult(request, location);
    }
}

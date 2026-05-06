using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace DotnetSourceResolver.Core.NuGet;

/// <summary>
/// Searches a GitHub repository tree by filename to find the actual path of a source file.
/// Used as a fallback when Source Link path heuristics produce a URL that does not exist.
/// Supports exact filename match and PascalCase-prefix fuzzy matching.
/// </summary>
public class GitHubFileLocator
{
    private readonly HttpClient _http;
    private readonly ILogger<GitHubFileLocator> _logger;

    public GitHubFileLocator(HttpClient http, ILogger<GitHubFileLocator> logger)
    {
        _http = http;
        _logger = logger;
    }

    /// <summary>
    /// Searches the repository tree for a file named <paramref name="fileName"/> at the
    /// given commit SHA. Returns the first matching path (relative to repo root), or null.
    /// Falls back to PascalCase-prefix matching when no exact filename match is found.
    /// <paramref name="preferredSubPath"/> biases results toward a specific sub-directory.
    /// </summary>
    public async Task<string?> FindFileAsync(
        string owner,
        string repo,
        string commitSha,
        string fileName,
        CancellationToken ct,
        string? preferredSubPath = null
    )
    {
        var url = $"https://api.github.com/repos/{owner}/{repo}/git/trees/{commitSha}?recursive=1";

        _logger.LogDebug("Searching GitHub tree {Url} for {FileName}", url, fileName);

        string json;
        try
        {
            var response = await _http.GetAsync(url, ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "GitHub tree API returned {StatusCode} for {Url}",
                    (int)response.StatusCode,
                    url
                );
                return null;
            }

            json = await response.Content.ReadAsStringAsync(ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to query GitHub tree API for {Url}", url);
            return null;
        }

        return FindInTreeJson(json, fileName, preferredSubPath);
    }

    /// <summary>
    /// Parses the GitHub tree API JSON and finds the best matching blob path for
    /// <paramref name="fileName"/>. Strategy:
    /// 1. Exact case-insensitive filename match.
    /// 2. PascalCase-prefix match (progressively shorter prefixes, avoiding test paths).
    /// </summary>
    internal static string? FindInTreeJson(
        string json,
        string fileName,
        string? preferredSubPath = null
    )
    {
        try
        {
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("tree", out var tree))
                return null;

            var nodes = tree.EnumerateArray()
                .Where(n => n.TryGetProperty("type", out var t) && t.GetString() == "blob")
                .Select(n => n.TryGetProperty("path", out var p) ? p.GetString() : null)
                .Where(p => p is not null)
                .Select(p => p!)
                .ToList();

            // 1. Exact filename match
            var exact = nodes.FirstOrDefault(p =>
                string.Equals(LastSegment(p), fileName, StringComparison.OrdinalIgnoreCase)
            );
            if (exact is not null)
                return exact;

            // 2. PascalCase-prefix match: "BffManagementEndpointsExtensions.cs"
            //    → try "BffManagementEndpointsExtensions", "BffManagementEndpoints",
            //          "BffManagement", ... as filename prefixes
            var stem = Path.GetFileNameWithoutExtension(fileName);
            foreach (var prefix in PascalCasePrefixes(stem))
            {
                if (prefix.Length < 6)
                    break; // too short — too many false positives

                string? best = null;
                int bestScore = int.MinValue;

                foreach (var path in nodes)
                {
                    var name = Path.GetFileNameWithoutExtension(LastSegment(path));
                    if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        continue;

                    int score = 0;
                    // Penalise test paths (starts with "test/" or contains "/test/" or "/tests/")
                    if (
                        path.StartsWith("test/", StringComparison.OrdinalIgnoreCase)
                        || path.StartsWith("tests/", StringComparison.OrdinalIgnoreCase)
                        || path.Contains("/test/", StringComparison.OrdinalIgnoreCase)
                        || path.Contains("/tests/", StringComparison.OrdinalIgnoreCase)
                        || Path.GetFileNameWithoutExtension(LastSegment(path))
                            .EndsWith("Tests", StringComparison.OrdinalIgnoreCase)
                        || Path.GetFileNameWithoutExtension(LastSegment(path))
                            .EndsWith("Test", StringComparison.OrdinalIgnoreCase)
                    )
                        score -= 20;
                    if (
                        preferredSubPath is not null
                        && path.StartsWith(preferredSubPath, StringComparison.OrdinalIgnoreCase)
                    )
                        score += 5;
                    score -= path.Split('/').Length; // shorter path wins

                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = path;
                    }
                }

                if (best is not null)
                    return best;
            }
        }
        catch (JsonException)
        {
            // Malformed response — return null
        }

        return null;
    }

    private static string LastSegment(string path)
    {
        var idx = path.LastIndexOf('/');
        return idx >= 0 ? path[(idx + 1)..] : path;
    }

    /// <summary>
    /// Yields progressively shorter PascalCase prefixes of a name, longest first.
    /// "BffManagementEndpointsExtensions" → ["BffManagementEndpointsExtensions",
    ///  "BffManagementEndpoints", "BffManagement", "Bff"]
    /// </summary>
    internal static IEnumerable<string> PascalCasePrefixes(string name)
    {
        yield return name;

        var boundaries = new List<int>();
        for (int i = 1; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]))
                boundaries.Add(i);
        }

        for (int i = boundaries.Count - 1; i >= 0; i--)
            yield return name[..boundaries[i]];
    }
}

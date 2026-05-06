using System.Xml.Linq;
using DotnetSourceResolver.Core.Models.NuGet;
using Microsoft.Extensions.Logging;

namespace DotnetSourceResolver.Core.NuGet;

/// <summary>
/// Fetches and parses .nuspec metadata from the NuGet v3 flat-container API.
/// Extracts &lt;repository&gt; metadata (URL, commit, branch) with a fallback to &lt;projectUrl&gt;.
/// </summary>
public class NuSpecRepository
{
    private readonly HttpClient _http;
    private readonly ILogger<NuSpecRepository> _logger;

    public NuSpecRepository(HttpClient http, ILogger<NuSpecRepository> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<RepositoryMetadata?> GetRepositoryMetadataAsync(
        string packageId,
        string packageVersion,
        CancellationToken ct
    )
    {
        var id = packageId.ToLowerInvariant();
        var version = packageVersion.ToLowerInvariant();
        var url = $"https://api.nuget.org/v3-flatcontainer/{id}/{version}/{id}.nuspec";

        string xml;
        try
        {
            var response = await _http.GetAsync(url, ct);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _logger.LogWarning(
                    "NuSpec not found for {PackageId} {Version}: {Url}",
                    packageId,
                    packageVersion,
                    url
                );
                return null;
            }

            response.EnsureSuccessStatusCode();
            xml = await response.Content.ReadAsStringAsync(ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to fetch .nuspec for {PackageId} {Version}",
                packageId,
                packageVersion
            );
            return null;
        }

        var metadata = ParseNuSpec(xml);
        if (metadata is null)
        {
            _logger.LogWarning(
                "Could not extract repository metadata from .nuspec for {PackageId} {Version}",
                packageId,
                packageVersion
            );
        }

        return metadata;
    }

    /// <summary>
    /// Parses a .nuspec XML string and returns repository metadata.
    /// Primary source: &lt;repository&gt; element.
    /// Fallback: &lt;projectUrl&gt; if it points to GitHub.
    /// </summary>
    internal static RepositoryMetadata? ParseNuSpec(string xml)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml);
        }
        catch
        {
            return null;
        }

        // .nuspec uses a versioned namespace, e.g. http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd
        // Use the local-name() approach to be namespace-agnostic.
        var ns = doc.Root?.Name.Namespace ?? XNamespace.None;

        var metadata = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "metadata");

        if (metadata is null)
            return null;

        // Try <repository> element first
        var repo = metadata.Elements().FirstOrDefault(e => e.Name.LocalName == "repository");

        if (repo is not null)
        {
            var repoUrl = repo.Attribute("url")?.Value;
            var commit = repo.Attribute("commit")?.Value;
            var branch = repo.Attribute("branch")?.Value;
            var type = repo.Attribute("type")?.Value;

            if (!string.IsNullOrWhiteSpace(repoUrl))
            {
                return new RepositoryMetadata(
                    Url: repoUrl,
                    Commit: string.IsNullOrWhiteSpace(commit) ? null : commit,
                    Branch: string.IsNullOrWhiteSpace(branch) ? null : branch,
                    Type: string.IsNullOrWhiteSpace(type) ? null : type
                );
            }
        }

        // Fallback: <projectUrl>
        var projectUrl = metadata
            .Elements()
            .FirstOrDefault(e => e.Name.LocalName == "projectUrl")
            ?.Value;

        var extractedUrl = ExtractRepoFromProjectUrl(projectUrl);
        if (extractedUrl is not null)
        {
            return new RepositoryMetadata(
                Url: extractedUrl,
                Commit: null,
                Branch: null,
                Type: "git"
            );
        }

        return null;
    }

    /// <summary>
    /// Returns the GitHub repository root URL from a projectUrl if it matches GitHub,
    /// otherwise returns null.
    /// </summary>
    internal static string? ExtractRepoFromProjectUrl(string? projectUrl)
    {
        if (string.IsNullOrWhiteSpace(projectUrl))
            return null;

        if (!Uri.TryCreate(projectUrl, UriKind.Absolute, out var uri))
            return null;

        var host = uri.Host.ToLowerInvariant();
        if (host != "github.com")
            return null;

        // Keep only the first two path segments: /{owner}/{repo}
        var segments = uri.AbsolutePath.Trim('/').Split('/');
        if (segments.Length < 2)
            return null;

        return $"https://github.com/{segments[0]}/{segments[1]}";
    }
}

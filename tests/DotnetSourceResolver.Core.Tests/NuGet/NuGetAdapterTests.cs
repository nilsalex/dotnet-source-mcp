using System.Net;
using DotnetSourceResolver.Core.Models;
using DotnetSourceResolver.Core.Models.NuGet;
using DotnetSourceResolver.Core.NuGet;
using DotnetSourceResolver.Core.Sources;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;

namespace DotnetSourceResolver.Core.Tests.NuGet;

public class NuGetAdapterTests
{
    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private const string PackageId = "Duende.BFF";
    private const string PackageVersion = "3.1.0";
    private const string Symbol = "Duende.BFF.DefaultUserService";
    private const string RepoUrl = "https://github.com/DuendeSoftware/products";
    private const string Commit = "0ca420dd34e43d6189d33fb27f8a543963050cab";

    private static readonly RepositoryMetadata ValidRepoMeta = new(
        Url: RepoUrl,
        Commit: Commit,
        Branch: "refs/heads/releases/bff/3.1.x",
        Type: "git"
    );

    private static readonly SourceLinkDocument ValidSourceLink = new(
        new Dictionary<string, string>
        {
            [$"C:\\build\\src\\*"] =
                $"https://raw.githubusercontent.com/DuendeSoftware/products/{Commit}/src/*",
        }
    );

    /// <summary>
    /// Builds a NuGetAdapter with real sub-services (NuSpec mocked via HTTP),
    /// plus a mocked GitHubAdapter for snippet fetching.
    /// </summary>
    private static (NuGetAdapter adapter, string cacheDir) BuildAdapter(
        RepositoryMetadata? repoMeta,
        string? assemblyPath,
        SourceLinkDocument? sourceLink,
        SnippetEntry? snippetEntry = null,
        string? locatorFilePath = null
    )
    {
        var cacheDir = Path.Combine(Path.GetTempPath(), $"nuget-adapter-test-{Guid.NewGuid()}");

        // Mock NuSpecRepository's HTTP
        var nuspecHtml = repoMeta is not null
            ? BuildNuSpecXml(repoMeta)
            : "<package><metadata></metadata></package>";

        var nuspecHttpHandler = new Mock<HttpMessageHandler>();
        nuspecHttpHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(
                repoMeta is not null
                    ? new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(nuspecHtml),
                    }
                    : new HttpResponseMessage(HttpStatusCode.NotFound)
            );
        var nuspec = new NuSpecRepository(
            new System.Net.Http.HttpClient(nuspecHttpHandler.Object),
            NullLogger<NuSpecRepository>.Instance
        );

        // Mock NuGetPackageDownloader's HTTP
        var downloaderHttpHandler = new Mock<HttpMessageHandler>();
        downloaderHttpHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.NotFound)); // no real download

        // If assemblyPath is provided, pre-populate the cache so downloader returns it
        NuGetPackageDownloader downloader;
        if (assemblyPath is not null)
        {
            // Cache hit: pre-place the DLL at the expected cache location
            var cachedFolder = Path.Combine(
                cacheDir,
                "nuget-packages",
                PackageId.ToLowerInvariant(),
                PackageVersion.ToLowerInvariant(),
                "best"
            );
            Directory.CreateDirectory(cachedFolder);
            File.Copy(
                assemblyPath,
                Path.Combine(cachedFolder, $"{PackageId}.dll"),
                overwrite: true
            );
        }

        downloader = new NuGetPackageDownloader(
            new System.Net.Http.HttpClient(downloaderHttpHandler.Object),
            NullLogger<NuGetPackageDownloader>.Instance,
            new CacheConfiguration { CacheDirectory = cacheDir }
        );

        var extractor = new SourceLinkExtractor(NullLogger<SourceLinkExtractor>.Instance);
        var matcher = new SourceLinkMatcher(NullLogger<SourceLinkMatcher>.Instance);

        // Build GitHubAdapter: HEAD returns 200 (so URL validation passes), GET returns snippet or 404
        var ghHandler = new Mock<HttpMessageHandler>();
        // HEAD → 200 (URL exists, skip tree search) for Source Link path validation
        ghHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(r => r.Method == HttpMethod.Head),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK));

        // Override: HEAD → 404 for raw.githubusercontent.com (prevents no-Source-Link fallback from succeeding)
        ghHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(r =>
                    r.Method == HttpMethod.Head
                    && r.RequestUri != null
                    && r.RequestUri.Host.Equals(
                        "raw.githubusercontent.com",
                        StringComparison.OrdinalIgnoreCase
                    )),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.NotFound));

        if (snippetEntry is not null)
        {
            ghHandler
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.Is<HttpRequestMessage>(r => r.Method == HttpMethod.Get),
                    ItExpr.IsAny<CancellationToken>()
                )
                .ReturnsAsync(
                    new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("line1\nline2\nline3"),
                    }
                );
        }
        else
        {
            ghHandler
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.Is<HttpRequestMessage>(r => r.Method == HttpMethod.Get),
                    ItExpr.IsAny<CancellationToken>()
                )
                .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        var github = new GitHubAdapter(
            new System.Net.Http.HttpClient(ghHandler.Object),
            NullLogger<GitHubAdapter>.Instance
        );

        // GitHubFileLocator — returns null by default, or a specific path if configured
        var locatorHandler = new Mock<HttpMessageHandler>();
        if (locatorFilePath is not null)
        {
            var treeJson = $$"""{"tree":[{"type":"blob","path":"{{locatorFilePath}}"}]}""";
            locatorHandler
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>()
                )
                .ReturnsAsync(
                    new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(treeJson),
                    }
                );
        }
        else
        {
            locatorHandler
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>()
                )
                .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
        var locator = new GitHubFileLocator(
            new System.Net.Http.HttpClient(locatorHandler.Object),
            NullLogger<GitHubFileLocator>.Instance
        );

        var adapter = new NuGetAdapter(
            nuspec,
            downloader,
            extractor,
            matcher,
            locator,
            github,
            NullLogger<NuGetAdapter>.Instance
        );

        return (adapter, cacheDir);
    }

    private static SymbolRequest MakeRequest(
        string? packageId = PackageId,
        string? packageVersion = PackageVersion,
        bool includeSnippets = false
    ) =>
        new(
            Symbol: Symbol,
            PackageId: packageId,
            PackageVersion: packageVersion,
            IncludeSnippets: includeSnippets
        );

    private static string BuildNuSpecXml(RepositoryMetadata meta) =>
        $"""
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
              <metadata>
                <id>{PackageId}</id>
                <version>{PackageVersion}</version>
                <repository type="{meta.Type ?? "git"}"
                            url="{meta.Url}"
                            commit="{meta.Commit ?? ""}" />
              </metadata>
            </package>
            """;

    // -------------------------------------------------------------------------
    // Guard clauses
    // -------------------------------------------------------------------------

    [Fact]
    public async Task TryResolveAsync_NoPackageId_ReturnsNull()
    {
        var (adapter, cacheDir) = BuildAdapter(ValidRepoMeta, null, ValidSourceLink);
        try
        {
            var result = await adapter.TryResolveAsync(MakeRequest(packageId: null), default);
            Assert.Null(result);
        }
        finally
        {
            if (Directory.Exists(cacheDir))
                Directory.Delete(cacheDir, recursive: true);
        }
    }

    [Fact]
    public async Task TryResolveAsync_NoPackageVersion_ReturnsNull()
    {
        var (adapter, cacheDir) = BuildAdapter(ValidRepoMeta, null, ValidSourceLink);
        try
        {
            var result = await adapter.TryResolveAsync(MakeRequest(packageVersion: null), default);
            Assert.Null(result);
        }
        finally
        {
            if (Directory.Exists(cacheDir))
                Directory.Delete(cacheDir, recursive: true);
        }
    }

    [Fact]
    public async Task TryResolveAsync_NoRepository_ReturnsNull()
    {
        var (adapter, cacheDir) = BuildAdapter(repoMeta: null, null, null);
        try
        {
            var result = await adapter.TryResolveAsync(MakeRequest(), default);
            Assert.Null(result);
        }
        finally
        {
            if (Directory.Exists(cacheDir))
                Directory.Delete(cacheDir, recursive: true);
        }
    }

    // -------------------------------------------------------------------------
    // Phase 1 fallback (no assembly)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task TryResolveAsync_RepositoryOnly_ReturnsMediumConfidence()
    {
        // No assembly path → downloader returns null → falls back to repo root
        var (adapter, cacheDir) = BuildAdapter(ValidRepoMeta, assemblyPath: null, null);
        try
        {
            var result = await adapter.TryResolveAsync(MakeRequest(), default);

            Assert.NotNull(result);
            Assert.True(result.Resolved);
            Assert.Equal(ResolutionKind.NuGet, result.ResolutionKind);
            Assert.Equal(ResolutionConfidence.Medium, result.Confidence);
            Assert.Single(result.Sources);
            Assert.Contains(RepoUrl, result.Sources[0].Url);
            Assert.Contains(Commit, result.Sources[0].Url);
        }
        finally
        {
            if (Directory.Exists(cacheDir))
                Directory.Delete(cacheDir, recursive: true);
        }
    }

    [Fact]
    public async Task TryResolveAsync_NonGitHubRepo_ReturnsLowConfidence()
    {
        var azureMeta = new RepositoryMetadata(
            Url: "https://dev.azure.com/myorg/myproject",
            Commit: "abc123",
            Branch: null,
            Type: "tfsgit"
        );
        var (adapter, cacheDir) = BuildAdapter(azureMeta, assemblyPath: null, null);
        try
        {
            var result = await adapter.TryResolveAsync(MakeRequest(), default);

            Assert.NotNull(result);
            Assert.Equal(ResolutionConfidence.Low, result.Confidence);
        }
        finally
        {
            if (Directory.Exists(cacheDir))
                Directory.Delete(cacheDir, recursive: true);
        }
    }

    // -------------------------------------------------------------------------
    // Full pipeline tests (need a real DLL with embedded PDB)
    // -------------------------------------------------------------------------

    /// <summary>Builds an in-memory DLL with embedded PDB containing Source Link.</summary>
    private static string CreateAssemblyWithSourceLink(
        string cacheDir,
        string? sourceLinkJson = null
    )
    {
        var json =
            sourceLinkJson
            ?? "{\"documents\":{\"C:\\\\build\\\\src\\\\*\":\"https://raw.githubusercontent.com/DuendeSoftware/products/"
                + Commit
                + "/src/*\"}}";

        // Use SourceLinkExtractorTests helper logic inline
        var dllBytes = SourceLinkExtractorTestHelper.BuildAssemblyWithEmbeddedPdb(json);
        var path = Path.Combine(cacheDir, "TestAssembly.dll");
        Directory.CreateDirectory(cacheDir);
        File.WriteAllBytes(path, dllBytes);
        return path;
    }

    [Fact]
    public async Task TryResolveAsync_WithSourceLink_ReturnsHighConfidence()
    {
        var cacheDir = Path.Combine(Path.GetTempPath(), $"nuget-sl-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(cacheDir);
        var assemblyPath = CreateAssemblyWithSourceLink(cacheDir);

        var (adapter, adapterCacheDir) = BuildAdapter(ValidRepoMeta, assemblyPath, ValidSourceLink,
            locatorFilePath: "src/Duende/BFF/DefaultUserService.cs");
        try
        {
            var result = await adapter.TryResolveAsync(
                MakeRequest(includeSnippets: false),
                default
            );

            Assert.NotNull(result);
            Assert.True(result.Resolved);
            Assert.Equal(ResolutionKind.NuGet, result.ResolutionKind);
            Assert.Equal(ResolutionConfidence.High, result.Confidence);
            Assert.Single(result.Sources);
            Assert.Contains("DefaultUserService.cs", result.Sources[0].Path);
        }
        finally
        {
            if (Directory.Exists(cacheDir))
                Directory.Delete(cacheDir, recursive: true);
            if (Directory.Exists(adapterCacheDir))
                Directory.Delete(adapterCacheDir, recursive: true);
        }
    }

    [Fact]
    public async Task TryResolveAsync_WithSourceLinkAndSnippets_FetchesFromGitHub()
    {
        var cacheDir = Path.Combine(Path.GetTempPath(), $"nuget-snippet-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(cacheDir);
        var assemblyPath = CreateAssemblyWithSourceLink(cacheDir);

        var dummySnippet = new SnippetEntry("DefaultUserService.cs", 1, 3, "line1\nline2\nline3");
        var (adapter, adapterCacheDir) = BuildAdapter(
            ValidRepoMeta,
            assemblyPath,
            ValidSourceLink,
            snippetEntry: dummySnippet,
            locatorFilePath: "src/Duende/BFF/DefaultUserService.cs"
        );
        try
        {
            var result = await adapter.TryResolveAsync(MakeRequest(includeSnippets: true), default);

            Assert.NotNull(result);
            Assert.True(result.Resolved);
            Assert.Equal(ResolutionKind.NuGet, result.ResolutionKind);
            Assert.Single(result.Snippets);
        }
        finally
        {
            if (Directory.Exists(cacheDir))
                Directory.Delete(cacheDir, recursive: true);
            if (Directory.Exists(adapterCacheDir))
                Directory.Delete(adapterCacheDir, recursive: true);
        }
    }
}

using DotnetSourceResolver.Core.Models;
using DotnetSourceResolver.Core.Models.NuGet;
using DotnetSourceResolver.Core.NuGet;
using DotnetSourceResolver.Core.Resolution;
using DotnetSourceResolver.Core.Sources;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetSourceResolver.Core.Tests.Live;

/// <summary>
/// Live tests for NuGet package source resolution.
/// Guard with RESOLVER_RUN_LIVE_TESTS=true to enable.
/// </summary>
[Trait("Category", "Live")]
public class NuGetLiveTests
{
    private static bool LiveTestsEnabled =>
        string.Equals(
            Environment.GetEnvironmentVariable("RESOLVER_RUN_LIVE_TESTS"),
            "true",
            StringComparison.OrdinalIgnoreCase
        );

    private static readonly string LiveCacheDir = Path.Combine(
        Path.GetTempPath(),
        "dotnet-source-resolver-live-test-cache"
    );

    private static NuGetAdapter BuildLiveNuGetAdapter()
    {
        var githubToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN");

        var githubHttp = new HttpClient();
        githubHttp.DefaultRequestHeaders.Add("User-Agent", "dotnet-source-resolver/live-test");
        if (!string.IsNullOrEmpty(githubToken))
            githubHttp.DefaultRequestHeaders.Add("Authorization", $"token {githubToken}");
        var github = new GitHubAdapter(githubHttp, NullLogger<GitHubAdapter>.Instance);

        var nuspecHttp = new HttpClient();
        nuspecHttp.DefaultRequestHeaders.Add("User-Agent", "dotnet-source-resolver/live-test");
        var nuspec = new NuSpecRepository(nuspecHttp, NullLogger<NuSpecRepository>.Instance);

        var downloaderHttp = new HttpClient();
        downloaderHttp.DefaultRequestHeaders.Add("User-Agent", "dotnet-source-resolver/live-test");
        downloaderHttp.Timeout = TimeSpan.FromMinutes(2);
        var downloader = new NuGetPackageDownloader(
            downloaderHttp,
            NullLogger<NuGetPackageDownloader>.Instance,
            new CacheConfiguration { CacheDirectory = LiveCacheDir }
        );

        var extractor = new SourceLinkExtractor(NullLogger<SourceLinkExtractor>.Instance);
        var matcher = new SourceLinkMatcher(NullLogger<SourceLinkMatcher>.Instance);

        var locatorHttp = new HttpClient();
        locatorHttp.DefaultRequestHeaders.Add("User-Agent", "dotnet-source-resolver/live-test");
        if (!string.IsNullOrEmpty(githubToken))
            locatorHttp.DefaultRequestHeaders.Add("Authorization", $"token {githubToken}");
        var locator = new GitHubFileLocator(locatorHttp, NullLogger<GitHubFileLocator>.Instance);

        return new NuGetAdapter(
            nuspec,
            downloader,
            extractor,
            matcher,
            locator,
            github,
            NullLogger<NuGetAdapter>.Instance
        );
    }

    private static DotNetSourceResolver BuildLiveResolverWithNuGet()
    {
        var githubToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN");

        var githubHttp = new HttpClient();
        githubHttp.DefaultRequestHeaders.Add("User-Agent", "dotnet-source-resolver/live-test");
        if (!string.IsNullOrEmpty(githubToken))
            githubHttp.DefaultRequestHeaders.Add("Authorization", $"token {githubToken}");
        var github = new GitHubAdapter(githubHttp, NullLogger<GitHubAdapter>.Instance);

        var sourceDotNetHttp = new HttpClient();
        sourceDotNetHttp.DefaultRequestHeaders.Add(
            "User-Agent",
            "dotnet-source-resolver/live-test"
        );
        var sourceDotNet = new SourceDotNetAdapter(
            sourceDotNetHttp,
            github,
            NullLogger<SourceDotNetAdapter>.Instance
        );

        var docsHttp = new HttpClient();
        docsHttp.DefaultRequestHeaders.Add("User-Agent", "dotnet-source-resolver/live-test");
        var docs = new DocsSourceLinkAdapter(
            docsHttp,
            github,
            NullLogger<DocsSourceLinkAdapter>.Instance
        );

        var nuget = BuildLiveNuGetAdapter();

        return new DotNetSourceResolver(
            [sourceDotNet, docs, nuget],
            NullLogger<DotNetSourceResolver>.Instance
        );
    }

    // -------------------------------------------------------------------------
    // NuSpec repository discovery (Phase 1)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task NuSpecRepository_DuendeBff_ReturnsRepositoryWithCommit()
    {
        if (!LiveTestsEnabled)
            return;

        var nuspecHttp = new HttpClient();
        nuspecHttp.DefaultRequestHeaders.Add("User-Agent", "dotnet-source-resolver/live-test");
        var nuspec = new NuSpecRepository(nuspecHttp, NullLogger<NuSpecRepository>.Instance);

        var meta = await nuspec.GetRepositoryMetadataAsync("Duende.BFF", "3.1.0", default);

        Assert.NotNull(meta);
        Assert.Equal("https://github.com/DuendeSoftware/products", meta.Url);
        Assert.Equal("0ca420dd34e43d6189d33fb27f8a543963050cab", meta.Commit);
        Assert.Equal("git", meta.Type);
    }

    [Fact]
    public async Task NuSpecRepository_NewtonsoftJson_ReturnsGitHubRepo()
    {
        if (!LiveTestsEnabled)
            return;

        var nuspecHttp = new HttpClient();
        nuspecHttp.DefaultRequestHeaders.Add("User-Agent", "dotnet-source-resolver/live-test");
        var nuspec = new NuSpecRepository(nuspecHttp, NullLogger<NuSpecRepository>.Instance);

        var meta = await nuspec.GetRepositoryMetadataAsync("Newtonsoft.Json", "13.0.3", default);

        Assert.NotNull(meta);
        Assert.Contains("github.com", meta.Url, StringComparison.OrdinalIgnoreCase);
    }

    // -------------------------------------------------------------------------
    // Full NuGet adapter pipeline
    // -------------------------------------------------------------------------

    [Fact]
    public async Task NuGetAdapter_DuendeBff_DefaultUserService_Resolves()
    {
        if (!LiveTestsEnabled)
            return;

        var adapter = BuildLiveNuGetAdapter();
        var request = new SymbolRequest(
            Symbol: "Duende.BFF.DefaultUserService",
            PackageId: "Duende.BFF",
            PackageVersion: "3.1.0",
            IncludeSnippets: true
        );

        var result = await adapter.TryResolveAsync(request, default);

        Assert.NotNull(result);
        Assert.True(result.Resolved);
        Assert.Equal(ResolutionKind.NuGet, result.ResolutionKind);
        Assert.NotEmpty(result.Sources);
        Assert.Contains("github.com", result.Sources[0].Url, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NuGetAdapter_NewtonsoftJson_JsonConvert_ResolvesToFileLevel()
    {
        if (!LiveTestsEnabled)
            return;

        var adapter = BuildLiveNuGetAdapter();
        var request = new SymbolRequest(
            Symbol: "Newtonsoft.Json.JsonConvert",
            PackageId: "Newtonsoft.Json",
            PackageVersion: "13.0.3",
            IncludeSnippets: true
        );

        var result = await adapter.TryResolveAsync(request, default);

        Assert.NotNull(result);
        Assert.True(result.Resolved);
        Assert.Equal(ResolutionKind.NuGet, result.ResolutionKind);
        Assert.NotEmpty(result.Sources);
        Assert.NotEmpty(result.Sources[0].Path);
        Assert.Contains("JsonConvert", result.Sources[0].Path);
        Assert.Single(result.Snippets);
    }

    [Fact]
    public async Task FullResolver_DuendeBff_DefaultUserService_ResolvedByNuGetAdapter()
    {
        if (!LiveTestsEnabled)
            return;

        var resolver = BuildLiveResolverWithNuGet();
        var request = new SymbolRequest(
            Symbol: "Duende.BFF.DefaultUserService",
            PackageId: "Duende.BFF",
            PackageVersion: "3.1.0",
            IncludeSnippets: true
        );

        var result = await resolver.ResolveAsync(request, default);

        Assert.True(
            result.Resolved,
            $"Expected resolved. Diagnostics: {string.Join("; ", result.Diagnostics)}"
        );
        Assert.Equal(ResolutionKind.NuGet, result.ResolutionKind);
        Assert.NotEmpty(result.Sources);
        // Should have a file-level location (High confidence), or at worst repo root (Medium)
        Assert.True(
            result.Confidence == ResolutionConfidence.High
                || result.Confidence == ResolutionConfidence.Medium
        );
    }
}

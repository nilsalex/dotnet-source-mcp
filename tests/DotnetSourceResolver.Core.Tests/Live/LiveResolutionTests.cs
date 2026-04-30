using DotnetSourceResolver.Core.Models;
using DotnetSourceResolver.Core.Resolution;
using DotnetSourceResolver.Core.Sources;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotnetSourceResolver.Core.Tests.Live;

/// <summary>
/// Live integration tests that hit real network resources.
/// Only run when the environment variable RESOLVER_RUN_LIVE_TESTS=true.
/// Normal CI: dotnet test --filter "Category!=Live"
/// Enable:    RESOLVER_RUN_LIVE_TESTS=true dotnet test
/// </summary>
[Trait("Category", "Live")]
public class LiveResolutionTests
{
    private static bool LiveTestsEnabled =>
        string.Equals(
            Environment.GetEnvironmentVariable("RESOLVER_RUN_LIVE_TESTS"),
            "true",
            StringComparison.OrdinalIgnoreCase
        );

    private static DotNetSourceResolver BuildLiveResolver()
    {
        var githubToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN");

        var githubHttp = new HttpClient();
        if (!string.IsNullOrEmpty(githubToken))
            githubHttp.DefaultRequestHeaders.Add("Authorization", $"token {githubToken}");
        githubHttp.DefaultRequestHeaders.Add("User-Agent", "dotnet-source-resolver/live-test");
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

        return new DotNetSourceResolver(
            [sourceDotNet, docs],
            NullLogger<DotNetSourceResolver>.Instance
        );
    }

    [Fact]
    public async Task Dictionary_ResolvesViaSourceDotNet()
    {
        if (!LiveTestsEnabled)
            return; // Skip when live tests not enabled

        var resolver = BuildLiveResolver();
        var result = await resolver.ResolveAsync(
            new SymbolRequest("System.Collections.Generic.Dictionary")
        );

        Assert.True(
            result.Resolved,
            $"Expected resolved. Diagnostics: {string.Join("; ", result.Diagnostics)}"
        );
        Assert.NotEmpty(result.Sources);
        Assert.NotEmpty(result.Sources[0].Url);
    }

    [Fact]
    public async Task StringBuilder_ResolvesViaSourceDotNetOrDocs()
    {
        if (!LiveTestsEnabled)
            return;

        var resolver = BuildLiveResolver();
        var result = await resolver.ResolveAsync(new SymbolRequest("System.Text.StringBuilder"));

        Assert.True(
            result.Resolved,
            $"Expected resolved. Diagnostics: {string.Join("; ", result.Diagnostics)}"
        );
        Assert.NotEmpty(result.Sources);
    }

    [Fact]
    public async Task DocsAdapter_StringBuilder_ResolvesDirectly()
    {
        if (!LiveTestsEnabled)
            return;

        var githubToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        var githubHttp = new HttpClient();
        if (!string.IsNullOrEmpty(githubToken))
            githubHttp.DefaultRequestHeaders.Add("Authorization", $"token {githubToken}");
        githubHttp.DefaultRequestHeaders.Add("User-Agent", "dotnet-source-resolver/live-test");
        var github = new GitHubAdapter(githubHttp, NullLogger<GitHubAdapter>.Instance);

        var docsHttp = new HttpClient();
        docsHttp.DefaultRequestHeaders.Add("User-Agent", "dotnet-source-resolver/live-test");
        var adapter = new DocsSourceLinkAdapter(
            docsHttp,
            github,
            NullLogger<DocsSourceLinkAdapter>.Instance
        );

        var result = await adapter.TryResolveAsync(
            new SymbolRequest("System.Text.StringBuilder"),
            CancellationToken.None
        );

        Assert.NotNull(result);
        Assert.True(result.Resolved);
        Assert.NotEmpty(result.Sources[0].Commit);
    }
}

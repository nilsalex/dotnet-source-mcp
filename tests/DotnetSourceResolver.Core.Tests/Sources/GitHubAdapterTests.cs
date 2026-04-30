using System.Net;
using DotnetSourceResolver.Core.Models;
using DotnetSourceResolver.Core.Sources;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using Xunit;

namespace DotnetSourceResolver.Core.Tests.Sources;

public class GitHubAdapterTests
{
    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private const string RawUrl =
        "https://raw.githubusercontent.com/dotnet/runtime/abc123/src/Foo/Bar.cs";

    private const string SampleSource =
        "line1\nline2\nline3\nline4\nline5\nline6\nline7\nline8\nline9\nline10";

    private static GitHubAdapter BuildAdapter(
        HttpResponseMessage response,
        string? githubToken = null
    )
    {
        var handler = new Mock<HttpMessageHandler>();
        handler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(response);

        var httpClient = new HttpClient(handler.Object);
        if (githubToken is not null)
            httpClient.DefaultRequestHeaders.Add("Authorization", $"token {githubToken}");

        return new GitHubAdapter(httpClient, NullLogger<GitHubAdapter>.Instance);
    }

    // -------------------------------------------------------------------------
    // ExtractSnippet static helper
    // -------------------------------------------------------------------------

    [Fact]
    public void ExtractSnippet_ReturnsRequestedRange()
    {
        var snippet = GitHubAdapter.ExtractSnippet(RawUrl, SampleSource, 2, 4);
        Assert.Equal(2, snippet.StartLine);
        Assert.Equal(4, snippet.EndLine);
        Assert.Equal("line2\nline3\nline4", snippet.Code);
    }

    [Fact]
    public void ExtractSnippet_ClampsEndBeyondFileLength()
    {
        var snippet = GitHubAdapter.ExtractSnippet(RawUrl, SampleSource, 8, 999);
        Assert.Equal(10, snippet.EndLine);
    }

    [Fact]
    public void ExtractSnippet_ClampsStartBelowOne()
    {
        var snippet = GitHubAdapter.ExtractSnippet(RawUrl, SampleSource, -5, 3);
        Assert.Equal(1, snippet.StartLine);
    }

    [Fact]
    public void ExtractSnippet_ExtractsPathFromRawUrl()
    {
        var snippet = GitHubAdapter.ExtractSnippet(RawUrl, SampleSource, 1, 2);
        Assert.Equal("src/Foo/Bar.cs", snippet.Path);
    }

    // -------------------------------------------------------------------------
    // BuildPermalink static helper
    // -------------------------------------------------------------------------

    [Fact]
    public void BuildPermalink_IncludesCommitAndLineRange()
    {
        var url = GitHubAdapter.BuildPermalink(
            "https://github.com/dotnet/runtime",
            "abc123",
            "src/Foo/Bar.cs",
            10,
            20
        );
        Assert.Equal("https://github.com/dotnet/runtime/blob/abc123/src/Foo/Bar.cs#L10-L20", url);
    }

    [Fact]
    public void BuildPermalink_UsesHEADWhenCommitEmpty()
    {
        var url = GitHubAdapter.BuildPermalink(
            "https://github.com/dotnet/runtime",
            "",
            "src/Foo/Bar.cs",
            1,
            5
        );
        Assert.Contains("/blob/HEAD/", url);
    }

    // -------------------------------------------------------------------------
    // FetchSnippetAsync
    // -------------------------------------------------------------------------

    [Fact]
    public async Task FetchSnippetAsync_HappyPath_ReturnsSnippet()
    {
        var adapter = BuildAdapter(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(SampleSource) }
        );

        var snippet = await adapter.FetchSnippetAsync(RawUrl, 1, 3, CancellationToken.None);

        Assert.NotNull(snippet);
        Assert.Equal(1, snippet.StartLine);
        Assert.Equal(3, snippet.EndLine);
    }

    [Fact]
    public async Task FetchSnippetAsync_404_ReturnsNull()
    {
        var adapter = BuildAdapter(new HttpResponseMessage(HttpStatusCode.NotFound));

        var snippet = await adapter.FetchSnippetAsync(RawUrl, 1, 10, CancellationToken.None);

        Assert.Null(snippet);
    }

    [Fact]
    public async Task FetchSnippetAsync_401_ThrowsHttpRequestException()
    {
        var adapter = BuildAdapter(new HttpResponseMessage(HttpStatusCode.Unauthorized));

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            adapter.FetchSnippetAsync(RawUrl, 1, 10, CancellationToken.None)
        );
    }

    [Fact]
    public async Task FetchSnippetAsync_403_ThrowsHttpRequestException()
    {
        var adapter = BuildAdapter(new HttpResponseMessage(HttpStatusCode.Forbidden));

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            adapter.FetchSnippetAsync(RawUrl, 1, 10, CancellationToken.None)
        );
    }

    // -------------------------------------------------------------------------
    // TryResolveAsync (ISourceAdapter)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task TryResolveAsync_WithoutGitHubRequest_ReturnsNull()
    {
        var adapter = BuildAdapter(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(SampleSource) }
        );

        var result = await adapter.TryResolveAsync(
            new SymbolRequest("System.String"),
            CancellationToken.None
        );

        Assert.Null(result);
    }

    [Fact]
    public async Task TryResolveAsync_WithGitHubRequest_ReturnsResult()
    {
        var adapter = BuildAdapter(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(SampleSource) }
        );

        var ghReq = new GitHubRequest(
            Repository: "https://github.com/dotnet/runtime",
            Commit: "abc123",
            Path: "src/Foo/Bar.cs",
            RawUrl: RawUrl,
            StartLine: 1,
            EndLine: 5
        );

        var request = new GitHubSymbolRequest("System.String", ghReq);
        var result = await adapter.TryResolveAsync(request, CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result.Resolved);
        Assert.Equal(ResolutionConfidence.High, result.Confidence);
        Assert.Single(result.Sources);
        Assert.Single(result.Snippets);
    }

    [Fact]
    public async Task TryResolveAsync_WithEmptyCommit_ConfidenceMedium()
    {
        var adapter = BuildAdapter(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(SampleSource) }
        );

        var ghReq = new GitHubRequest(
            Repository: "https://github.com/dotnet/runtime",
            Commit: "",
            Path: "src/Foo/Bar.cs",
            RawUrl: RawUrl,
            StartLine: 1,
            EndLine: 5
        );

        var request = new GitHubSymbolRequest("System.String", ghReq);
        var result = await adapter.TryResolveAsync(request, CancellationToken.None);

        Assert.Equal(ResolutionConfidence.Medium, result!.Confidence);
    }

    [Fact]
    public async Task TryResolveAsync_IncludeSnippetsFalse_SnippetsEmpty()
    {
        var adapter = BuildAdapter(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(SampleSource) }
        );

        var ghReq = new GitHubRequest(
            Repository: "https://github.com/dotnet/runtime",
            Commit: "abc123",
            Path: "src/Foo/Bar.cs",
            RawUrl: RawUrl,
            StartLine: 1,
            EndLine: 5
        );

        var request = new GitHubSymbolRequest("System.String", ghReq, IncludeSnippets: false);
        var result = await adapter.TryResolveAsync(request, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Empty(result.Snippets);
        Assert.Single(result.Sources);
    }
}

using System.Net;
using DotnetSourceResolver.Core.Models;
using DotnetSourceResolver.Core.Sources;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using Xunit;

namespace DotnetSourceResolver.Core.Tests.Sources;

public class DocsSourceLinkAdapterTests
{
    // -------------------------------------------------------------------------
    // NormaliseSymbol
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("System.String", "system.string")]
    [InlineData("System.Collections.Generic.Dictionary", "system.collections.generic.dictionary")]
    [InlineData("System.Collections.Generic.List`1", "system.collections.generic.list-1")]
    [InlineData("System.Text.StringBuilder", "system.text.stringbuilder")]
    public void NormaliseSymbol_ProducesExpectedSlug(string input, string expected)
    {
        Assert.Equal(expected, DocsSourceLinkAdapter.NormaliseSymbol(input));
    }

    // -------------------------------------------------------------------------
    // ExtractBestSourceLink
    // -------------------------------------------------------------------------

    private const string SingleRuntimeLink = """
        <dt>Source:</dt><dd><a href="https://github.com/dotnet/runtime/blob/d099f075e45d2aa6007a22b71b45a08758559f80/src/libraries/System.Private.CoreLib/src/System/Text/StringBuilder.cs" data-linktype="external">StringBuilder.cs</a></dd>
        """;

    private const string MultipleLinks = """
        <dt>Source:</dt><dd><a href="https://github.com/dotnet/dotnet/blob/a8b33e7593686eaee701cd124daaabff2311634f/src/runtime/src/libraries/System.Private.CoreLib/src/System/Text/StringBuilder.cs" data-linktype="external">StringBuilder.cs</a></dd>
        <dt>Source:</dt><dd><a href="https://github.com/dotnet/runtime/blob/d099f075e45d2aa6007a22b71b45a08758559f80/src/libraries/System.Private.CoreLib/src/System/Text/StringBuilder.cs" data-linktype="external">StringBuilder.cs</a></dd>
        """;

    private const string LinkWithLineNumbers = """
        <dt>Source:</dt><dd><a href="https://github.com/dotnet/runtime/blob/d099f075e45d2aa6007a22b71b45a08758559f80/src/libraries/System.Private.CoreLib/src/System/Text/StringBuilder.cs#L42-L80" data-linktype="external">StringBuilder.cs</a></dd>
        """;

    [Fact]
    public void ExtractBestSourceLink_SingleRuntimeLink_ParsesCorrectly()
    {
        var link = DocsSourceLinkAdapter.ExtractBestSourceLink(SingleRuntimeLink, null);
        Assert.NotNull(link);
        Assert.Equal("dotnet", link.Owner);
        Assert.Equal("runtime", link.Repo);
        Assert.Equal("d099f075e45d2aa6007a22b71b45a08758559f80", link.Commit);
        Assert.Equal(
            "src/libraries/System.Private.CoreLib/src/System/Text/StringBuilder.cs",
            link.Path
        );
    }

    [Fact]
    public void ExtractBestSourceLink_MultipleLinks_PrefersRuntime()
    {
        var link = DocsSourceLinkAdapter.ExtractBestSourceLink(MultipleLinks, null);
        Assert.NotNull(link);
        Assert.Equal("runtime", link!.Repo);
    }

    [Fact]
    public void ExtractBestSourceLink_WithLineNumbers_ParsesLineRange()
    {
        var link = DocsSourceLinkAdapter.ExtractBestSourceLink(LinkWithLineNumbers, null);
        Assert.NotNull(link);
        Assert.Equal(42, link!.StartLine);
        Assert.Equal(80, link.EndLine);
    }

    [Fact]
    public void ExtractBestSourceLink_NoLinks_ReturnsNull()
    {
        var link = DocsSourceLinkAdapter.ExtractBestSourceLink("<html>no source</html>", null);
        Assert.Null(link);
    }

    [Fact]
    public void ExtractBestSourceLink_404Page_ReturnsNull()
    {
        var link = DocsSourceLinkAdapter.ExtractBestSourceLink("The page was not found", null);
        Assert.Null(link);
    }

    // -------------------------------------------------------------------------
    // TryResolveAsync (mocked HTTP)
    // -------------------------------------------------------------------------

    private const string SampleFileContent =
        "line1\nline2\nline3\npublic class StringBuilder { }\nline5";

    private static DocsSourceLinkAdapter BuildAdapter(HttpStatusCode docsStatus, string docsContent)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(
                new HttpResponseMessage(docsStatus) { Content = new StringContent(docsContent) }
            );

        var httpClient = new HttpClient(handler.Object);

        var githubHandler = new Mock<HttpMessageHandler>();
        githubHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(SampleFileContent),
                }
            );
        var githubClient = new HttpClient(githubHandler.Object);
        var github = new GitHubAdapter(githubClient, NullLogger<GitHubAdapter>.Instance);

        return new DocsSourceLinkAdapter(
            httpClient,
            github,
            NullLogger<DocsSourceLinkAdapter>.Instance
        );
    }

    [Fact]
    public async Task TryResolveAsync_PageWith404_ReturnsNull()
    {
        var adapter = BuildAdapter(HttpStatusCode.NotFound, "");
        var result = await adapter.TryResolveAsync(
            new SymbolRequest("System.String"),
            CancellationToken.None
        );
        Assert.Null(result);
    }

    [Fact]
    public async Task TryResolveAsync_PageWithNoSourceLink_ReturnsNull()
    {
        var adapter = BuildAdapter(HttpStatusCode.OK, "<html>no source link</html>");
        var result = await adapter.TryResolveAsync(
            new SymbolRequest("System.String"),
            CancellationToken.None
        );
        Assert.Null(result);
    }

    [Fact]
    public async Task TryResolveAsync_PageWithSourceLink_ReturnsResult()
    {
        var adapter = BuildAdapter(HttpStatusCode.OK, SingleRuntimeLink);
        var result = await adapter.TryResolveAsync(
            new SymbolRequest("System.Text.StringBuilder"),
            CancellationToken.None
        );

        Assert.NotNull(result);
        Assert.True(result.Resolved);
        Assert.Equal(ResolutionKind.Docs, result.ResolutionKind);
        Assert.Equal(ResolutionConfidence.Medium, result.Confidence);
        Assert.Single(result.Sources);
        Assert.Equal("https://github.com/dotnet/runtime", result.Sources[0].Repository);
    }

    [Fact]
    public async Task TryResolveAsync_IncludeSnippetsFalse_SnippetsEmpty()
    {
        var adapter = BuildAdapter(HttpStatusCode.OK, SingleRuntimeLink);
        var result = await adapter.TryResolveAsync(
            new SymbolRequest("System.Text.StringBuilder", IncludeSnippets: false),
            CancellationToken.None
        );

        Assert.NotNull(result);
        Assert.Empty(result.Snippets);
    }

    [Fact]
    public async Task TryResolveAsync_LinkWithLineNumbers_UsesLineRange()
    {
        var adapter = BuildAdapter(HttpStatusCode.OK, LinkWithLineNumbers);
        var result = await adapter.TryResolveAsync(
            new SymbolRequest("System.Text.StringBuilder"),
            CancellationToken.None
        );

        Assert.NotNull(result);
        Assert.Equal(42, result.Sources[0].StartLine);
        Assert.Equal(80, result.Sources[0].EndLine);
    }
}

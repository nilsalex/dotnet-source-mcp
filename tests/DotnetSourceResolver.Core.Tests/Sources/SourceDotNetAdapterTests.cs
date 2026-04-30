using System.Net;
using DotnetSourceResolver.Core.Models;
using DotnetSourceResolver.Core.Sources;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using Xunit;

namespace DotnetSourceResolver.Core.Tests.Sources;

public class SourceDotNetAdapterTests
{
    // -------------------------------------------------------------------------
    // HTML builder helper — mimics the real source.dot.net result block structure
    // -------------------------------------------------------------------------

    private static string ResultBlock(
        string project,
        string hash,
        string kind,
        string description
    ) =>
        $"""
            <a href="/{project}/A.html#{hash}" target="s"><div class="resultItem"><div class="resultLine">
            <div class="resultKind">{kind}</div></div>
            <div class="resultDescription">{description}</div>
            </div></a>
            """;

    // -------------------------------------------------------------------------
    // ScoreResult
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(
        "System.Collections.Generic.Dictionary",
        "class",
        "System.Collections.Generic.Dictionary",
        13
    )] // exact + class(3) = 10+3
    [InlineData(
        "System.Collections.Generic.Dictionary<TKey, TValue>",
        "class",
        "System.Collections.Generic.Dictionary",
        13
    )] // generic stripped
    [InlineData(
        "System.Collections.Generic.Dictionary",
        "property",
        "System.Collections.Generic.Dictionary",
        11
    )] // exact + property(1) = 10+1
    [InlineData(
        "System.Collections.Generic.Dictionary.AlternateLookup",
        "class",
        "System.Collections.Generic.Dictionary",
        9
    )] // starts-with + class = 6+3
    [InlineData(
        "System.Collections.Generic.DictionaryExtensions",
        "class",
        "System.Collections.Generic.Dictionary",
        0
    )] // no match (Extensions ≠ exact suffix)
    [InlineData(
        "System.Collections.Generic.AlternateLookup.Dictionary",
        "property",
        "System.Collections.Generic.Dictionary",
        1
    )] // ends-with + property = 0+1
    [InlineData(
        "System.Collections.Generic.AlternateLookup.Dictionary",
        "class",
        "System.Collections.Generic.Dictionary",
        3
    )] // ends-with + class = 0+3
    public void ScoreResult_ReturnsExpectedScore(
        string description,
        string kind,
        string symbol,
        int expectedScore
    )
    {
        var score = SourceDotNetAdapter.ScoreResult(description, kind, symbol);
        Assert.Equal(expectedScore, score);
    }

    // -------------------------------------------------------------------------
    // PickBestResult
    // -------------------------------------------------------------------------

    [Fact]
    public void PickBestResult_EmptyHtml_ReturnsNulls()
    {
        var (project, hash) = SourceDotNetAdapter.PickBestResult(
            "<div>No results found</div>",
            "X"
        );
        Assert.Null(project);
        Assert.Null(hash);
    }

    [Fact]
    public void PickBestResult_SingleResult_ReturnsThatResult()
    {
        var html = ResultBlock(
            "System.Private.CoreLib",
            "d3599058f8d79be0",
            "class",
            "System.Collections.Generic.Dictionary"
        );
        var (project, hash) = SourceDotNetAdapter.PickBestResult(
            html,
            "System.Collections.Generic.Dictionary"
        );
        Assert.Equal("System.Private.CoreLib", project);
        Assert.Equal("d3599058f8d79be0", hash);
    }

    [Fact]
    public void PickBestResult_ClassPreferredOverPropertyWithSameName()
    {
        // Regression: "Dictionary" property inside AlternateLookup must lose to the class declaration.
        var html =
            ResultBlock(
                "System.Private.CoreLib",
                "fe4061361a1c71fc",
                "property",
                "System.Collections.Generic.Dictionary&lt;TKey, TValue&gt;.AlternateLookup&lt;TAlternateKey&gt;.Dictionary"
            )
            + ResultBlock(
                "System.Private.CoreLib",
                "d3599058f8d79be0",
                "class",
                "System.Collections.Generic.Dictionary&lt;TKey, TValue&gt;"
            );

        var (project, hash) = SourceDotNetAdapter.PickBestResult(
            html,
            "System.Collections.Generic.Dictionary"
        );
        Assert.Equal("System.Private.CoreLib", project);
        Assert.Equal("d3599058f8d79be0", hash);
    }

    [Fact]
    public void PickBestResult_ExactMatchPreferredOverPartialMatch()
    {
        var html =
            ResultBlock(
                "System.Collections",
                "aaaaaaaaaaaaaaa1",
                "class",
                "System.Collections.SomethingElse"
            )
            + ResultBlock(
                "System.Private.CoreLib",
                "d3599058f8d79be0",
                "class",
                "System.Collections.Generic.Dictionary"
            );
        var (project, hash) = SourceDotNetAdapter.PickBestResult(
            html,
            "System.Collections.Generic.Dictionary"
        );
        Assert.Equal("System.Private.CoreLib", project);
        Assert.Equal("d3599058f8d79be0", hash);
    }

    [Fact]
    public void PickBestResult_DictionaryExtensions_DoesNotMatchDictionary()
    {
        // "DictionaryExtensions" should not be treated as an exact match for "Dictionary"
        var html =
            ResultBlock(
                "System.Collections",
                "aaaaaaaaaaaaaaaa",
                "class",
                "System.Collections.Generic.DictionaryExtensions"
            )
            + ResultBlock(
                "System.Private.CoreLib",
                "d3599058f8d79be0",
                "class",
                "System.Collections.Generic.Dictionary"
            );
        var (project, hash) = SourceDotNetAdapter.PickBestResult(
            html,
            "System.Collections.Generic.Dictionary"
        );
        Assert.Equal("d3599058f8d79be0", hash);
    }

    // -------------------------------------------------------------------------
    // ResolveFilePathFromBucket
    // -------------------------------------------------------------------------

    [Fact]
    public void ResolveFilePathFromBucket_FindsMatchingFile()
    {
        // Simulate Ad.html content
        var bucketHtml = """
            var f = [
            "src/libraries/System.Private.CoreLib/src/System/Text/StringBuilder.cs",
            "src/libraries/System.Private.CoreLib/src/System/Collections/Generic/Dictionary.cs",
            ];
            var m = new Object();
            m["3599058"]=f[1];
            redirect(m, 8);
            """;

        // Symbol hash: d3599058f8d79be0 → key = hash[1..8] = "3599058"
        var path = SourceDotNetAdapter.ResolveFilePathFromBucket(bucketHtml, "d3599058f8d79be0");
        Assert.Equal(
            "src/libraries/System.Private.CoreLib/src/System/Collections/Generic/Dictionary.cs",
            path
        );
    }

    [Fact]
    public void ResolveFilePathFromBucket_NoMatch_ReturnsNull()
    {
        var bucketHtml = """
            var f = ["src/Foo/Bar.cs",];
            var m = new Object();
            m["1234567"]=f[0];
            redirect(m, 8);
            """;
        var path = SourceDotNetAdapter.ResolveFilePathFromBucket(bucketHtml, "d3599058f8d79be0");
        Assert.Null(path);
    }

    // -------------------------------------------------------------------------
    // ExtractGitHubInfo
    // -------------------------------------------------------------------------

    [Fact]
    public void ExtractGitHubInfo_ParsesRepoCommitPath()
    {
        var html = """
            <a href="https://github.com/dotnet/runtime/tree/03df3922283c2fe198085e027113d13a0cd9a053/src/libraries/System.Private.CoreLib/src/System/Collections/Generic/Dictionary.cs" target="_blank">Web&nbsp;Access</a>
            """;
        var info = SourceDotNetAdapter.ExtractGitHubInfo(html);
        Assert.NotNull(info);
        Assert.Equal("https://github.com/dotnet/runtime", info.Value.repo);
        Assert.Equal("03df3922283c2fe198085e027113d13a0cd9a053", info.Value.commit);
        Assert.Equal(
            "src/libraries/System.Private.CoreLib/src/System/Collections/Generic/Dictionary.cs",
            info.Value.path
        );
    }

    [Fact]
    public void ExtractGitHubInfo_NoGitHubLink_ReturnsNull()
    {
        var html = "<html><body>no github link</body></html>";
        var info = SourceDotNetAdapter.ExtractGitHubInfo(html);
        Assert.Null(info);
    }

    // -------------------------------------------------------------------------
    // ExtractSymbolLine
    // -------------------------------------------------------------------------

    [Fact]
    public void ExtractSymbolLine_FindsCorrectLine()
    {
        var html = """
            <pre id="code">
            line1
            line2
            <a id="d3599058f8d79be0" href="...">Symbol</a>
            line4
            </pre>
            """;
        var line = SourceDotNetAdapter.ExtractSymbolLine(html, "d3599058f8d79be0");
        Assert.Equal(4, line);
    }

    [Fact]
    public void ExtractSymbolLine_SymbolNotFound_ReturnsOne()
    {
        var html = "<pre id=\"code\">nothing here</pre>";
        var line = SourceDotNetAdapter.ExtractSymbolLine(html, "aaaaaaaaaaaaaaaa");
        Assert.Equal(1, line);
    }

    // -------------------------------------------------------------------------
    // TryResolveAsync (mocked HTTP)
    // -------------------------------------------------------------------------

    private const string SampleFileContent =
        "// line1\n// line2\npublic class Dictionary { }\n// line4\n// line5";

    private static SourceDotNetAdapter BuildAdapter((string url, string content)[] responses)
    {
        var handler = new Mock<HttpMessageHandler>();

        // Fallback: 404 (registered first so specific setups below take precedence)
        handler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.NotFound));

        foreach (var (url, content) in responses)
        {
            handler
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.Is<HttpRequestMessage>(r => r.RequestUri!.ToString() == url),
                    ItExpr.IsAny<CancellationToken>()
                )
                .ReturnsAsync(
                    new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(content),
                    }
                );
        }

        var httpClient = new HttpClient(handler.Object);

        // GitHubAdapter also needs its own HttpClient for file fetching
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

        return new SourceDotNetAdapter(
            httpClient,
            github,
            NullLogger<SourceDotNetAdapter>.Instance
        );
    }

    [Fact]
    public async Task TryResolveAsync_NoSearchResults_ReturnsNull()
    {
        var adapter = BuildAdapter([
            ("https://source.dot.net/api/symbols/?symbol=Unknown", "<div>No results found</div>"),
        ]);

        var result = await adapter.TryResolveAsync(
            new SymbolRequest("Unknown"),
            CancellationToken.None
        );
        Assert.Null(result);
    }

    [Fact]
    public async Task TryResolveAsync_HappyPath_ReturnsResult()
    {
        var searchHtml = ResultBlock(
            "System.Private.CoreLib",
            "d3599058f8d79be0",
            "class",
            "System.Collections.Generic.Dictionary"
        );
        var bucketHtml = """
            var f = [
            "src/libraries/System.Private.CoreLib/src/System/Collections/Generic/Dictionary.cs",
            ];
            var m = new Object();
            m["3599058"]=f[0];
            redirect(m, 8);
            """;
        var fileHtml = """
            <a href="https://github.com/dotnet/runtime/tree/03df3922283c2fe198085e027113d13a0cd9a053/src/libraries/System.Private.CoreLib/src/System/Collections/Generic/Dictionary.cs" target="_blank">Web Access</a>
            <pre id="code">
            // line1
            <a id="d3599058f8d79be0" class="t">Dictionary</a>
            </pre>
            """;

        var adapter = BuildAdapter([
            (
                "https://source.dot.net/api/symbols/?symbol=System.Collections.Generic.Dictionary",
                searchHtml
            ),
            ("https://source.dot.net/System.Private.CoreLib/Ad.html", bucketHtml),
            (
                "https://source.dot.net/System.Private.CoreLib/src/libraries/System.Private.CoreLib/src/System/Collections/Generic/Dictionary.cs.html",
                fileHtml
            ),
        ]);

        var result = await adapter.TryResolveAsync(
            new SymbolRequest("System.Collections.Generic.Dictionary"),
            CancellationToken.None
        );

        Assert.NotNull(result);
        Assert.True(result.Resolved);
        Assert.Equal(ResolutionKind.SourceDotNet, result.ResolutionKind);
        Assert.Equal(ResolutionConfidence.High, result.Confidence);
        Assert.Single(result.Sources);
        Assert.Contains("03df3922283c2fe198085e027113d13a0cd9a053", result.Sources[0].Commit);
    }
}

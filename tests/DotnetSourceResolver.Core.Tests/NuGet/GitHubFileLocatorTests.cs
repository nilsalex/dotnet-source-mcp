using System.Net;
using DotnetSourceResolver.Core.NuGet;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;

namespace DotnetSourceResolver.Core.Tests.NuGet;

public class GitHubFileLocatorTests
{
    // -------------------------------------------------------------------------
    // FindInTreeJson — static helper
    // -------------------------------------------------------------------------

    private const string SampleTreeJson = """
        {
          "sha": "abc123",
          "tree": [
            { "path": "bff/src/Bff/EndpointServices/User/DefaultUserService.cs", "type": "blob" },
            { "path": "bff/src/Bff/OtherFile.cs", "type": "blob" },
            { "path": "bff/src", "type": "tree" }
          ]
        }
        """;

    [Fact]
    public void FindInTreeJson_MatchingFilename_ReturnsPath()
    {
        var result = GitHubFileLocator.FindInTreeJson(SampleTreeJson, "DefaultUserService.cs");

        Assert.Equal("bff/src/Bff/EndpointServices/User/DefaultUserService.cs", result);
    }

    [Fact]
    public void FindInTreeJson_CaseInsensitive_ReturnsPath()
    {
        var result = GitHubFileLocator.FindInTreeJson(SampleTreeJson, "defaultuserservice.cs");

        Assert.Equal("bff/src/Bff/EndpointServices/User/DefaultUserService.cs", result);
    }

    [Fact]
    public void FindInTreeJson_NoMatch_ReturnsNull()
    {
        var result = GitHubFileLocator.FindInTreeJson(SampleTreeJson, "NonExistent.cs");

        Assert.Null(result);
    }

    [Fact]
    public void FindInTreeJson_SkipsTreeNodes_ReturnsOnlyBlobs()
    {
        // "bff/src" is a tree, not a blob — should not be returned
        var result = GitHubFileLocator.FindInTreeJson(SampleTreeJson, "src");

        Assert.Null(result);
    }

    [Fact]
    public void FindInTreeJson_EmptyTree_ReturnsNull()
    {
        var result = GitHubFileLocator.FindInTreeJson("""{"tree":[]}""", "Foo.cs");

        Assert.Null(result);
    }

    [Fact]
    public void FindInTreeJson_InvalidJson_ReturnsNull()
    {
        var result = GitHubFileLocator.FindInTreeJson("not json <<>>", "Foo.cs");

        Assert.Null(result);
    }

    [Fact]
    public void FindInTreeJson_MissingTreeProperty_ReturnsNull()
    {
        var result = GitHubFileLocator.FindInTreeJson("""{"sha":"abc"}""", "Foo.cs");

        Assert.Null(result);
    }

    // -------------------------------------------------------------------------
    // FindFileAsync — mocked HTTP
    // -------------------------------------------------------------------------

    private static GitHubFileLocator BuildLocator(HttpResponseMessage response)
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

        return new GitHubFileLocator(
            new HttpClient(handler.Object),
            NullLogger<GitHubFileLocator>.Instance
        );
    }

    [Fact]
    public async Task FindFileAsync_ValidResponse_ReturnsPath()
    {
        var locator = BuildLocator(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SampleTreeJson),
            }
        );

        var result = await locator.FindFileAsync(
            "DuendeSoftware",
            "products",
            "abc123",
            "DefaultUserService.cs",
            default
        );

        Assert.Equal("bff/src/Bff/EndpointServices/User/DefaultUserService.cs", result);
    }

    [Fact]
    public async Task FindFileAsync_HttpError_ReturnsNull()
    {
        var locator = BuildLocator(new HttpResponseMessage(HttpStatusCode.NotFound));

        var result = await locator.FindFileAsync("org", "repo", "sha", "Foo.cs", default);

        Assert.Null(result);
    }

    [Fact]
    public async Task FindFileAsync_UsesCorrectUrl()
    {
        HttpRequestMessage? captured = null;
        var handler = new Mock<HttpMessageHandler>();
        handler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => captured = req)
            .ReturnsAsync(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"tree":[]}"""),
                }
            );

        var locator = new GitHubFileLocator(
            new HttpClient(handler.Object),
            NullLogger<GitHubFileLocator>.Instance
        );

        await locator.FindFileAsync("myowner", "myrepo", "mysha", "Foo.cs", default);

        Assert.NotNull(captured);
        Assert.Equal(
            "https://api.github.com/repos/myowner/myrepo/git/trees/mysha?recursive=1",
            captured.RequestUri?.ToString()
        );
    }

    // -------------------------------------------------------------------------
    // PascalCase prefix matching
    // -------------------------------------------------------------------------

    [Fact]
    public void FindInTreeJson_PascalCasePrefixMatch_FindsFileByPrefix()
    {
        // "BffManagementExtensions.cs" → prefix "BffManagement" (10 chars, > 6)
        // matches "BffManagementEndpointRouteBuilderExtensions.cs" in the tree
        const string treeJson = """
            {
              "tree": [
                { "path": "bff/src/Bff/Configuration/BffManagementEndpointRouteBuilderExtensions.cs", "type": "blob" },
                { "path": "bff/test/BffTests.cs", "type": "blob" }
              ]
            }
            """;

        var result = GitHubFileLocator.FindInTreeJson(treeJson, "BffManagementExtensions.cs");

        Assert.NotNull(result);
        Assert.Equal(
            "bff/src/Bff/Configuration/BffManagementEndpointRouteBuilderExtensions.cs",
            result
        );
    }

    [Fact]
    public void FindInTreeJson_PascalCasePrefixes_LongestFirst()
    {
        var prefixes = GitHubFileLocator.PascalCasePrefixes("BffManagementEndpoints").ToList();

        Assert.Equal("BffManagementEndpoints", prefixes[0]); // full name first
        Assert.Contains("BffManagement", prefixes);
        Assert.Contains("Bff", prefixes);
        // Longer prefixes come before shorter ones
        var mgmtIdx = prefixes.IndexOf("BffManagement");
        var bffIdx = prefixes.IndexOf("Bff");
        Assert.True(mgmtIdx < bffIdx);
    }

    [Fact]
    public void FindInTreeJson_PrefersNonTestFiles()
    {
        const string treeJson = """
            {
              "tree": [
                { "path": "src/Bff/BffBuilder.cs", "type": "blob" },
                { "path": "test/BffBuilderTests.cs", "type": "blob" }
              ]
            }
            """;

        // BffBuilderExtensions.cs → prefix "BffBuilder" (10 chars) matches both
        var result = GitHubFileLocator.FindInTreeJson(treeJson, "BffBuilderExtensions.cs");

        Assert.Equal("src/Bff/BffBuilder.cs", result);
    }
}

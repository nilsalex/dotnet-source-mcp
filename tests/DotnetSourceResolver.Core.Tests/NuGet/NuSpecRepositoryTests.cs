using System.Net;
using DotnetSourceResolver.Core.NuGet;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;

namespace DotnetSourceResolver.Core.Tests.NuGet;

public class NuSpecRepositoryTests
{
    // -------------------------------------------------------------------------
    // ParseNuSpec — static helper
    // -------------------------------------------------------------------------

    private const string ValidNuSpecWithRepository = """
        <?xml version="1.0" encoding="utf-8"?>
        <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
          <metadata>
            <id>Duende.BFF</id>
            <version>3.1.0</version>
            <repository type="git"
                        url="https://github.com/DuendeSoftware/products"
                        branch="refs/heads/releases/bff/3.1.x"
                        commit="0ca420dd34e43d6189d33fb27f8a543963050cab" />
          </metadata>
        </package>
        """;

    private const string NuSpecWithoutRepository = """
        <?xml version="1.0" encoding="utf-8"?>
        <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
          <metadata>
            <id>OldPackage</id>
            <version>1.0.0</version>
            <projectUrl>https://github.com/some/repo</projectUrl>
          </metadata>
        </package>
        """;

    private const string NuSpecWithEmptyRepositoryUrl = """
        <?xml version="1.0" encoding="utf-8"?>
        <package>
          <metadata>
            <repository type="git" url="" commit="abc123" />
          </metadata>
        </package>
        """;

    private const string NuSpecWithNoUrlAnywhere = """
        <?xml version="1.0" encoding="utf-8"?>
        <package>
          <metadata>
            <id>MinimalPackage</id>
          </metadata>
        </package>
        """;

    [Fact]
    public void ParseNuSpec_ValidXml_ReturnsMetadata()
    {
        var result = NuSpecRepository.ParseNuSpec(ValidNuSpecWithRepository);

        Assert.NotNull(result);
        Assert.Equal("https://github.com/DuendeSoftware/products", result.Url);
        Assert.Equal("0ca420dd34e43d6189d33fb27f8a543963050cab", result.Commit);
        Assert.Equal("refs/heads/releases/bff/3.1.x", result.Branch);
        Assert.Equal("git", result.Type);
    }

    [Fact]
    public void ParseNuSpec_MissingRepository_FallsBackToProjectUrl()
    {
        var result = NuSpecRepository.ParseNuSpec(NuSpecWithoutRepository);

        Assert.NotNull(result);
        Assert.Equal("https://github.com/some/repo", result.Url);
        Assert.Null(result.Commit);
        Assert.Equal("git", result.Type);
    }

    [Fact]
    public void ParseNuSpec_EmptyRepositoryUrl_FallsBackToProjectUrl()
    {
        var result = NuSpecRepository.ParseNuSpec(NuSpecWithEmptyRepositoryUrl);

        // <repository url=""> treated as missing → projectUrl fallback → nothing → null
        Assert.Null(result);
    }

    [Fact]
    public void ParseNuSpec_NoUrlAnywhere_ReturnsNull()
    {
        var result = NuSpecRepository.ParseNuSpec(NuSpecWithNoUrlAnywhere);

        Assert.Null(result);
    }

    [Fact]
    public void ParseNuSpec_InvalidXml_ReturnsNull()
    {
        var result = NuSpecRepository.ParseNuSpec("this is not xml <<>>");

        Assert.Null(result);
    }

    // -------------------------------------------------------------------------
    // ExtractRepoFromProjectUrl — static helper
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(
        "https://github.com/DuendeSoftware/products",
        "https://github.com/DuendeSoftware/products"
    )]
    [InlineData("https://github.com/dotnet/runtime/tree/main", "https://github.com/dotnet/runtime")]
    [InlineData("https://github.com/owner/repo/issues", "https://github.com/owner/repo")]
    public void ExtractRepoFromProjectUrl_GitHubUrl_ReturnsRepoRoot(string input, string expected)
    {
        var result = NuSpecRepository.ExtractRepoFromProjectUrl(input);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("https://www.newtonsoft.com/json")]
    [InlineData("https://serilog.net/")]
    [InlineData("https://dev.azure.com/org/project")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-url")]
    public void ExtractRepoFromProjectUrl_NonGitHub_ReturnsNull(string? input)
    {
        var result = NuSpecRepository.ExtractRepoFromProjectUrl(input);

        Assert.Null(result);
    }

    // -------------------------------------------------------------------------
    // GetRepositoryMetadataAsync — mocked HTTP
    // -------------------------------------------------------------------------

    private static NuSpecRepository BuildService(HttpResponseMessage response)
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

        return new NuSpecRepository(
            new HttpClient(handler.Object),
            NullLogger<NuSpecRepository>.Instance
        );
    }

    [Fact]
    public async Task GetRepositoryMetadataAsync_ValidPackage_ReturnsMetadata()
    {
        var svc = BuildService(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ValidNuSpecWithRepository),
            }
        );

        var result = await svc.GetRepositoryMetadataAsync("Duende.BFF", "3.1.0", default);

        Assert.NotNull(result);
        Assert.Equal("https://github.com/DuendeSoftware/products", result.Url);
        Assert.Equal("0ca420dd34e43d6189d33fb27f8a543963050cab", result.Commit);
    }

    [Fact]
    public async Task GetRepositoryMetadataAsync_404_ReturnsNull()
    {
        var svc = BuildService(new HttpResponseMessage(HttpStatusCode.NotFound));

        var result = await svc.GetRepositoryMetadataAsync("Unknown.Package", "9.9.9", default);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetRepositoryMetadataAsync_HttpError_ReturnsNull()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ThrowsAsync(new HttpRequestException("Network error"));

        var svc = new NuSpecRepository(
            new HttpClient(handler.Object),
            NullLogger<NuSpecRepository>.Instance
        );

        var result = await svc.GetRepositoryMetadataAsync("Duende.BFF", "3.1.0", default);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetRepositoryMetadataAsync_UsesCorrectUrl()
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
                    Content = new StringContent(ValidNuSpecWithRepository),
                }
            );

        var svc = new NuSpecRepository(
            new HttpClient(handler.Object),
            NullLogger<NuSpecRepository>.Instance
        );

        await svc.GetRepositoryMetadataAsync("Duende.BFF", "3.1.0", default);

        Assert.NotNull(captured);
        Assert.Equal(
            "https://api.nuget.org/v3-flatcontainer/duende.bff/3.1.0/duende.bff.nuspec",
            captured.RequestUri?.ToString()
        );
    }
}

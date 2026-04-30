using System.IO.Pipelines;
using System.Text.Json;
using DotnetSourceResolver.Core.Models;
using DotnetSourceResolver.Core.Resolution;
using DotnetSourceResolver.McpServer.Prompts;
using DotnetSourceResolver.McpServer.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Moq;
using Xunit;
using McpServerType = ModelContextProtocol.Server.McpServer;

namespace DotnetSourceResolver.McpServer.Tests;

public class McpToolsTests : IAsyncDisposable
{
    private readonly Pipe _clientToServer = new();
    private readonly Pipe _serverToClient = new();
    private readonly ServiceProvider _services;
    private readonly McpServerType _server;
    private McpClient? _client;

    public McpToolsTests()
    {
        var mockResolver = BuildMockResolver();

        var sc = new ServiceCollection();
        sc.AddSingleton(mockResolver);
        sc.AddLogging(b => b.SetMinimumLevel(LogLevel.None));
        sc.AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation
                {
                    Name = "dotnet-source-resolver",
                    Version = "0.0.0-test",
                };
                options.ServerInstructions =
                    "Resolves .NET symbols to exact source locations (BCL, ASP.NET Core, Microsoft.Extensions.*). "
                    + "Use resolve_dotnet_source to get a GitHub permalink + code snippet for any type or member. "
                    + "Use explain_dotnet_implementation to answer questions about how something is internally implemented. "
                    + "Prefer these tools over reflection helpers or manual GitHub browsing.";
            })
            .WithStreamServerTransport(
                _clientToServer.Reader.AsStream(),
                _serverToClient.Writer.AsStream()
            )
            .WithToolsFromAssembly(typeof(DotnetTools).Assembly)
            .WithPromptsFromAssembly(typeof(DotnetPrompts).Assembly);

        _services = sc.BuildServiceProvider();
        _server = _services.GetRequiredService<McpServerType>();
    }

    private static DotNetSourceResolver BuildMockResolver()
    {
        var successResult = new SourceResult(
            Resolved: true,
            CanonicalSymbol: "System.String",
            ResolutionKind: ResolutionKind.SourceDotNet,
            Confidence: ResolutionConfidence.High,
            Sources:
            [
                new SourceEntry(
                    Kind: "source.dot.net",
                    Repository: "https://github.com/dotnet/runtime",
                    Commit: "abc123",
                    Path: "src/libraries/System.Private.CoreLib/src/System/String.cs",
                    Url: "https://github.com/dotnet/runtime/blob/abc123/src/.../String.cs#L1-L80",
                    StartLine: 1,
                    EndLine: 80
                ),
            ],
            Snippets:
            [
                new SnippetEntry(
                    Path: "src/libraries/System.Private.CoreLib/src/System/String.cs",
                    StartLine: 1,
                    EndLine: 5,
                    Code: "public sealed class String { }"
                ),
            ],
            Diagnostics: [],
            ResolverVersion: "0.0.0-test"
        );

        var adapter = new Mock<ISourceAdapter>();
        adapter
            .Setup(a => a.TryResolveAsync(It.IsAny<SymbolRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(successResult);

        return new DotNetSourceResolver(
            [adapter.Object],
            NullLogger<DotNetSourceResolver>.Instance
        );
    }

    private async Task<McpClient> GetClientAsync()
    {
        if (_client is null)
        {
            _ = _server.RunAsync();
            _client = await McpClient.CreateAsync(
                new StreamClientTransport(
                    serverInput: _clientToServer.Writer.AsStream(),
                    serverOutput: _serverToClient.Reader.AsStream()
                )
            );
        }
        return _client;
    }

    private static string GetTextContent(CallToolResult result)
    {
        var block = result.Content.OfType<TextContentBlock>().FirstOrDefault();
        return block?.Text ?? throw new InvalidOperationException("No text content in result");
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
            await _client.DisposeAsync();
        await _server.DisposeAsync();
        await _services.DisposeAsync();
    }

    // -------------------------------------------------------------------------
    // Tool registration
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ToolsList_ContainsResolveTool()
    {
        var client = await GetClientAsync();
        var tools = await client.ListToolsAsync();

        var names = tools.Select(t => t.Name).ToList();
        Assert.Contains("resolve_dotnet_source", names);
    }

    [Fact]
    public async Task ToolsList_DoesNotContainExplainTool()
    {
        var client = await GetClientAsync();
        var tools = await client.ListToolsAsync();

        Assert.DoesNotContain(tools, t => t.Name == "explain_dotnet_implementation");
    }

    // -------------------------------------------------------------------------
    // resolve_dotnet_source
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ResolveDotnetSource_ValidInput_ReturnsResolvedResult()
    {
        var client = await GetClientAsync();
        var tools = await client.ListToolsAsync();
        var tool = tools.First(t => t.Name == "resolve_dotnet_source");

        var callResult = await tool.CallAsync(
            new Dictionary<string, object?> { ["symbol"] = "System.String" }
        );

        Assert.NotNull(callResult);
        Assert.True(callResult.IsError != true);

        var json = GetTextContent(callResult);
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("Resolved").GetBoolean());
        Assert.Equal("System.String", doc.RootElement.GetProperty("CanonicalSymbol").GetString());
    }

    [Fact]
    public async Task ResolveDotnetSource_WithAllOptionalParams_DoesNotThrow()
    {
        var client = await GetClientAsync();
        var tools = await client.ListToolsAsync();
        var tool = tools.First(t => t.Name == "resolve_dotnet_source");

        var callResult = await tool.CallAsync(
            new Dictionary<string, object?>
            {
                ["symbol"] = "System.String",
                ["packageId"] = "System.Private.CoreLib",
                ["packageVersion"] = "8.0.5",
                ["assemblyName"] = "System.Private.CoreLib",
                ["targetFramework"] = "net10.0",
                ["includeSnippets"] = true,
                ["maxSnippetLines"] = 40,
            }
        );

        Assert.True(callResult.IsError != true);
        var json = GetTextContent(callResult);
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("Resolved").GetBoolean());
    }

    [Fact]
    public async Task ResolveDotnetSource_ResultHasExpectedShape()
    {
        var client = await GetClientAsync();
        var tools = await client.ListToolsAsync();
        var tool = tools.First(t => t.Name == "resolve_dotnet_source");

        var callResult = await tool.CallAsync(
            new Dictionary<string, object?> { ["symbol"] = "System.String" }
        );

        var json = GetTextContent(callResult);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("Resolved", out _));
        Assert.True(root.TryGetProperty("CanonicalSymbol", out _));
        Assert.True(root.TryGetProperty("ResolutionKind", out _));
        Assert.True(root.TryGetProperty("Confidence", out _));
        Assert.True(root.TryGetProperty("Sources", out _));
        Assert.True(root.TryGetProperty("Snippets", out _));
        Assert.True(root.TryGetProperty("Diagnostics", out _));
        Assert.True(root.TryGetProperty("ResolverVersion", out _));
    }

    // -------------------------------------------------------------------------
    // ServerInstructions
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ServerInstructions_IsNonEmpty()
    {
        var client = await GetClientAsync();
        Assert.False(string.IsNullOrWhiteSpace(client.ServerInstructions));
    }

    [Fact]
    public async Task ServerInstructions_MentionsResolveTool()
    {
        var client = await GetClientAsync();
        Assert.Contains("resolve_dotnet_source", client.ServerInstructions);
    }

    [Fact]
    public async Task ServerInfo_HasCorrectName()
    {
        var client = await GetClientAsync();
        Assert.Equal("dotnet-source-resolver", client.ServerInfo.Name);
    }

    // -------------------------------------------------------------------------
    // Prompts
    // -------------------------------------------------------------------------

    [Fact]
    public async Task PromptsList_ContainsLookupPrompt()
    {
        var client = await GetClientAsync();
        var prompts = await client.ListPromptsAsync();

        Assert.Contains(prompts, p => p.Name == "lookup_dotnet_symbol");
    }

    [Fact]
    public async Task LookupPrompt_WithSymbolOnly_ReturnsMessage()
    {
        var client = await GetClientAsync();
        var result = await client.GetPromptAsync(
            "lookup_dotnet_symbol",
            new Dictionary<string, object?> { ["symbol"] = "System.String" }
        );

        Assert.NotNull(result);
        Assert.NotEmpty(result.Messages);
        var text = ((TextContentBlock)result.Messages[0].Content).Text;
        Assert.Contains("System.String", text);
    }

    [Fact]
    public async Task LookupPrompt_WithTargetFramework_IncludesTfmInMessage()
    {
        var client = await GetClientAsync();
        var result = await client.GetPromptAsync(
            "lookup_dotnet_symbol",
            new Dictionary<string, object?>
            {
                ["symbol"] = "System.String",
                ["targetFramework"] = "net10.0",
            }
        );

        var text = ((TextContentBlock)result.Messages[0].Content).Text;
        Assert.Contains("net10.0", text);
    }
}

using DotnetSourceResolver.Core.Resolution;
using DotnetSourceResolver.Core.Sources;
using DotnetSourceResolver.McpServer.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

// Redirect all logs to stderr so they don't pollute the stdio MCP stream on stdout
builder.Logging.AddConsole(opts => opts.LogToStandardErrorThreshold = LogLevel.Trace);

var logLevel = Enum.TryParse<LogLevel>(
    Environment.GetEnvironmentVariable("RESOLVER_LOG_LEVEL"),
    out var ll
)
    ? ll
    : LogLevel.Warning;
builder.Logging.SetMinimumLevel(logLevel);

// Configuration from environment
var githubToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
var maxSnippetLines = int.TryParse(
    Environment.GetEnvironmentVariable("RESOLVER_MAX_SNIPPET_LINES"),
    out var msl
)
    ? msl
    : 80;

// Register HttpClients
builder.Services.AddHttpClient<GitHubAdapter>(c =>
{
    c.DefaultRequestHeaders.Add("User-Agent", "dotnet-source-resolver/mcp");
    if (!string.IsNullOrEmpty(githubToken))
        c.DefaultRequestHeaders.Add("Authorization", $"token {githubToken}");
});

builder.Services.AddHttpClient<SourceDotNetAdapter>(c =>
    c.DefaultRequestHeaders.Add("User-Agent", "dotnet-source-resolver/mcp")
);

builder.Services.AddHttpClient<DocsSourceLinkAdapter>(c =>
    c.DefaultRequestHeaders.Add("User-Agent", "dotnet-source-resolver/mcp")
);

// Register adapters and resolver
builder.Services.AddSingleton<ISourceAdapter, SourceDotNetAdapter>();
builder.Services.AddSingleton<ISourceAdapter, DocsSourceLinkAdapter>();
builder.Services.AddSingleton(sp => new DotNetSourceResolver(
    sp.GetServices<ISourceAdapter>(),
    sp.GetRequiredService<ILogger<DotNetSourceResolver>>()
));

// Register MCP server (stdio transport)
builder
    .Services.AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly(typeof(DotnetTools).Assembly);

var app = builder.Build();

// Log startup configuration (goes to stderr)
var startupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");

startupLogger.LogInformation(
    "dotnet-source-resolver MCP server v{Version} starting",
    DotnetSourceResolver.Core.ResolverVersionProvider.Version
);

if (string.IsNullOrEmpty(githubToken))
    startupLogger.LogWarning(
        "GITHUB_TOKEN is not set. GitHub API calls may be rate-limited (60 req/hr)."
    );

startupLogger.LogInformation(
    "Config: RESOLVER_MAX_SNIPPET_LINES={MaxLines}, RESOLVER_LOG_LEVEL={LogLevel}",
    maxSnippetLines,
    logLevel
);

await app.RunAsync();

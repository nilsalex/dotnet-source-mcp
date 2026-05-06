using DotnetSourceResolver.Core.Models.NuGet;
using DotnetSourceResolver.Core.NuGet;
using DotnetSourceResolver.Core.Resolution;
using DotnetSourceResolver.Core.Sources;
using DotnetSourceResolver.McpServer.Prompts;
using DotnetSourceResolver.McpServer.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

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
var cacheDir =
    Environment.GetEnvironmentVariable("RESOLVER_CACHE_DIR")
    ?? Path.Combine(Path.GetTempPath(), "dotnet-source-resolver-cache");

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

builder.Services.AddHttpClient<NuSpecRepository>(c =>
    c.DefaultRequestHeaders.Add("User-Agent", "dotnet-source-resolver/mcp")
);

builder.Services.AddHttpClient<NuGetPackageDownloader>(c =>
{
    c.DefaultRequestHeaders.Add("User-Agent", "dotnet-source-resolver/mcp");
    c.Timeout = TimeSpan.FromMinutes(2);
});

builder.Services.AddHttpClient<GitHubFileLocator>(c =>
{
    c.DefaultRequestHeaders.Add("User-Agent", "dotnet-source-resolver/mcp");
    if (!string.IsNullOrEmpty(githubToken))
        c.DefaultRequestHeaders.Add("Authorization", $"token {githubToken}");
});

// NuGet infrastructure singletons
builder.Services.AddSingleton(new CacheConfiguration { CacheDirectory = cacheDir });
builder.Services.AddSingleton<SourceLinkExtractor>();
builder.Services.AddSingleton<SourceLinkMatcher>();

// Register adapters and resolver (order determines chain priority)
builder.Services.AddSingleton<ISourceAdapter, SourceDotNetAdapter>();
builder.Services.AddSingleton<ISourceAdapter, DocsSourceLinkAdapter>();
builder.Services.AddSingleton<ISourceAdapter, NuGetAdapter>();
builder.Services.AddSingleton(sp => new DotNetSourceResolver(
    sp.GetServices<ISourceAdapter>(),
    sp.GetRequiredService<ILogger<DotNetSourceResolver>>()
));

// Register MCP server (stdio transport)
builder
    .Services.AddMcpServer(options =>
    {
        options.ServerInfo = new Implementation
        {
            Name = "dotnet-source-resolver",
            Version = DotnetSourceResolver.Core.ResolverVersionProvider.Version,
        };
        options.ServerInstructions =
            "Resolves .NET symbols to exact source code locations with GitHub permalinks and code snippets.\n\n"
            + "Coverage:\n"
            + "- .NET BCL and framework libraries (System.*, Microsoft.*)\n"
            + "- ASP.NET Core (Microsoft.AspNetCore.*)\n"
            + "- Microsoft.Extensions.* (Options, DI, Logging, Configuration, etc.)\n"
            + "- NuGet packages with Source Link (Duende.BFF, Newtonsoft.Json, Serilog, and more)\n\n"
            + "For NuGet packages, provide packageId AND packageVersion:\n"
            + "  symbol='Duende.BFF.DefaultUserService', packageId='Duende.BFF', packageVersion='3.1.0'\n\n"
            + "Returns High confidence (exact file + line numbers) when Source Link is available,\n"
            + "Medium confidence (repository root) when Source Link is missing.";
    })
    .WithStdioServerTransport()
    .WithToolsFromAssembly(typeof(DotnetTools).Assembly)
    .WithPromptsFromAssembly(typeof(DotnetPrompts).Assembly);

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
    "Config: RESOLVER_MAX_SNIPPET_LINES={MaxLines}, RESOLVER_LOG_LEVEL={LogLevel}, RESOLVER_CACHE_DIR={CacheDir}",
    maxSnippetLines,
    logLevel,
    cacheDir
);

await app.RunAsync();

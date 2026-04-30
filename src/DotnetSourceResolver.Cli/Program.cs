using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using DotnetSourceResolver.Core.Models;
using DotnetSourceResolver.Core.Resolution;
using DotnetSourceResolver.Core.Sources;
using Microsoft.Extensions.Logging;

// Options
var symbolOption = new Option<string>("--symbol", "-s")
{
    Description = "Fully or partially qualified symbol name (required).",
    Required = true,
};

var packageOption = new Option<string?>("--package", "-p")
{
    Description = "NuGet package ID, e.g. System.Text.Json",
};

var versionOption = new Option<string?>("--version", "-v")
{
    Description = "Package version, e.g. 8.0.5",
};

var tfmOption = new Option<string?>("--tfm", "-t")
{
    Description = "Target framework moniker, e.g. net10.0",
};

var noSnippetsOption = new Option<bool>("--no-snippets")
{
    Description = "Omit source code snippets from the output",
};

var maxLinesOption = new Option<int>("--max-lines")
{
    Description = "Maximum lines per snippet",
    DefaultValueFactory = _ => 80,
};

// Sub-command
var resolveCommand = new Command("resolve", "Resolve a .NET symbol to its source location.");
resolveCommand.Options.Add(symbolOption);
resolveCommand.Options.Add(packageOption);
resolveCommand.Options.Add(versionOption);
resolveCommand.Options.Add(tfmOption);
resolveCommand.Options.Add(noSnippetsOption);
resolveCommand.Options.Add(maxLinesOption);

resolveCommand.SetAction(
    async (parseResult, ct) =>
    {
        var symbol = parseResult.GetValue(symbolOption)!;
        var package = parseResult.GetValue(packageOption);
        var version = parseResult.GetValue(versionOption);
        var tfm = parseResult.GetValue(tfmOption);
        var noSnippets = parseResult.GetValue(noSnippetsOption);
        var maxLines = parseResult.GetValue(maxLinesOption);

        using var loggerFactory = LoggerFactory.Create(b =>
            b.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Warning)
                .SetMinimumLevel(LogLevel.Warning)
        );

        var githubToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        var maxSnippetLines = int.TryParse(
            Environment.GetEnvironmentVariable("RESOLVER_MAX_SNIPPET_LINES"),
            out var envMax
        )
            ? envMax
            : maxLines;

        // Build adapter chain
        var githubHttp = BuildHttpClient(githubToken);
        var github = new GitHubAdapter(githubHttp, loggerFactory.CreateLogger<GitHubAdapter>());

        var sourceDotNetHttp = BuildHttpClient(null);
        var sourceDotNet = new SourceDotNetAdapter(
            sourceDotNetHttp,
            github,
            loggerFactory.CreateLogger<SourceDotNetAdapter>()
        );

        var docsHttp = BuildHttpClient(null);
        var docs = new DocsSourceLinkAdapter(
            docsHttp,
            github,
            loggerFactory.CreateLogger<DocsSourceLinkAdapter>()
        );

        var resolver = new DotNetSourceResolver(
            [sourceDotNet, docs],
            loggerFactory.CreateLogger<DotNetSourceResolver>()
        );

        var request = new SymbolRequest(
            Symbol: symbol,
            PackageId: package,
            PackageVersion: version,
            TargetFramework: tfm,
            IncludeSnippets: !noSnippets,
            MaxSnippetLines: maxSnippetLines
        );

        SourceResult result;
        try
        {
            result = await resolver.ResolveAsync(request, ct);
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"Error: {ex.Message}");
            return 2;
        }

        var json = JsonSerializer.Serialize(
            result,
            new JsonSerializerOptions
            {
                WriteIndented = true,
                Converters = { new JsonStringEnumConverter() },
            }
        );

        Console.WriteLine(json);
        return result.Resolved ? 0 : 1;
    }
);

// Root command
var rootCommand = new RootCommand("dotnet-source-resolver — look up .NET library source code");
rootCommand.Subcommands.Add(resolveCommand);

return await rootCommand.Parse(args).InvokeAsync();

// -------------------------------------------------------------------------
static HttpClient BuildHttpClient(string? githubToken)
{
    var client = new HttpClient();
    client.DefaultRequestHeaders.Add("User-Agent", "dotnet-source-resolver/cli");
    if (!string.IsNullOrEmpty(githubToken))
        client.DefaultRequestHeaders.Add("Authorization", $"token {githubToken}");
    return client;
}

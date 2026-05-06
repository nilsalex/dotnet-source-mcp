using System.ComponentModel;
using ModelContextProtocol.Server;

namespace DotnetSourceResolver.McpServer.Prompts;

[McpServerPromptType]
public static class DotnetPrompts
{
    [McpServerPrompt(Name = "lookup_dotnet_symbol")]
    [Description(
        "Resolve a .NET symbol to its source location. "
            + "Returns a user message ready to send to resolve_dotnet_source."
    )]
    public static string LookupDotnetSymbol(
        [Description(
            "Fully or partially qualified symbol, e.g. 'System.Text.StringBuilder.Append' or 'Duende.BFF.DefaultUserService'"
        )]
            string symbol,
        [Description(
            "NuGet package ID, e.g. 'Duende.BFF'. Required for third-party packages. Optional for framework types."
        )]
            string? packageId = null,
        [Description("NuGet package version, e.g. '3.1.0'. Required when packageId is specified.")]
            string? packageVersion = null,
        [Description("Target framework moniker, e.g. 'net10.0'. Optional.")]
            string? targetFramework = null
    )
    {
        var parts = new List<string>();
        parts.Add($"Resolve the source location for `{symbol}`");

        if (packageId is not null && packageVersion is not null)
            parts.Add($"from NuGet package `{packageId}` version `{packageVersion}`");
        else if (packageId is not null)
            parts.Add($"from NuGet package `{packageId}`");

        if (targetFramework is not null)
            parts.Add($"targeting {targetFramework}");

        return string.Join(" ", parts) + ".";
    }
}

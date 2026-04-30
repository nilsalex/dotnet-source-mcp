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
            "Fully or partially qualified symbol, e.g. 'System.Text.StringBuilder.Append'"
        )]
            string symbol,
        [Description("Target framework moniker, e.g. 'net10.0'. Optional.")]
            string? targetFramework = null
    ) =>
        targetFramework is not null
            ? $"Resolve the source location for `{symbol}` targeting {targetFramework}."
            : $"Resolve the source location for `{symbol}`.";
}

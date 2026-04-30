# dotnet-source-resolver

An MCP server that gives LLM agents precise, evidence-grounded access to .NET library source code.

Instead of having an agent improvise with reflection helpers or heuristic GitHub browsing, it calls a single tool and gets back exact source links, commit SHAs, and code snippets.

## What it covers (MVP)

- BCL (`System.Private.CoreLib`)
- ASP.NET Core
- `Microsoft.Extensions.*`
- Any Microsoft library indexed on [source.dot.net](https://source.dot.net)
- Public API entries on [learn.microsoft.com/dotnet/api](https://learn.microsoft.com/dotnet/api)

NuGet/Source Link resolution and decompilation fallback are planned for future slices.

## MCP tools

### `resolve_dotnet_source`

Resolves a .NET symbol to its source location.

```json
{
  "symbol": "System.Text.Json.JsonSerializer.Serialize",
  "packageId": "System.Text.Json",
  "packageVersion": "8.0.5",
  "targetFramework": "net10.0"
}
```

Returns: source links, GitHub permalink with commit SHA, code snippet, confidence (`High/Medium/Low`), diagnostics.

### `explain_dotnet_implementation`

Answers a question about an implementation, grounded in retrieved source snippets.

```json
{
  "symbol": "System.Collections.Generic.Dictionary",
  "question": "How is the internal hash table resized?"
}
```

Returns: answer text, evidence (source links + snippets), confidence, caveats.

## Building

```bash
dotnet build
```

## Running the MCP server

```bash
dotnet run --project src/DotnetSourceResolver.McpServer
```

Or publish a self-contained binary:

```bash
dotnet publish src/DotnetSourceResolver.McpServer -c Release -r linux-x64 --self-contained true -o ./publish
./publish/DotnetSourceResolver.McpServer
```

## MCP client configuration

```json
{
  "mcpServers": {
    "dotnet-source": {
      "command": "/path/to/DotnetSourceResolver.McpServer",
      "args": [],
      "env": {
        "GITHUB_TOKEN": "ghp_...",
        "RESOLVER_LOG_LEVEL": "Warning"
      }
    }
  }
}
```

## CLI (for debugging and interactive use)

```bash
# Resolve a symbol
dotnet run --project src/DotnetSourceResolver.Cli -- resolve \
  --symbol "System.Text.StringBuilder" \
  --tfm net10.0

# With full options
dotnet run --project src/DotnetSourceResolver.Cli -- resolve \
  --symbol "System.Collections.Generic.Dictionary" \
  --package "System.Private.CoreLib" \
  --tfm net10.0 \
  --max-lines 60

# Omit snippets (faster, just source links)
dotnet run --project src/DotnetSourceResolver.Cli -- resolve \
  --symbol "System.Text.Json.JsonSerializer" \
  --no-snippets
```

## Configuration

| Variable | Default | Description |
|---|---|---|
| `GITHUB_TOKEN` | (none) | GitHub PAT — without it you get 60 requests/hr |
| `RESOLVER_LOG_LEVEL` | `Warning` | `Trace`, `Debug`, `Information`, `Warning`, `Error` |
| `RESOLVER_MAX_SNIPPET_LINES` | `80` | Global default snippet length |
| `RESOLVER_RUN_LIVE_TESTS` | `false` | Enable live network tests in the test suite |

## Testing

```bash
# Fast unit tests (no network)
dotnet test --filter "Category!=Live"

# Live integration tests (requires network)
RESOLVER_RUN_LIVE_TESTS=true dotnet test --filter "Category=Live"
```

## Architecture

```
MCP Client / IDE
      │ stdio (JSON-RPC)
DotnetSourceResolver.McpServer
      │ direct call
DotnetSourceResolver.Core
      ├── SourceDotNetAdapter   → source.dot.net search + file pages
      ├── DocsSourceLinkAdapter → learn.microsoft.com source links
      └── GitHubAdapter         → raw file fetch + snippet extraction
```

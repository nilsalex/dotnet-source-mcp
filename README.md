# dotnet-source-mcp

An MCP server that gives LLM agents precise, evidence-grounded access to .NET library source code.

Instead of having an agent improvise with reflection helpers or heuristic GitHub browsing, it calls a single tool and gets back exact source links, commit SHAs, and code snippets.

## Install

```bash
dotnet tool install --global DotnetSourceMcp
```

## Agent configuration

### Claude Code

```bash
claude mcp add --transport stdio dotnet-source-mcp -- dotnet-source-mcp
```

Or add to `.mcp.json` in your project root (check into version control for team use):

```json
{
  "mcpServers": {
    "dotnet-source-mcp": {
      "command": "dotnet-source-mcp",
      "args": [],
      "env": {
        "GITHUB_TOKEN": "${GITHUB_TOKEN}"
      }
    }
  }
}
```

### OpenCode

Add to `opencode.json`:

```json
{
  "mcp": {
    "dotnet-source-mcp": {
      "type": "local",
      "command": ["dotnet-source-mcp"],
      "environment": {
        "GITHUB_TOKEN": "{env:GITHUB_TOKEN}"
      }
    }
  }
}
```

### GitHub Copilot / VS Code

Add to `.vscode/mcp.json`:

```json
{
  "servers": {
    "dotnet-source-mcp": {
      "type": "stdio",
      "command": "dotnet-source-mcp",
      "args": [],
      "env": {
        "GITHUB_TOKEN": "${GITHUB_TOKEN}"
      }
    }
  }
}
```

### GitHub Copilot cloud agent

Add the MCP configuration in your repository settings (Settings → Copilot → Cloud agent):

```json
{
  "mcpServers": {
    "dotnet-source-mcp": {
      "type": "local",
      "command": "dotnet-source-mcp",
      "args": [],
      "tools": ["resolve_dotnet_source", "explain_dotnet_implementation"]
    }
  }
}
```

The .NET tool must be installed on the runner. Add `.github/workflows/copilot-setup-steps.yml`:

```yaml
on:
  workflow_dispatch:
permissions:
  contents: read
jobs:
  copilot-setup-steps:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: "10.0.x"
      - run: dotnet tool install --global DotnetSourceMcp
```

### Cursor

Add to `.cursor/mcp.json` in your project root:

```json
{
  "mcpServers": {
    "dotnet-source-mcp": {
      "command": "dotnet-source-mcp",
      "args": [],
      "env": {
        "GITHUB_TOKEN": "${GITHUB_TOKEN}"
      }
    }
  }
}
```

### Generic (any MCP client)

```json
{
  "mcpServers": {
    "dotnet-source-mcp": {
      "command": "dotnet-source-mcp",
      "args": [],
      "env": {
        "GITHUB_TOKEN": "ghp_...",
        "RESOLVER_LOG_LEVEL": "Warning"
      }
    }
  }
}
```

Find the tool path for manual config:

```bash
which dotnet-source-mcp
```

## What it covers

- BCL (`System.Private.CoreLib`)
- ASP.NET Core
- `Microsoft.Extensions.*`
- Any Microsoft library indexed on [source.dot.net](https://source.dot.net)
- Public API entries on [learn.microsoft.com/dotnet/api](https://learn.microsoft.com/dotnet/api)
- NuGet packages with Source Link (provide `packageId` + `packageVersion`)

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

## Configuration

| Variable | Default | Description |
|---|---|---|
| `GITHUB_TOKEN` | (none) | GitHub PAT — without it you get 60 requests/hr |
| `RESOLVER_LOG_LEVEL` | `Warning` | `Trace`, `Debug`, `Information`, `Warning`, `Error` |
| `RESOLVER_MAX_SNIPPET_LINES` | `80` | Global default snippet length |
| `RESOLVER_CACHE_DIR` | `{temp}/dotnet-source-resolver-cache` | NuGet package download cache |

## Development

### Building

```bash
dotnet build
```

### Running from source

```bash
dotnet run --project src/DotnetSourceResolver.McpServer
```

Or publish a self-contained binary:

```bash
dotnet publish src/DotnetSourceResolver.McpServer -c Release -r linux-x64 --self-contained true -o ./publish
./publish/DotnetSourceResolver.McpServer
```

### CLI (for debugging)

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

### Testing

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
      ├── NuGetAdapter          → .nuspec + embedded PDB Source Link extraction
      └── GitHubAdapter         → raw file fetch + snippet extraction
```

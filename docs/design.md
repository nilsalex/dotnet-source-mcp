# Design Document: .NET Source Resolver MCP Server

## Problem

LLM agents working on .NET development tasks need to look up library internals —
implementation details, method bodies, type hierarchies, caching behaviour, etc.
Without a targeted tool, agents improvise: they write small reflection helpers,
browse GitHub heuristically, or hallucinate. All of these approaches are slow,
unreliable, and version-unaware.

The goal is a purpose-built MCP server that gives any agent a single,
high-level, evidence-grounded interface to .NET source code.

---

## Goals

- Provide two high-level MCP tools: `resolve_dotnet_source` and
  `explain_dotnet_implementation`
- Return structured, versioned, evidence-grounded results (source links +
  snippets + confidence)
- Cover the BCL / Microsoft framework libraries as the MVP scope
- Be testable without network access (mocked HTTP) and exercisable
  interactively (CLI)
- Keep the resolver logic decoupled from the MCP protocol layer

## Non-goals (MVP)

- NuGet package + Source Link / PDB resolution (planned for a future slice)
- Decompilation fallback
- Third-party package support
- Centralised/shared deployment (local stdio process only for now)

---

## Architecture

The system is structured as **one product, two layers**:

```
┌─────────────────────────────────────────────────────┐
│                  MCP Clients / IDEs                 │
└────────────────────────┬────────────────────────────┘
                         │ stdio (JSON-RPC / MCP)
┌────────────────────────▼────────────────────────────┐
│         DotnetSourceResolver.McpServer              │
│  - registers tools via [McpServerTool]              │
│  - translates MCP input → Core request              │
│  - translates Core result → MCP output              │
└────────────────────────┬────────────────────────────┘
                         │ direct library call
┌────────────────────────▼────────────────────────────┐
│           DotnetSourceResolver.Core                 │
│  - symbol normalisation                             │
│  - resolution pipeline (adapter chain)              │
│  - snippet extraction                               │
│  - confidence assignment                            │
└──────┬──────────────────┬──────────────────┬────────┘
       │                  │                  │
  SourceDotNet        DocsSource          GitHub
  Adapter             LinkAdapter         Adapter
  (source.dot.net)    (learn.ms API)      (raw file fetch)
```

Additionally, `DotnetSourceResolver.Cli` calls `Core` directly for interactive
use and debugging.

### Layers in detail

**`DotnetSourceResolver.Core`** — pure class library, no MCP dependency

Responsibilities:
- Accept a `SymbolRequest` and return a `SourceResult`
- Orchestrate adapters in priority order, stopping at first confident result
- Extract and trim code snippets to the requested line range
- Assign `confidence` based on which adapter succeeded and how

**`DotnetSourceResolver.McpServer`** — thin console app

Responsibilities:
- Host the MCP server over stdio using `ModelContextProtocol` SDK
- Declare the two tools with their JSON schemas
- Read configuration from environment variables
- Delegate all resolution logic to Core

**`DotnetSourceResolver.Cli`** — console app

Responsibilities:
- Accept `resolve` sub-command with `--symbol`, `--package`, `--version`,
  `--tfm` flags
- Call Core, print `SourceResult` as formatted JSON to stdout
- Used for interactive exploration and live smoke tests

---

## Repository layout

```
dotnet-mcp/
├── src/
│   ├── DotnetSourceResolver.Core/
│   │   ├── Models/
│   │   │   ├── SymbolRequest.cs
│   │   │   ├── SourceResult.cs
│   │   │   ├── SourceEntry.cs
│   │   │   ├── SnippetEntry.cs
│   │   │   └── ResolutionConfidence.cs
│   │   ├── Resolution/
│   │   │   ├── ISourceAdapter.cs
│   │   │   └── DotNetSourceResolver.cs
│   │   └── Sources/
│   │       ├── SourceDotNetAdapter.cs
│   │       ├── DocsSourceLinkAdapter.cs
│   │       └── GitHubAdapter.cs
│   ├── DotnetSourceResolver.McpServer/
│   │   ├── Program.cs
│   │   └── Tools/
│   │       └── DotnetTools.cs
│   └── DotnetSourceResolver.Cli/
│       └── Program.cs
├── tests/
│   ├── DotnetSourceResolver.Core.Tests/
│   │   ├── Resolution/
│   │   │   └── DotNetSourceResolverTests.cs
│   │   ├── Sources/
│   │   │   ├── SourceDotNetAdapterTests.cs
│   │   │   ├── DocsSourceLinkAdapterTests.cs
│   │   │   └── GitHubAdapterTests.cs
│   │   └── Live/
│   │       └── LiveResolutionTests.cs
│   └── DotnetSourceResolver.McpServer.Tests/
│       └── McpToolsTests.cs
├── DotnetSourceResolver.sln
└── docs/
    ├── design.md           (this file)
    └── implementation-plan.md
```

---

## Data models

### `SymbolRequest`

```csharp
public record SymbolRequest(
    string Symbol,                   // required; fully or partially qualified
    string? PackageId      = null,
    string? PackageVersion = null,
    string? AssemblyName   = null,
    string? TargetFramework = null,
    bool   IncludeSnippets = true,
    int    MaxSnippetLines = 80
);
```

### `SourceResult`

```csharp
public record SourceResult(
    bool               Resolved,
    string             CanonicalSymbol,
    ResolutionKind     ResolutionKind,
    ResolutionConfidence Confidence,
    IReadOnlyList<SourceEntry>  Sources,
    IReadOnlyList<SnippetEntry> Snippets,
    IReadOnlyList<string>       Diagnostics
);
```

### `SourceEntry`

```csharp
public record SourceEntry(
    string Kind,          // "github", "source.dot.net", "docs"
    string Repository,
    string Commit,
    string Path,
    string Url,
    int    StartLine,
    int    EndLine
);
```

### `SnippetEntry`

```csharp
public record SnippetEntry(
    string Path,
    int    StartLine,
    int    EndLine,
    string Code
);
```

### `ResolutionConfidence`

```csharp
public enum ResolutionConfidence { High, Medium, Low }
```

Confidence assignment:
- `High` — exact commit SHA known, source fetched from GitHub permalink
- `Medium` — source found via `source.dot.net` or docs but without exact commit
- `Low` — only a file URL without line-level precision

---

## Resolution pipeline

Adapters are tried in order. The first adapter to return a non-null result wins.
All adapters implement:

```csharp
public interface ISourceAdapter
{
    Task<SourceResult?> TryResolveAsync(
        SymbolRequest request,
        CancellationToken ct);
}
```

### MVP adapter chain (framework/BCL scope)

1. **`SourceDotNetAdapter`**
   - Queries `https://source.dot.net` search/symbol endpoints
   - Targets `dotnet/runtime`, `aspnetcore`, `extensions`
   - Extracts file path + line range from response
   - Fetches raw source via GitHub if a permalink is found
   - Confidence: `High` if commit SHA present, else `Medium`

2. **`DocsSourceLinkAdapter`**
   - Fetches `https://learn.microsoft.com/dotnet/api/{normalized-symbol}`
   - Parses the page for "Source" / "View Source" links pointing to GitHub
   - Extracts repo, commit, path, line range from the link URL
   - Confidence: `Medium` (commit sometimes present, sometimes not)

3. **`GitHubAdapter`**
   - Used by the adapters above as a shared file-fetching primitive
   - Also exposed standalone for fetching a known permalink
   - Fetches raw content via `https://raw.githubusercontent.com/{owner}/{repo}/{commit}/{path}`
   - Cuts snippet to `[startLine, endLine]` range
   - Respects `GITHUB_TOKEN` env var for rate-limit avoidance

If all adapters return null, `SourceResult` has `Resolved = false` and
`Confidence = Low`.

---

## MCP tool schemas

### `resolve_dotnet_source`

**Input**

| Field | Type | Required | Notes |
|---|---|---|---|
| `symbol` | string | yes | Fully or partially qualified symbol |
| `packageId` | string | no | NuGet package ID or assembly name |
| `packageVersion` | string | no | Semver string |
| `assemblyName` | string | no | Assembly name if different from package |
| `targetFramework` | string | no | e.g. `net10.0` |
| `includeSnippets` | bool | no | Default `true` |
| `maxSnippetLines` | int | no | Default `80` |

**Output** — serialised `SourceResult`

### `explain_dotnet_implementation`

**Input**

| Field | Type | Required | Notes |
|---|---|---|---|
| `symbol` | string | yes | Symbol to explain |
| `question` | string | yes | Natural language question about the implementation |
| `packageId` | string | no | |
| `packageVersion` | string | no | |
| `targetFramework` | string | no | |

**Output**

```json
{
  "answer": "string",
  "evidence": [ { "url": "...", "path": "...", "startLine": 0, "endLine": 0, "summary": "..." } ],
  "confidence": "High|Medium|Low",
  "caveats": [ "string" ]
}
```

The `explain` tool calls `resolve_dotnet_source` internally, then synthesises
an explanation from the returned snippets. In MVP the synthesis step may simply
return the snippets with a minimal framing sentence; a richer LLM-backed
synthesis step is a future concern.

---

## Configuration (environment variables)

| Variable | Default | Description |
|---|---|---|
| `GITHUB_TOKEN` | (none) | GitHub PAT for authenticated API calls |
| `RESOLVER_CACHE_DIR` | `~/.cache/dotnet-source-resolver` | Filesystem cache root |
| `RESOLVER_MAX_SNIPPET_LINES` | `80` | Global default for snippet length |
| `RESOLVER_LOG_LEVEL` | `Information` | Microsoft.Extensions.Logging level |
| `RESOLVER_ENABLE_DECOMPILATION` | `false` | Future: enable decompiler fallback |
| `RESOLVER_RUN_LIVE_TESTS` | `false` | Enable live network tests (test suite only) |

---

## Testing strategy

### Unit tests — `DotnetSourceResolver.Core.Tests`

- All HTTP calls mocked via `MockHttpMessageHandler` (no network)
- Test each adapter independently with canned HTTP responses
- Test `DotNetSourceResolver` orchestration: correct adapter priority, fallback
  behaviour, result merging
- Test snippet extraction edge cases (out-of-range lines, empty files)
- Test symbol normalisation helpers

### In-process MCP integration tests — `DotnetSourceResolver.McpServer.Tests`

- Use `StreamServerTransport` + `StreamClientTransport` over `System.IO.Pipelines`
- Spin up real MCP server in-process; HTTP still mocked in Core
- Assert tool names are registered, input schemas are correct, output is valid
  JSON matching `SourceResult` shape
- Test error handling: unknown symbol, network timeout

### Live tests — gated by `RESOLVER_RUN_LIVE_TESTS=true`

- Marked `[Trait("Category", "Live")]`
- Filtered out by default: `dotnet test --filter "Category!=Live"`
- Enabled: `RESOLVER_RUN_LIVE_TESTS=true dotnet test`
- Resolve one known BCL symbol (e.g. `System.Collections.Generic.Dictionary`)
  and assert `Resolved == true` and `Sources` is non-empty

### Interactive / development loop (CLI)

```bash
# from repo root
dotnet run --project src/DotnetSourceResolver.Cli -- \
  resolve \
  --symbol "System.Collections.Generic.Dictionary" \
  --tfm net10.0
```

Prints the full `SourceResult` JSON. Used to verify adapter behaviour against
real sources during development.

---

## Future slices (out of scope for MVP)

| Slice | Description |
|---|---|
| NuGet metadata | Resolve repository URL and commit from `.nupkg` metadata |
| Source Link / PDB | Extract exact file↔commit mapping from portable PDBs |
| Decompilation fallback | Use ILSpy/ICSharpCode for packages without source |
| Snippet ranking | Score and rank multiple snippet candidates |
| Result cache | SQLite cache keyed by `(symbol, packageId, version)` |
| HTTP transport | Expose as ASP.NET Core MCP server for shared deployment |

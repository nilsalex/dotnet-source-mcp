# Implementation Plan: .NET Source Resolver MCP Server

This document breaks the implementation into ordered, independently verifiable
slices. Each slice ends with a clearly stated "done" criterion that can be
checked by running the test suite or the CLI.

For design rationale, data models, and architecture, see `design.md`.

---

## Prerequisites

- .NET 10 SDK installed (`dotnet --version` → `10.x`)
- `ModelContextProtocol` NuGet package v1.2.0
- `xunit` and `Microsoft.NET.Test.Sdk` for tests
- `Moq` or `NSubstitute` for HTTP mocking in unit tests

---

## Slice 0 — Solution scaffold

**Goal:** working solution file, all projects compile, `dotnet test` runs and
reports 0 tests (no test code yet).

### Tasks

1. Create solution: `dotnet new sln -n DotnetSourceResolver`
2. Create projects:
   - `dotnet new classlib -n DotnetSourceResolver.Core -f net10.0`
   - `dotnet new console -n DotnetSourceResolver.McpServer -f net10.0`
   - `dotnet new console -n DotnetSourceResolver.Cli -f net10.0`
   - `dotnet new xunit -n DotnetSourceResolver.Core.Tests -f net10.0`
   - `dotnet new xunit -n DotnetSourceResolver.McpServer.Tests -f net10.0`
3. Add all projects to the solution
4. Add project references:
   - `McpServer` → `Core`
   - `Cli` → `Core`
   - `Core.Tests` → `Core`
   - `McpServer.Tests` → `Core` + `McpServer`
5. Add NuGet packages:
   - `Core`: `Microsoft.Extensions.Http`
   - `McpServer`: `ModelContextProtocol`, `Microsoft.Extensions.Hosting`
   - `Core.Tests`: `xunit`, `Microsoft.NET.Test.Sdk`, `Moq`
   - `McpServer.Tests`: `ModelContextProtocol`, `Microsoft.NET.Test.Sdk`, `xunit`
6. Commit a `.gitignore` (standard .NET)

**Done criterion:** `dotnet build` succeeds; `dotnet test` exits 0.

---

## Slice 1 — Core models and interfaces

**Goal:** data models and `ISourceAdapter` interface in `Core`; no logic yet.

### Tasks

1. Create `Models/ResolutionConfidence.cs` — enum `High | Medium | Low`
2. Create `Models/ResolutionKind.cs` — enum `SourceDotNet | Docs | GitHub | Unresolved`
3. Create `Models/SymbolRequest.cs` — record (see design doc)
4. Create `Models/SourceEntry.cs` — record
5. Create `Models/SnippetEntry.cs` — record
6. Create `Models/SourceResult.cs` — record with factory method `Unresolved(string symbol, IEnumerable<string> diagnostics)`
7. Create `Resolution/ISourceAdapter.cs` — interface
8. Write unit tests asserting the `Unresolved` factory sets correct defaults

**Done criterion:** `dotnet test` — all model tests pass.

---

## Slice 2 — Resolver orchestrator

**Goal:** `DotNetSourceResolver` tries adapters in order, returns first
non-null result, returns `Unresolved` if all adapters return null.

### Tasks

1. Create `Resolution/DotNetSourceResolver.cs`
   - Constructor takes `IEnumerable<ISourceAdapter>`
   - `ResolveAsync(SymbolRequest, CancellationToken)` loops adapters
2. Unit tests (all HTTP mocked):
   - First adapter returns result → resolver returns it
   - First adapter returns null, second returns result → resolver returns second
   - All adapters return null → resolver returns `Unresolved`
   - Adapter throws → resolver wraps in diagnostic, tries next adapter
   - CancellationToken propagated to adapters

**Done criterion:** `dotnet test` — all orchestrator tests pass.

---

## Slice 3 — GitHubAdapter (shared fetch primitive)

**Goal:** fetch raw file content from a GitHub permalink URL; extract snippet
by line range.

### Tasks

1. Create `Sources/GitHubAdapter.cs`
   - `TryResolveAsync` accepts a `SymbolRequest` that contains a pre-built
     GitHub raw URL in a dedicated field (or is called directly by other adapters)
   - Internal `FetchSnippetAsync(string rawUrl, int startLine, int endLine, CancellationToken)` method
   - Respects `GITHUB_TOKEN` env var via `Authorization: token` header
   - Handles 404 (returns null), 403/429 rate-limit (throws with diagnostic)
2. Unit tests (mocked `HttpMessageHandler`):
   - Happy path: returns snippet for requested line range
   - Line range clamped if file is shorter than requested range
   - 404 → returns null
   - 401/403 → throws with informative message
   - GITHUB_TOKEN injected into request header when set

**Done criterion:** `dotnet test` — all `GitHubAdapterTests` pass.

---

## Slice 4 — SourceDotNetAdapter

**Goal:** resolve a symbol via `source.dot.net`, return a `SourceResult` with
GitHub source link and snippet.

### Notes on source.dot.net

`source.dot.net` is a Roslyn-backed source browser. Key URL patterns:

- Symbol search: `https://source.dot.net/api/find?query={symbol}`
- Source file browse: `https://source.dot.net/{project}/{path}`

The search API returns JSON with `results[]` each containing `projectName`,
`fileName`, `lineNumber`. From that we can construct a GitHub permalink using
the known repository and the commit pinned to the project version.

In practice the simplest reliable approach is:
1. GET `https://source.dot.net/api/find?query={symbol}` — returns JSON array
2. Pick the best match (prefer exact type/member match)
3. The result includes a `url` field pointing to `source.dot.net/{project}/{file}`
4. Fetch that page and extract the GitHub "View on GitHub" permalink from the HTML

### Tasks

1. Create `Sources/SourceDotNetAdapter.cs`
   - Calls `https://source.dot.net/api/find?query={symbol}`
   - Parses JSON response to find best candidate
   - Fetches the detail page and extracts GitHub permalink
   - Delegates file content fetch to `GitHubAdapter.FetchSnippetAsync`
   - Returns `SourceResult` with `ResolutionKind.SourceDotNet`
   - Confidence: `High` if commit SHA present in permalink, else `Medium`
2. Unit tests (mocked HTTP):
   - Known symbol found → correct `SourceEntry` and snippet returned
   - Symbol not found (empty results) → returns null
   - Ambiguous results → picks best match (exact type name wins)
   - GitHub fetch fails → still returns result with empty snippets and diagnostic
3. Live test (`[Trait("Category", "Live")]`):
   - Resolve `System.Collections.Generic.Dictionary` → `Resolved == true`
   - At least one `SourceEntry` with non-empty `Url`

**Done criterion:** unit tests pass; live test passes when
`RESOLVER_RUN_LIVE_TESTS=true`.

---

## Slice 5 — DocsSourceLinkAdapter

**Goal:** resolve a symbol via `learn.microsoft.com/dotnet/api`, extract the
GitHub source link from the page HTML.

### Notes on docs source links

Many API pages on `learn.microsoft.com/dotnet/api/{fully-qualified-name}` have
a "Source" button that links to GitHub (e.g.
`https://github.com/dotnet/runtime/blob/{sha}/src/.../File.cs#L42-L80`).

The adapter:
1. Normalises the symbol to the docs URL format
   (e.g. `System.Collections.Generic.Dictionary` →
   `system.collections.generic.dictionary`)
2. Fetches `https://learn.microsoft.com/dotnet/api/{normalised}`
3. Scrapes the first GitHub link matching the pattern
   `github.com/{owner}/{repo}/blob/{sha}/{path}#L{start}-L{end}`
4. Extracts `repo`, `commit`, `path`, `startLine`, `endLine`
5. Delegates content fetch to `GitHubAdapter.FetchSnippetAsync`

### Tasks

1. Create `Sources/DocsSourceLinkAdapter.cs` (logic as above)
2. Unit tests (mocked HTTP):
   - Page contains source link → correct `SourceEntry` extracted
   - Page has no source link → returns null
   - Page returns 404 → returns null
3. Live test (`[Trait("Category", "Live")]`):
   - Resolve `System.Text.StringBuilder` → at least one source entry

**Done criterion:** unit tests pass; live test passes when
`RESOLVER_RUN_LIVE_TESTS=true`.

---

## Slice 6 — CLI

**Goal:** `dotnet run --project src/DotnetSourceResolver.Cli -- resolve
--symbol "..." [--package ...] [--version ...] [--tfm ...]` prints a valid
`SourceResult` JSON.

### Tasks

1. Add `System.CommandLine` NuGet package to `Cli`
2. Implement `Program.cs`:
   - Parse `resolve` sub-command with options
   - Build `SymbolRequest` from parsed values
   - Construct `DotNetSourceResolver` with default adapter chain
   - Call `ResolveAsync`, print result as indented JSON to stdout
   - Print error diagnostics to stderr
3. Manual verification: run against a known symbol, inspect JSON output

**Done criterion:** `dotnet run --project src/DotnetSourceResolver.Cli -- resolve --symbol "System.Collections.Generic.List" --tfm net10.0` exits 0 and prints JSON containing `"Resolved": true`.

---

## Slice 7 — MCP server

**Goal:** stdio MCP server with two tools (`resolve_dotnet_source`,
`explain_dotnet_implementation`) callable in-process via the SDK test helpers.

### Tasks

1. Implement `McpServer/Program.cs`:
   - `Host.CreateApplicationBuilder` + `AddMcpServer()` + `WithStdioServerTransport()` + `WithToolsFromAssembly()`
   - Logging redirected to stderr
   - `DotNetSourceResolver` registered in DI with default adapter chain
   - Read configuration from env vars (`GITHUB_TOKEN`, `RESOLVER_MAX_SNIPPET_LINES`, etc.)
2. Implement `McpServer/Tools/DotnetTools.cs`:
   - `[McpServerToolType]` class
   - `[McpServerTool] resolve_dotnet_source(...)` — maps parameters to `SymbolRequest`, calls resolver, returns serialised `SourceResult`
   - `[McpServerTool] explain_dotnet_implementation(...)` — calls resolve internally, wraps snippets into an explanation response
3. In-process integration tests (`McpServer.Tests/McpToolsTests.cs`):
   - Wire server using `StreamServerTransport` + `StreamClientTransport` over `System.IO.Pipelines`; mock HTTP in Core
   - Assert `tools/list` returns both tool names
   - Call `resolve_dotnet_source` with valid input → response parses as `SourceResult` with correct shape
   - Call `explain_dotnet_implementation` with valid input → response has `answer`, `evidence`, `confidence`
   - Call with missing required parameter → error response

**Done criterion:** `dotnet test` — all MCP integration tests pass.

---

## Slice 8 — Polish and hardening

**Goal:** production-ready error handling, consistent logging, configuration
validation, and README.

### Tasks

1. Add `OperationCanceledException` and `HttpRequestException` handling in
   resolver and adapters — always produce a `SourceResult` with diagnostics
   rather than throwing to the MCP layer
2. Validate configuration at startup (log warning if `GITHUB_TOKEN` not set)
3. Add `[Description(...)]` attributes to all MCP tool parameters (improves
   agent usage)
4. Add `resolverVersion` field to all responses (read from assembly version)
5. Write `README.md` with:
   - What the tool does
   - How to build and run
   - MCP config example
   - 3 example CLI invocations
6. Ensure `dotnet test --filter "Category!=Live"` passes cleanly in a
   no-network environment

**Done criterion:** `dotnet test --filter "Category!=Live"` passes; CLI and
MCP server start cleanly.

---

## Slice ordering summary

| Slice | Deliverable | Depends on |
|---|---|---|
| 0 | Solution scaffold | — |
| 1 | Models + interface | 0 |
| 2 | Resolver orchestrator | 1 |
| 3 | GitHubAdapter | 2 |
| 4 | SourceDotNetAdapter | 3 |
| 5 | DocsSourceLinkAdapter | 3 |
| 6 | CLI | 4, 5 |
| 7 | MCP server | 4, 5 |
| 8 | Polish | 6, 7 |

Slices 4 and 5 can be developed in parallel once Slice 3 is done.
Slices 6 and 7 can be developed in parallel once Slices 4 and 5 are done.

---

## Development loop summary

| Activity | Command |
|---|---|
| Fast unit tests (no network) | `dotnet test --filter "Category!=Live"` |
| Live tests (real network) | `RESOLVER_RUN_LIVE_TESTS=true dotnet test --filter "Category=Live"` |
| Interactive resolution | `dotnet run --project src/DotnetSourceResolver.Cli -- resolve --symbol "..." --tfm net10.0` |
| Full build | `dotnet build` |

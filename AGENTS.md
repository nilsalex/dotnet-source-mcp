# AGENTS.md — dotnet-source-resolver

## Essential commands

```bash
# Normal dev loop (no network, fast)
dotnet test --filter "Category!=Live"

# Live tests against real sources (network required)
RESOLVER_RUN_LIVE_TESTS=true dotnet test --filter "Category=Live"

# Interactive resolution — the fastest way to validate adapter behaviour
dotnet run --project src/DotnetSourceResolver.Cli -- resolve \
  --symbol "System.Text.StringBuilder" --tfm net10.0

# NuGet package resolution (interactive)
dotnet run --project src/DotnetSourceResolver.Cli -- resolve \
  --symbol "Duende.BFF.DefaultUserService" \
  --package "Duende.BFF" \
  --version "3.1.0"

# Full build
dotnet build

# Format all C# files (CSharpier, installed as local tool)
dotnet csharpier format .
```

## Project layout

| Project | Role |
|---|---|
| `src/DotnetSourceResolver.Core` | All resolution logic; no MCP dependency |
| `src/DotnetSourceResolver.Core/NuGet/` | NuGet package resolution (Source Link extraction) |
| `src/DotnetSourceResolver.McpServer` | stdio MCP server, thin wrapper over Core |
| `src/DotnetSourceResolver.Cli` | CLI for interactive use and debugging |
| `tests/DotnetSourceResolver.Core.Tests` | Unit tests (123) + live tests (6) |
| `tests/DotnetSourceResolver.McpServer.Tests` | In-process MCP integration tests (13) |

Target: **net10.0** throughout. Solution file: `DotnetSourceResolver.sln`.

## Adapter chain (Core)

`DotNetSourceResolver` tries adapters in this order, stops at first non-null result:

1. `SourceDotNetAdapter` — scrapes `source.dot.net` HTML (not a JSON API)
2. `DocsSourceLinkAdapter` — scrapes `learn.microsoft.com/en-us/dotnet/api/{symbol}`
3. `NuGetAdapter` — fetches `.nuspec` + downloads `.nupkg`, reads embedded PDB, extracts Source Link; requires `PackageId` + `PackageVersion`
4. `GitHubAdapter` — shared raw-file fetch primitive used by the adapters above; also an `ISourceAdapter` for pre-resolved GitHub URLs

## NuGet resolution pipeline (NuGetAdapter)

**Phase 1 (always runs):** Fetch `.nuspec` from `api.nuget.org/v3-flatcontainer/{id}/{version}/{id}.nuspec` → parse `<repository>` element → get repository URL + commit SHA.

**Phase 2 (runs when PackageId + PackageVersion present):** Download `.nupkg` → extract `lib/{tfm}/{id}.dll` → read embedded Portable PDB via `System.Reflection.Metadata` → extract Source Link JSON (GUID `CC110556-A091-4D38-9FEC-25AB9A351A6A`) → match symbol to file path using namespace→directory heuristics.

**Fallback chain:** Phase 2 fail → Phase 1 fallback (repo root, `Confidence.Medium`). No PDB/Source Link → same fallback.

**Confidence levels:**
- `High` — file-level URL with commit SHA (Source Link present)
- `Medium` — repository root URL from `.nuspec` (GitHub repos)
- `Low` — repository root URL for non-GitHub repos (Azure DevOps, GitLab, etc.)

## source.dot.net scraping — non-obvious flow

The site has no public JSON API. The adapter does 4 HTTP fetches per resolution:

1. `GET /api/symbols/?symbol={q}` → returns **HTML** (not JSON) with result links
2. `GET /{project}/A{firstHashChar}.html` → bucket file containing `var m = {...}` JS map (hash prefix → file index) and `var f = [...]` file list
3. `GET /{project}/{file}.cs.html` → source page; GitHub `tree` URL with SHA is in the page header
4. `GET raw.githubusercontent.com/...` → actual file content for snippet extraction

## InternalsVisibleTo

`DotnetSourceResolver.Core` exposes `internal` helpers to the test project via `InternalsVisibleToAttribute` in the `.csproj` (not in `AssemblyInfo.cs`). The LSP often reports false "inaccessible" errors for these — `dotnet build` is the source of truth.

The LSP also reports false errors for `SourceLinkExtractorTestHelper` in `NuGetAdapterTests.cs` — they are in the same namespace and build correctly.

## MCP server metadata

Agent-facing context is provided through two mechanisms — keep both accurate when changing scope or tool behaviour:

- **`McpServerOptions.ServerInstructions`** — sent in the `initialize` response; MCP clients inject it as a system message. Set in `McpServer/Program.cs` via `AddMcpServer(options => { ... })`.
- **`prompts/list`** — the `lookup_dotnet_symbol` prompt in `McpServer/Prompts/DotnetPrompts.cs` gives agents a structured entry point. Registered via `.WithPromptsFromAssembly(typeof(DotnetPrompts).Assembly)`.
- **Tool `[Description(...)]` attributes** — the only per-tool structured signal the model sees; live in `McpServer/Tools/DotnetTools.cs`.

## MCP SDK quirks

- Package: `ModelContextProtocol` v1.2.0 (not `ModelContextProtocol.Core`)
- `McpServer` conflicts with the project namespace `DotnetSourceResolver.McpServer` — alias it: `using McpServerType = ModelContextProtocol.Server.McpServer;`
- Use `tool.CallAsync(IReadOnlyDictionary<string,object?>)` in tests — **not** `InvokeAsync` (which requires `AIFunctionArguments`)
- Read tool result text with `result.Content.OfType<TextContentBlock>().First().Text`
- `PromptMessage.Content` is a single `ContentBlock` (not a list) — cast directly: `(TextContentBlock)result.Messages[0].Content`
- All logging must go to **stderr** (`LogToStandardErrorThreshold = LogLevel.Trace`) so it does not corrupt the stdout MCP stream

## System.CommandLine v3 API (CLI project)

The CLI uses `System.CommandLine` 3.0.0-preview.3 which has a **breaking API** vs v2 beta:
- Options: `new Option<T>("--name") { Description = "...", Required = true, DefaultValueFactory = _ => value }`
- Register: `command.Options.Add(opt)` / `command.Subcommands.Add(sub)`
- Handler: `command.SetAction(async (parseResult, ct) => { ... return exitCode; })`
- Entry point: `rootCommand.Parse(args).InvokeAsync()` (no `.Build()`)

## Test patterns

**Unit tests (Core):** Mock `HttpMessageHandler` directly via Moq. Register the fallback 404 handler **before** specific URL handlers so specific setups take precedence (Moq uses last-registered-wins for `ItExpr.Is<>`).

**NuGet adapter tests:** Use real sub-service instances with mocked HTTP. Pre-populate the cache directory to simulate assembly extraction. Use `SourceLinkExtractorTestHelper.BuildAssemblyWithEmbeddedPdb()` to create in-memory DLLs with PDB + Source Link.

**MCP integration tests:** Wire server in-process with `System.IO.Pipelines`:
```csharp
sc.AddMcpServer()
  .WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream())
  .WithToolsFromAssembly(typeof(DotnetTools).Assembly);
```

**Live tests:** Marked `[Trait("Category", "Live")]`. Guard with `if (!LiveTestsEnabled) return;` (xUnit 2.x has no `Skip.If`). Run with `RESOLVER_RUN_LIVE_TESTS=true dotnet test --filter "Category=Live"`.

## Environment variables

| Variable | Default | Effect |
|---|---|---|
| `GITHUB_TOKEN` | (none) | Avoids 60 req/hr GitHub rate limit |
| `RESOLVER_LOG_LEVEL` | `Warning` | Server/CLI log verbosity |
| `RESOLVER_MAX_SNIPPET_LINES` | `80` | Default snippet length |
| `RESOLVER_CACHE_DIR` | `{temp}/dotnet-source-resolver-cache` | NuGet package download cache |
| `RESOLVER_RUN_LIVE_TESTS` | `false` | Enable live network tests |

## docs/ — what to trust

`docs/design.md` and `docs/implementation-plan.md` are partially stale:

- `design.md` lists `RESOLVER_CACHE_DIR` — **this IS now implemented** (NuGet package cache).
- `design.md` lists `RESOLVER_ENABLE_DECOMPILATION` — **not implemented**.
- `implementation-plan.md` (Slice 4) describes the source.dot.net API as returning JSON from `/api/find?query=` — **wrong**. The real endpoint is `/api/symbols/?symbol=` and returns HTML. The scraping flow in this file and in `SourceDotNetAdapter.cs` is correct.

Trust the code and this file over the docs.

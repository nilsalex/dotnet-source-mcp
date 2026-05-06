# No-Source-Link Fallback Enhancement

## Problem

When a NuGet package lacks embedded Source Link in its PDB (e.g. Newtonsoft.Json, Serilog, ModelContextProtocol), the resolver returns a Medium-confidence result with only a repo root URL — no file path, no line numbers, no snippet. Agents cannot meaningfully use this result without additional tool calls to explore the repo.

## Goal

When the nuspec provides a GitHub repository URL + commit SHA but the assembly lacks Source Link, attempt file-level resolution by combining the existing namespace-to-path heuristic with HEAD validation and the GitHub tree API. Return High confidence with file path, permalink, and snippet when successful.

## Scope

- GitHub repos only (non-GitHub repos keep existing Low-confidence repo root behavior)
- Both fallback points: "no assembly extracted" and "no Source Link in assembly"
- Always attempt tree API regardless of GITHUB_TOKEN presence; gracefully degrade on 403

## Architecture

### Data flow (updated)

```
Phase 1: nuspec → repoMeta (URL + commit)
    │
    ├─ no repoMeta → return null
    │
    ├─ Phase 2: download .nupkg → extract DLL → read PDB → Source Link
    │   │
    │   ├─ Source Link found → Phase 3a/3b → Phase 4 → Phase 5 (unchanged)
    │   │
    │   ├─ No assembly extracted → TryLocateWithoutSourceLinkAsync
    │   │                              ├─ found → BuildResultWithSnippetAsync (High)
    │   │                              └─ not found → BuildRepoRootResult (Medium)
    │   │
    │   └─ No Source Link in assembly → TryLocateWithoutSourceLinkAsync
    │                                    ├─ found → BuildResultWithSnippetAsync (High)
    │                                    └─ not found → BuildRepoRootResult (Medium)
    │
    └─ non-GitHub repos skip TryLocateWithoutSourceLinkAsync → BuildRepoRootResult as before
```

### New method: `TryLocateWithoutSourceLinkAsync`

```csharp
private async Task<SourceFileLocation?> TryLocateWithoutSourceLinkAsync(
    SymbolRequest request,
    RepositoryMetadata repoMeta,
    CancellationToken ct)
```

Algorithm:

1. **Precondition check**: `repoMeta.Url` must be a GitHub repo, `repoMeta.Commit` must be non-empty. Return null otherwise.
2. **Parse repo**: Use existing `TryParseGitHubRepoUrl` to extract owner/repo.
3. **Generate candidates**: Call `SourceLinkMatcher.GuessFilePathsFromSymbol(request.Symbol)` to get candidate file paths (e.g. `Newtonsoft/Json/JsonConvert.cs`, `JsonConvert.cs`).
4. **HEAD-validate candidates**: For each candidate, construct `https://raw.githubusercontent.com/{owner}/{repo}/{commit}/{candidate}` and issue a HEAD request via `_github.SendRawAsync`. Return the first that returns 200.
5. **Tree search fallback**: If all HEAD requests fail (short-circuit on first 200), call `GitHubFileLocator.FindFileAsync(owner, repo, commit, shortFileName)` to search by filename. No `preferredSubPath` is passed (unlike the Source Link path) since we lack Source Link prefix info to derive one.
6. **Build result**: If a file is found (via HEAD or tree search), construct and return a `SourceFileLocation`.
7. **Return null** if nothing found → caller falls back to `BuildRepoRootResult`.

### Refactored method: `BuildResultWithSnippetAsync`

Extract the snippet-fetching logic currently inline at lines 167-199 of NuGetAdapter into a reusable method:

```csharp
private async Task<SourceResult> BuildResultWithSnippetAsync(
    SymbolRequest request,
    SourceFileLocation location,
    CancellationToken ct)
```

This method:
1. If `request.IncludeSnippets`, creates a `GitHubSymbolRequest` and calls `_github.TryResolveAsync`
2. If snippet fetch succeeds, returns the result with `ResolutionKind = NuGet`
3. If snippet fetch fails, falls back to `BuildFileResult`

Both the existing Source Link path and the new no-Source-Link fallback call this method.

### Changes to `TryResolveAsync`

The two early-return blocks that currently call `BuildRepoRootResult` become:

```csharp
// No assembly extracted (line 89)
var fallback = await TryLocateWithoutSourceLinkAsync(request, repoMeta, ct);
if (fallback is not null)
    return await BuildResultWithSnippetAsync(request, fallback, ct);
return BuildRepoRootResult(request, repoMeta, ["NuGetAdapter: no assembly extracted"]);

// No Source Link (line 101)
fallback = await TryLocateWithoutSourceLinkAsync(request, repoMeta, ct);
if (fallback is not null)
    return await BuildResultWithSnippetAsync(request, fallback, ct);
return BuildRepoRootResult(request, repoMeta, ["NuGetAdapter: no Source Link in assembly"]);
```

The inline snippet-fetch block (lines 167-199) is replaced by:

```csharp
return await BuildResultWithSnippetAsync(request, location, ct);
```

### Confidence

Results from `TryLocateWithoutSourceLinkAsync` use the same rules as `BuildFileResult`:
- **High** when commit SHA is present (which it always is in this path, since we require it for URL construction)
- **Medium** if somehow commit is empty

### Error handling

| Failure | Behavior |
|---------|----------|
| HEAD request network error | Skip candidate, try next |
| All HEAD requests 404 | Fall through to tree search |
| Tree API 403 (rate limit) | Log warning, return null → repo root |
| Tree API other failure | Return null → repo root |
| Snippet fetch failure | Return `BuildFileResult` without snippet |

### What stays the same

- Source Link path (Phase 2 → 3 → 4 → 5): completely unchanged
- `SourceLinkMatcher.GuessFilePathsFromSymbol`: reused as-is
- `GitHubFileLocator.FindFileAsync`: reused as-is
- `ValidateOrFallbackAsync`: continues for Source Link path
- `BuildRepoRootResult`: remains terminal fallback for all failure cases
- Non-GitHub repos: unchanged (Low confidence repo root)

## Test plan

### Unit tests (NuGetAdapterTests)

1. **No Source Link, GitHub repo, HEAD hit**: Mock HEAD to return 200 for first candidate. Assert High confidence, correct file path, snippet present when `IncludeSnippets: true`.
2. **No Source Link, GitHub repo, HEAD miss + tree hit**: Mock all HEADs to return 404, tree API returns a path. Assert High confidence, tree-search path in result.
3. **No Source Link, GitHub repo, all miss**: Mock HEADs 404 + tree API null. Assert Medium confidence, repo root URL, empty path.
4. **No Source Link, non-GitHub repo**: Azure DevOps URL. Assert no tree API call, Low confidence, repo root.
5. **No Source Link, no commit SHA**: GitHub URL but empty commit. Assert no HEAD/tree calls, Medium confidence repo root.
6. **No assembly extracted + GitHub**: Same as test 1 but without assembly path — confirms the other entry point works.
7. **Rate limit handling**: Tree API returns 403. Assert graceful fallback to repo root with diagnostic.

### Live tests

Add a live test for `Newtonsoft.Json 13.0.3` that verifies the fallback produces a file-level result (not just repo root). This package is known to lack Source Link.

## Files changed

| File | Change |
|------|--------|
| `src/DotnetSourceResolver.Core/Sources/NuGetAdapter.cs` | Add `TryLocateWithoutSourceLinkAsync`, `BuildResultWithSnippetAsync`; modify two early-return blocks; refactor inline snippet logic |
| `tests/DotnetSourceResolver.Core.Tests/NuGet/NuGetAdapterTests.cs` | Add 7 unit tests for the no-Source-Link fallback |
| `tests/DotnetSourceResolver.Core.Tests/Live/NuGetLiveTests.cs` | Add 1 live test for Newtonsoft.Json fallback |

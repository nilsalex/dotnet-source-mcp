# No-Source-Link Fallback Enhancement — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** When a NuGet package lacks Source Link, use namespace-to-path heuristics + HEAD validation + GitHub tree API to resolve file-level results instead of returning a bare repo root URL.

**Architecture:** Add a `TryLocateWithoutSourceLinkAsync` method to `NuGetAdapter` that runs after Phase 1 (nuspec) succeeds but Phase 2 (Source Link) fails. It generates candidate file paths via `SourceLinkMatcher.GuessFilePathsFromSymbol`, HEAD-validates them against raw.githubusercontent.com, and falls back to `GitHubFileLocator.FindFileAsync`. A refactored `BuildResultWithSnippetAsync` shares snippet-fetch logic between both the Source Link and no-Source-Link paths.

**Tech Stack:** C# 10, .NET 10, xUnit, Moq, System.Net.Http

---

### Task 1: Refactor inline snippet logic into `BuildResultWithSnippetAsync`

**Files:**
- Modify: `src/DotnetSourceResolver.Core/Sources/NuGetAdapter.cs:166-199`

This refactoring extracts the snippet-fetch logic currently duplicated between the Phase 5 inline block and the new fallback path. It must come first so Tasks 2+ can call the new method.

- [ ] **Step 1: Write the `BuildResultWithSnippetAsync` method**

Add this method to `NuGetAdapter.cs` after the `BuildFileResult` method (after line 465):

```csharp
private async Task<SourceResult> BuildResultWithSnippetAsync(
    SymbolRequest request,
    SourceFileLocation location,
    CancellationToken ct)
{
    if (request.IncludeSnippets)
    {
        var ghReq = new GitHubSymbolRequest(
            Symbol: request.Symbol,
            GitHub: new GitHubRequest(
                Repository: location.Repository,
                Commit: location.Commit,
                Path: location.FilePath,
                RawUrl: location.RawUrl,
                StartLine: location.StartLine ?? 1,
                EndLine: location.EndLine ?? int.MaxValue
            ),
            PackageId: request.PackageId,
            PackageVersion: request.PackageVersion,
            AssemblyName: request.AssemblyName,
            TargetFramework: request.TargetFramework,
            IncludeSnippets: true,
            MaxSnippetLines: request.MaxSnippetLines
        );

        try
        {
            var ghResult = await _github.TryResolveAsync(ghReq, ct);
            if (ghResult is not null)
                return ghResult with { ResolutionKind = ResolutionKind.NuGet };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "GitHub snippet fetch failed for {Url}", location.RawUrl);
        }
    }

    return BuildFileResult(request, location);
}
```

- [ ] **Step 2: Replace the inline Phase 5 snippet block with a call to the new method**

Replace lines 166-199 of `NuGetAdapter.cs` (the `if (request.IncludeSnippets)` block and the `return BuildFileResult` fallback) with:

```csharp
return await BuildResultWithSnippetAsync(request, location, ct);
```

- [ ] **Step 3: Run existing tests to verify no regression**

Run: `dotnet test --filter "Category!=Live"`
Expected: All existing tests pass (123 unit tests).

- [ ] **Step 4: Commit**

```bash
git add src/DotnetSourceResolver.Core/Sources/NuGetAdapter.cs
git commit -m "refactor: extract BuildResultWithSnippetAsync from NuGetAdapter Phase 5"
```

---

### Task 2: Implement `TryLocateWithoutSourceLinkAsync`

**Files:**
- Modify: `src/DotnetSourceResolver.Core/Sources/NuGetAdapter.cs`

This is the core new method. It generates candidate paths, HEAD-validates them, and falls back to tree search.

- [ ] **Step 1: Write the `TryLocateWithoutSourceLinkAsync` method**

Add this method to `NuGetAdapter.cs` after the `ValidateOrFallbackAsync` method (after line 367, before `TryParseGitHubRepoUrl`):

```csharp
private async Task<SourceFileLocation?> TryLocateWithoutSourceLinkAsync(
    SymbolRequest request,
    RepositoryMetadata repoMeta,
    CancellationToken ct)
{
    if (repoMeta.Url is null || repoMeta.Commit is null)
        return null;

    if (!TryParseGitHubRepoUrl(repoMeta.Url, out var owner, out var repo))
        return null;

    if (owner is null || repo is null)
        return null;

    var commit = repoMeta.Commit;
    var candidates = SourceLinkMatcher.GuessFilePathsFromSymbol(request.Symbol);

    foreach (var candidate in candidates)
    {
        var rawUrl = $"https://raw.githubusercontent.com/{owner}/{repo}/{commit}/{candidate}";

        bool exists;
        try
        {
            using var headReq = new HttpRequestMessage(HttpMethod.Head, rawUrl);
            using var headResp = await _github.SendRawAsync(headReq, ct);
            exists = headResp.IsSuccessStatusCode;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            continue;
        }

        if (!exists)
            continue;

        _logger.LogInformation(
            "No-Source-Link fallback: HEAD confirmed {Url}",
            rawUrl
        );

        return new SourceFileLocation(
            Repository: repoMeta.Url,
            Commit: commit,
            FilePath: candidate,
            RawUrl: rawUrl
        );
    }

    _logger.LogDebug(
        "No-Source-Link fallback: all HEAD requests failed for {Symbol}, trying tree search",
        request.Symbol
    );

    var shortName = SourceLinkMatcher
        .GuessFilePathsFromSymbol(request.Symbol)
        .Select(p => Path.GetFileName(p))
        .FirstOrDefault();

    if (shortName is null)
        return null;

    var foundPath = await _locator.FindFileAsync(owner, repo, commit, shortName, ct);

    if (foundPath is null)
    {
        _logger.LogDebug(
            "No-Source-Link fallback: tree search found no file named {FileName} in {Owner}/{Repo}@{Commit}",
            shortName,
            owner,
            repo,
            commit
        );
        return null;
    }

    _logger.LogInformation(
        "No-Source-Link fallback: tree search found {FileName} at {Path}",
        shortName,
        foundPath
    );

    var treeRawUrl = $"https://raw.githubusercontent.com/{owner}/{repo}/{commit}/{foundPath}";

    return new SourceFileLocation(
        Repository: repoMeta.Url,
        Commit: commit,
        FilePath: foundPath,
        RawUrl: treeRawUrl
    );
}
```

- [ ] **Step 2: Wire the new method into both fallback points in `TryResolveAsync`**

Replace the "no assembly extracted" early return (around line 89-97):

```csharp
if (assemblyPath is null)
{
    _logger.LogInformation(
        "Could not extract assembly for {PackageId} {Version}, falling back to repo root",
        request.PackageId,
        request.PackageVersion
    );
    return BuildRepoRootResult(request, repoMeta, ["NuGetAdapter: no assembly extracted"]);
}
```

with:

```csharp
if (assemblyPath is null)
{
    _logger.LogInformation(
        "Could not extract assembly for {PackageId} {Version}, attempting no-Source-Link fallback",
        request.PackageId,
        request.PackageVersion
    );
    var noAssemblyFallback = await TryLocateWithoutSourceLinkAsync(request, repoMeta, ct);
    if (noAssemblyFallback is not null)
        return await BuildResultWithSnippetAsync(request, noAssemblyFallback, ct);
    return BuildRepoRootResult(request, repoMeta, ["NuGetAdapter: no assembly extracted"]);
}
```

Replace the "no Source Link" early return (around line 101-112):

```csharp
if (sourceLink is null)
{
    _logger.LogInformation(
        "No Source Link in {AssemblyPath}, falling back to repo root",
        assemblyPath
    );
    return BuildRepoRootResult(
        request,
        repoMeta,
        ["NuGetAdapter: no Source Link in assembly"]
    );
}
```

with:

```csharp
if (sourceLink is null)
{
    _logger.LogInformation(
        "No Source Link in {AssemblyPath}, attempting no-Source-Link fallback",
        assemblyPath
    );
    var noSourceLinkFallback = await TryLocateWithoutSourceLinkAsync(request, repoMeta, ct);
    if (noSourceLinkFallback is not null)
        return await BuildResultWithSnippetAsync(request, noSourceLinkFallback, ct);
    return BuildRepoRootResult(
        request,
        repoMeta,
        ["NuGetAdapter: no Source Link in assembly"]
    );
}
```

- [ ] **Step 3: Build and run all existing tests**

Run: `dotnet test --filter "Category!=Live"`
Expected: All 123 unit tests pass. The existing "RepositoryOnly_ReturnsMediumConfidence" and "NonGitHubRepo_ReturnsLowConfidence" tests may now return different results because the mock `BuildAdapter` sets HEAD → 200 for all GitHub URLs. We need to fix these tests in Task 3.

- [ ] **Step 4: Commit**

```bash
git add src/DotnetSourceResolver.Core/Sources/NuGetAdapter.cs
git commit -m "feat: add TryLocateWithoutSourceLinkAsync for no-Source-Link packages"
```

---

### Task 3: Fix existing tests broken by the new fallback

**Files:**
- Modify: `tests/DotnetSourceResolver.Core.Tests/NuGet/NuGetAdapterTests.cs`

The existing `BuildAdapter` method mocks the GitHubAdapter's HEAD handler to always return 200. This means `TryLocateWithoutSourceLinkAsync` will now find a match via HEAD when previously tests expected Medium-confidence repo root results. We need to adjust the mock to return 404 for raw.githubusercontent.com HEAD requests so the fallback still reaches `BuildRepoRootResult`.

- [ ] **Step 1: Update the GitHubAdapter mock in `BuildAdapter` to distinguish HEAD targets**

Replace the HEAD handler in `BuildAdapter` (lines 119-128):

```csharp
var ghHandler = new Mock<HttpMessageHandler>();
// HEAD → 200 (URL exists, skip tree search)
ghHandler
    .Protected()
    .Setup<Task<HttpResponseMessage>>(
        "SendAsync",
        ItExpr.Is<HttpRequestMessage>(r => r.Method == HttpMethod.Head),
        ItExpr.IsAny<CancellationToken>()
    )
    .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK));
```

with a conditional handler that returns 200 for non-raw URLs (Source Link path) but 404 for raw.githubusercontent.com URLs (no-Source-Link fallback path):

```csharp
var ghHandler = new Mock<HttpMessageHandler>();
ghHandler
    .Protected()
    .Setup<Task<HttpResponseMessage>>(
        "SendAsync",
        ItExpr.Is<HttpRequestMessage>(r => r.Method == HttpMethod.Head),
        ItExpr.IsAny<CancellationToken>()
    )
    .ReturnsAsync(
        (HttpRequestMessage req, CancellationToken _) =>
            req.RequestUri?.Host.Equals(
                "raw.githubusercontent.com",
                StringComparison.OrdinalIgnoreCase
            ) == true
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK)
    );
```

Note: Moq's `ReturnsAsync` with lambda works with the protected `SendAsync` mock because the invocation receives the actual `HttpRequestMessage` argument. If this doesn't compile due to Moq protected API limitations, use a two-setup approach instead — register the 404 setup first (raw.githubusercontent.com), then the 200 setup (general), relying on Moq's last-setup-wins to put the specific 404 first and general 200 as default.

- [ ] **Step 2: Run existing tests to verify they pass**

Run: `dotnet test --filter "Category!=Live" -- NuGetAdapterTests`
Expected: All NuGet adapter tests pass, including `TryResolveAsync_RepositoryOnly_ReturnsMediumConfidence` and `TryResolveAsync_NonGitHubRepo_ReturnsLowConfidence`.

- [ ] **Step 3: Commit**

```bash
git add tests/DotnetSourceResolver.Core.Tests/NuGet/NuGetAdapterTests.cs
git commit -m "test: fix existing NuGet tests for no-Source-Link fallback"
```

---

### Task 4: Write `BuildAdapterWithNoSourceLinkFallback` test helper + HEAD hit test

**Files:**
- Modify: `tests/DotnetSourceResolver.Core.Tests/NuGet/NuGetAdapterTests.cs`

- [ ] **Step 1: Write the `BuildAdapterWithNoSourceLinkFallback` helper**

This helper builds a `NuGetAdapter` where the downloader returns no assembly (triggering the no-Source-Link fallback), with configurable HEAD status codes and optional tree API JSON. Add it after the existing `BuildAdapter` method (after line 189):

```csharp
private static (NuGetAdapter adapter, string cacheDir) BuildAdapterWithNoSourceLinkFallback(
    string cacheDir,
    HttpStatusCode headStatusCode = HttpStatusCode.NotFound,
    string? treeJson = null,
    RepositoryMetadata? repoMeta = null)
{
    var meta = repoMeta ?? ValidRepoMeta;
    var nuspecXml = BuildNuSpecXml(meta);

    var nuspecHttpHandler = new Mock<HttpMessageHandler>();
    nuspecHttpHandler
        .Protected()
        .Setup<Task<HttpResponseMessage>>(
            "SendAsync",
            ItExpr.IsAny<HttpRequestMessage>(),
            ItExpr.IsAny<CancellationToken>()
        )
        .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(nuspecXml),
        });
    var nuspec = new NuSpecRepository(
        new System.Net.Http.HttpClient(nuspecHttpHandler.Object),
        NullLogger<NuSpecRepository>.Instance
    );

    var downloaderHttpHandler = new Mock<HttpMessageHandler>();
    downloaderHttpHandler
        .Protected()
        .Setup<Task<HttpResponseMessage>>(
            "SendAsync",
            ItExpr.IsAny<HttpRequestMessage>(),
            ItExpr.IsAny<CancellationToken>()
        )
        .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.NotFound));
    var downloader = new NuGetPackageDownloader(
        new System.Net.Http.HttpClient(downloaderHttpHandler.Object),
        NullLogger<NuGetPackageDownloader>.Instance,
        new CacheConfiguration { CacheDirectory = cacheDir }
    );

    var ghHandler = new Mock<HttpMessageHandler>();
    ghHandler
        .Protected()
        .Setup<Task<HttpResponseMessage>>(
            "SendAsync",
            ItExpr.Is<HttpRequestMessage>(r => r.Method == HttpMethod.Head),
            ItExpr.IsAny<CancellationToken>()
        )
        .ReturnsAsync(new HttpResponseMessage(headStatusCode));
    ghHandler
        .Protected()
        .Setup<Task<HttpResponseMessage>>(
            "SendAsync",
            ItExpr.Is<HttpRequestMessage>(r => r.Method == HttpMethod.Get),
            ItExpr.IsAny<CancellationToken>()
        )
        .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.NotFound));
    var github = new GitHubAdapter(
        new System.Net.Http.HttpClient(ghHandler.Object),
        NullLogger<GitHubAdapter>.Instance
    );

    var locatorHandler = new Mock<HttpMessageHandler>();
    if (treeJson is not null)
    {
        locatorHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(treeJson),
            });
    }
    else
    {
        locatorHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
    var locator = new GitHubFileLocator(
        new System.Net.Http.HttpClient(locatorHandler.Object),
        NullLogger<GitHubFileLocator>.Instance
    );

    var adapter = new NuGetAdapter(
        nuspec,
        downloader,
        new SourceLinkExtractor(NullLogger<SourceLinkExtractor>.Instance),
        new SourceLinkMatcher(NullLogger<SourceLinkMatcher>.Instance),
        locator,
        github,
        NullLogger<NuGetAdapter>.Instance
    );

    return (adapter, cacheDir);
}
```

- [ ] **Step 2: Write the HEAD hit test**

Add this test in the "Phase 1 fallback" section of `NuGetAdapterTests.cs`, after the existing `TryResolveAsync_NonGitHubRepo_ReturnsLowConfidence` test:

```csharp
[Fact]
public async Task TryResolveAsync_NoSourceLink_HeadHit_ReturnsHighConfidence()
{
    var cacheDir = Path.Combine(
        Path.GetTempPath(),
        $"nuget-nosl-headhit-{Guid.NewGuid()}"
    );
    try
    {
        var (adapter, _) = BuildAdapterWithNoSourceLinkFallback(
            cacheDir,
            headStatusCode: HttpStatusCode.OK,
            treeJson: null
        );

        var result = await adapter.TryResolveAsync(MakeRequest(), default);

        Assert.NotNull(result);
        Assert.True(result.Resolved);
        Assert.Equal(ResolutionConfidence.High, result.Confidence);
        Assert.Single(result.Sources);
        Assert.NotEmpty(result.Sources[0].Path);
        Assert.NotEmpty(result.Sources[0].Url);
        Assert.Contains("github.com", result.Sources[0].Url);
        Assert.DoesNotContain("/tree/", result.Sources[0].Url);
    }
    finally
    {
        if (Directory.Exists(cacheDir))
            Directory.Delete(cacheDir, recursive: true);
    }
}
```

- [ ] **Step 3: Run the new test**

Run: `dotnet test --filter "Category!=Live" -- TryResolveAsync_NoSourceLink_HeadHit_ReturnsHighConfidence`
Expected: PASS

- [ ] **Step 4: Commit**

```bash
git add tests/DotnetSourceResolver.Core.Tests/NuGet/NuGetAdapterTests.cs
git commit -m "test: add BuildAdapterWithNoSourceLinkFallback helper and HeadHit test"
```

---

### Task 5: Unit test — HEAD miss, tree search hit

**Files:**
- Modify: `tests/DotnetSourceResolver.Core.Tests/NuGet/NuGetAdapterTests.cs`

- [ ] **Step 1: Write the test**

```csharp
[Fact]
public async Task TryResolveAsync_NoSourceLink_HeadMissTreeHit_ReturnsHighConfidence()
{
    var cacheDir = Path.Combine(
        Path.GetTempPath(),
        $"nuget-nosl-treehit-{Guid.NewGuid()}"
    );
    try
    {
        var treeJson = """
            {
              "sha": "abc",
              "tree": [
                { "path": "src/Shared/DefaultUserService.cs", "type": "blob" },
                { "path": "test/DefaultUserServiceTests.cs", "type": "blob" }
              ]
            }
            """;

        var (adapter, _) = BuildAdapterWithNoSourceLinkFallback(
            cacheDir,
            headStatusCode: HttpStatusCode.NotFound,
            treeJson: treeJson
        );

        var result = await adapter.TryResolveAsync(MakeRequest(), default);

        Assert.NotNull(result);
        Assert.True(result.Resolved);
        Assert.Equal(ResolutionConfidence.High, result.Confidence);
        Assert.Single(result.Sources);
        Assert.NotEmpty(result.Sources[0].Path);
        Assert.Contains("DefaultUserService.cs", result.Sources[0].Path);
    }
    finally
    {
        if (Directory.Exists(cacheDir))
            Directory.Delete(cacheDir, recursive: true);
    }
}
```

- [ ] **Step 2: Run the new test**

Run: `dotnet test --filter "Category!=Live" -- TryResolveAsync_NoSourceLink_HeadMissTreeHit_ReturnsHighConfidence`
Expected: PASS

- [ ] **Step 3: Commit**

```bash
git add tests/DotnetSourceResolver.Core.Tests/NuGet/NuGetAdapterTests.cs
git commit -m "test: add NoSourceLink_HeadMissTreeHit unit test"
```

---

### Task 6: Unit tests — all miss, non-GitHub, no commit, rate limit

**Files:**
- Modify: `tests/DotnetSourceResolver.Core.Tests/NuGet/NuGetAdapterTests.cs`

- [ ] **Step 1: Write the all-miss test**

```csharp
[Fact]
public async Task TryResolveAsync_NoSourceLink_AllMiss_ReturnsMediumConfidence()
{
    var cacheDir = Path.Combine(
        Path.GetTempPath(),
        $"nuget-nosl-allmiss-{Guid.NewGuid()}"
    );
    try
    {
        var (adapter, _) = BuildAdapterWithNoSourceLinkFallback(
            cacheDir,
            headStatusCode: HttpStatusCode.NotFound,
            treeJson: null
        );

        var result = await adapter.TryResolveAsync(MakeRequest(), default);

        Assert.NotNull(result);
        Assert.True(result.Resolved);
        Assert.Equal(ResolutionConfidence.Medium, result.Confidence);
        Assert.Single(result.Sources);
        Assert.Empty(result.Sources[0].Path);
        Assert.Contains("/tree/", result.Sources[0].Url);
    }
    finally
    {
        if (Directory.Exists(cacheDir))
            Directory.Delete(cacheDir, recursive: true);
    }
}
```

- [ ] **Step 2: Write the non-GitHub test**

```csharp
[Fact]
public async Task TryResolveAsync_NoSourceLink_NonGitHubRepo_ReturnsLowConfidence()
{
    var cacheDir = Path.Combine(
        Path.GetTempPath(),
        $"nuget-nosl-nongh-{Guid.NewGuid()}"
    );
    try
    {
        var azureMeta = new RepositoryMetadata(
            Url: "https://dev.azure.com/myorg/myproject",
            Commit: "abc123",
            Branch: null,
            Type: "tfsgit"
        );

        var (adapter, _) = BuildAdapterWithNoSourceLinkFallback(
            cacheDir,
            headStatusCode: HttpStatusCode.NotFound,
            treeJson: null,
            repoMeta: azureMeta
        );

        var result = await adapter.TryResolveAsync(MakeRequest(), default);

        Assert.NotNull(result);
        Assert.Equal(ResolutionConfidence.Low, result.Confidence);
        Assert.Empty(result.Sources[0].Path);
    }
    finally
    {
        if (Directory.Exists(cacheDir))
            Directory.Delete(cacheDir, recursive: true);
    }
}
```

- [ ] **Step 3: Write the no-commit test**

```csharp
[Fact]
public async Task TryResolveAsync_NoSourceLink_NoCommit_ReturnsMediumConfidence()
{
    var cacheDir = Path.Combine(
        Path.GetTempPath(),
        $"nuget-nosl-nocommit-{Guid.NewGuid()}"
    );
    try
    {
        var noCommitMeta = new RepositoryMetadata(
            Url: RepoUrl,
            Commit: null,
            Branch: null,
            Type: "git"
        );

        var (adapter, _) = BuildAdapterWithNoSourceLinkFallback(
            cacheDir,
            headStatusCode: HttpStatusCode.NotFound,
            treeJson: null,
            repoMeta: noCommitMeta
        );

        var result = await adapter.TryResolveAsync(MakeRequest(), default);

        Assert.NotNull(result);
        Assert.Equal(ResolutionConfidence.Medium, result.Confidence);
        Assert.Empty(result.Sources[0].Path);
    }
    finally
    {
        if (Directory.Exists(cacheDir))
            Directory.Delete(cacheDir, recursive: true);
    }
}
```

- [ ] **Step 4: Write the rate-limit test**

For the rate-limit test, the tree API handler needs to return 403. This requires a custom adapter build since the `BuildAdapterWithNoSourceLinkFallback` helper configures the locator handler based on `treeJson` only. Build the adapter inline for this one test:

```csharp
[Fact]
public async Task TryResolveAsync_NoSourceLink_TreeRateLimited_ReturnsMediumConfidence()
{
    var cacheDir = Path.Combine(
        Path.GetTempPath(),
        $"nuget-nosl-ratelimit-{Guid.NewGuid()}"
    );
    try
    {
        var nuspecXml = BuildNuSpecXml(ValidRepoMeta);
        var nuspecHttpHandler = new Mock<HttpMessageHandler>();
        nuspecHttpHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(nuspecXml),
            });
        var nuspec = new NuSpecRepository(
            new System.Net.Http.HttpClient(nuspecHttpHandler.Object),
            NullLogger<NuSpecRepository>.Instance
        );

        var downloaderHttpHandler = new Mock<HttpMessageHandler>();
        downloaderHttpHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.NotFound));
        var downloader = new NuGetPackageDownloader(
            new System.Net.Http.HttpClient(downloaderHttpHandler.Object),
            NullLogger<NuGetPackageDownloader>.Instance,
            new CacheConfiguration { CacheDirectory = cacheDir }
        );

        var ghHandler = new Mock<HttpMessageHandler>();
        ghHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(r => r.Method == HttpMethod.Head),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.NotFound));
        ghHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(r => r.Method == HttpMethod.Get),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.NotFound));
        var github = new GitHubAdapter(
            new System.Net.Http.HttpClient(ghHandler.Object),
            NullLogger<GitHubAdapter>.Instance
        );

        var locatorHandler = new Mock<HttpMessageHandler>();
        locatorHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(new HttpResponseMessage((HttpStatusCode)403));
        var locator = new GitHubFileLocator(
            new System.Net.Http.HttpClient(locatorHandler.Object),
            NullLogger<GitHubFileLocator>.Instance
        );

        var adapter = new NuGetAdapter(
            nuspec,
            downloader,
            new SourceLinkExtractor(NullLogger<SourceLinkExtractor>.Instance),
            new SourceLinkMatcher(NullLogger<SourceLinkMatcher>.Instance),
            locator,
            github,
            NullLogger<NuGetAdapter>.Instance
        );

        var result = await adapter.TryResolveAsync(MakeRequest(), default);

        Assert.NotNull(result);
        Assert.Equal(ResolutionConfidence.Medium, result.Confidence);
        Assert.Empty(result.Sources[0].Path);
    }
    finally
    {
        if (Directory.Exists(cacheDir))
            Directory.Delete(cacheDir, recursive: true);
    }
}
```

- [ ] **Step 5: Run all new tests**

Run: `dotnet test --filter "Category!=Live" -- NuGetAdapterTests`
Expected: All NuGet adapter tests pass (existing + new).

- [ ] **Step 6: Commit**

```bash
git add tests/DotnetSourceResolver.Core.Tests/NuGet/NuGetAdapterTests.cs
git commit -m "test: add no-Source-Link fallback unit tests (all-miss, non-GitHub, no-commit, rate-limit)"
```

---

### Task 7: Unit test — snippet fetch with no-Source-Link fallback

**Files:**
- Modify: `tests/DotnetSourceResolver.Core.Tests/NuGet/NuGetAdapterTests.cs`

- [ ] **Step 1: Write the test**

```csharp
[Fact]
public async Task TryResolveAsync_NoSourceLink_WithSnippets_FetchesFromGitHub()
{
    var cacheDir = Path.Combine(
        Path.GetTempPath(),
        $"nuget-nosl-snippet-{Guid.NewGuid()}"
    );
    try
    {
        var nuspecXml = BuildNuSpecXml(ValidRepoMeta);
        var nuspecHttpHandler = new Mock<HttpMessageHandler>();
        nuspecHttpHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(nuspecXml),
            });
        var nuspec = new NuSpecRepository(
            new System.Net.Http.HttpClient(nuspecHttpHandler.Object),
            NullLogger<NuSpecRepository>.Instance
        );

        var downloaderHttpHandler = new Mock<HttpMessageHandler>();
        downloaderHttpHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.NotFound));
        var downloader = new NuGetPackageDownloader(
            new System.Net.Http.HttpClient(downloaderHttpHandler.Object),
            NullLogger<NuGetPackageDownloader>.Instance,
            new CacheConfiguration { CacheDirectory = cacheDir }
        );

        var ghHandler = new Mock<HttpMessageHandler>();
        ghHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(r => r.Method == HttpMethod.Head),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK));
        ghHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(r => r.Method == HttpMethod.Get),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("line1\nline2\nline3"),
            });
        var github = new GitHubAdapter(
            new System.Net.Http.HttpClient(ghHandler.Object),
            NullLogger<GitHubAdapter>.Instance
        );

        var locatorHandler = new Mock<HttpMessageHandler>();
        locatorHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.NotFound));
        var locator = new GitHubFileLocator(
            new System.Net.Http.HttpClient(locatorHandler.Object),
            NullLogger<GitHubFileLocator>.Instance
        );

        var adapter = new NuGetAdapter(
            nuspec,
            downloader,
            new SourceLinkExtractor(NullLogger<SourceLinkExtractor>.Instance),
            new SourceLinkMatcher(NullLogger<SourceLinkMatcher>.Instance),
            locator,
            github,
            NullLogger<NuGetAdapter>.Instance
        );

        var result = await adapter.TryResolveAsync(MakeRequest(includeSnippets: true), default);

        Assert.NotNull(result);
        Assert.True(result.Resolved);
        Assert.Equal(ResolutionConfidence.High, result.Confidence);
        Assert.Single(result.Snippets);
    }
    finally
    {
        if (Directory.Exists(cacheDir))
            Directory.Delete(cacheDir, recursive: true);
    }
}
```

- [ ] **Step 2: Run the new test**

Run: `dotnet test --filter "Category!=Live" -- TryResolveAsync_NoSourceLink_WithSnippets_FetchesFromGitHub`
Expected: PASS

- [ ] **Step 3: Commit**

```bash
git add tests/DotnetSourceResolver.Core.Tests/NuGet/NuGetAdapterTests.cs
git commit -m "test: add NoSourceLink_WithSnippets unit test"
```

---

### Task 8: Live test for Newtonsoft.Json

**Files:**
- Modify: `tests/DotnetSourceResolver.Core.Tests/Live/NuGetLiveTests.cs`

This verifies the full pipeline against a real package that lacks Source Link.

- [ ] **Step 1: Update the existing Newtonsoft.Json live test to assert file-level resolution**

Replace the existing `NuGetAdapter_NewtonsoftJson_JsonConvert_Resolves` test (line 174-194) with:

```csharp
[Fact]
public async Task NuGetAdapter_NewtonsoftJson_JsonConvert_ResolvesToFileLevel()
{
    if (!LiveTestsEnabled)
        return;

    var adapter = BuildLiveNuGetAdapter();
    var request = new SymbolRequest(
        Symbol: "Newtonsoft.Json.JsonConvert",
        PackageId: "Newtonsoft.Json",
        PackageVersion: "13.0.3",
        IncludeSnippets: true
    );

    var result = await adapter.TryResolveAsync(request, default);

    Assert.NotNull(result);
    Assert.True(result.Resolved);
    Assert.Equal(ResolutionKind.NuGet, result.ResolutionKind);
    Assert.NotEmpty(result.Sources);
    Assert.NotEmpty(result.Sources[0].Path);
    Assert.Contains("JsonConvert", result.Sources[0].Path);
    Assert.Single(result.Snippets);
}
```

- [ ] **Step 2: Run live tests (if GITHUB_TOKEN is set)**

Run: `RESOLVER_RUN_LIVE_TESTS=true dotnet test --filter "Category=Live" -- NuGetAdapter_NewtonsoftJson_JsonConvert_ResolvesToFileLevel`
Expected: PASS — Newtonsoft.Json now resolves to a file-level URL with a snippet instead of a repo root.

- [ ] **Step 3: Commit**

```bash
git add tests/DotnetSourceResolver.Core.Tests/Live/NuGetLiveTests.cs
git commit -m "test: update Newtonsoft.Json live test to assert file-level resolution"
```

---

### Task 9: Full test suite run and format

**Files:** None (verification only)

- [ ] **Step 1: Run the complete unit test suite**

Run: `dotnet test --filter "Category!=Live"`
Expected: All tests pass (123 + new ~7 = ~130 tests).

- [ ] **Step 2: Run the formatter**

Run: `dotnet csharpier format .`
Expected: No changes or only whitespace normalization.

- [ ] **Step 3: Build the full solution**

Run: `dotnet build`
Expected: No errors, no warnings.

- [ ] **Step 4: Commit any formatting changes**

```bash
git add -A
git commit -m "chore: format after no-Source-Link fallback implementation"
```

(Only if there are formatting changes; skip if clean.)

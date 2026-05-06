using System.IO.Compression;
using DotnetSourceResolver.Core.Models.NuGet;
using Microsoft.Extensions.Logging;

namespace DotnetSourceResolver.Core.NuGet;

/// <summary>
/// Downloads .nupkg files from the NuGet v3 flat-container API, extracts the relevant
/// DLL for a given target framework, and caches the result to disk.
/// </summary>
public class NuGetPackageDownloader
{
    private readonly HttpClient _http;
    private readonly ILogger<NuGetPackageDownloader> _logger;
    private readonly string _cacheDir;

    // TFM order for "best lower or equal" matching: later entries are preferred.
    // This list only needs to cover what NuGet packages commonly ship.
    private static readonly string[] TfmPrecedence =
    [
        "netstandard1.0",
        "netstandard1.1",
        "netstandard1.2",
        "netstandard1.3",
        "netstandard1.4",
        "netstandard1.5",
        "netstandard1.6",
        "netstandard2.0",
        "netstandard2.1",
        "net45",
        "net451",
        "net452",
        "net46",
        "net461",
        "net462",
        "net47",
        "net471",
        "net472",
        "net48",
        "net481",
        "net5.0",
        "net6.0",
        "net7.0",
        "net8.0",
        "net9.0",
        "net10.0",
    ];

    public NuGetPackageDownloader(
        HttpClient http,
        ILogger<NuGetPackageDownloader> logger,
        CacheConfiguration cacheConfig
    )
    {
        _http = http;
        _logger = logger;
        _cacheDir = cacheConfig.CacheDirectory;
    }

    /// <summary>
    /// Downloads the .nupkg for the given package, extracts the best-matching DLL
    /// for <paramref name="targetFramework"/>, caches it, and returns the local path.
    /// Returns null if the package is not found or contains no managed DLL.
    /// </summary>
    public async Task<string?> DownloadAndExtractAssemblyAsync(
        string packageId,
        string packageVersion,
        string? targetFramework,
        CancellationToken ct
    )
    {
        var id = packageId.ToLowerInvariant();
        var version = packageVersion.ToLowerInvariant();
        var tfm = targetFramework?.ToLowerInvariant();

        // Check cache first
        var cacheKey = tfm ?? "best";
        var cacheFolder = Path.Combine(_cacheDir, "nuget-packages", id, version, cacheKey);
        var assemblyName = $"{packageId}.dll";
        var cachedPath = Path.Combine(cacheFolder, assemblyName);

        if (File.Exists(cachedPath))
        {
            _logger.LogDebug("Cache hit for {PackageId} {Version} {Tfm}", id, version, cacheKey);
            return cachedPath;
        }

        // Download .nupkg
        var nupkgUrl =
            $"https://api.nuget.org/v3-flatcontainer/{id}/{version}/{id}.{version}.nupkg";

        _logger.LogInformation("Downloading {Url}", nupkgUrl);

        byte[] nupkgBytes;
        try
        {
            var response = await _http.GetAsync(nupkgUrl, ct);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _logger.LogWarning(
                    "Package not found: {PackageId} {Version}",
                    packageId,
                    packageVersion
                );
                return null;
            }

            response.EnsureSuccessStatusCode();
            nupkgBytes = await response.Content.ReadAsByteArrayAsync(ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to download package {PackageId} {Version}",
                packageId,
                packageVersion
            );
            return null;
        }

        // Extract from zip
        using var zip = new ZipArchive(new MemoryStream(nupkgBytes), ZipArchiveMode.Read);
        var dllPaths = zip
            .Entries.Where(e =>
                e.FullName.StartsWith("lib/", StringComparison.OrdinalIgnoreCase)
                && e.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            )
            .Select(e => e.FullName)
            .ToList();

        if (dllPaths.Count == 0)
        {
            _logger.LogWarning(
                "No DLLs found in lib/ for {PackageId} {Version}",
                packageId,
                packageVersion
            );
            return null;
        }

        var bestDllPath = SelectBestDll(dllPaths, tfm);
        if (bestDllPath is null)
        {
            _logger.LogWarning(
                "No suitable DLL found for TFM {Tfm} in {PackageId} {Version}",
                tfm,
                packageId,
                packageVersion
            );
            return null;
        }

        // Extract to cache
        var entry = zip.GetEntry(bestDllPath);
        if (entry is null)
            return null;

        Directory.CreateDirectory(cacheFolder);

        await using var entryStream = entry.Open();
        await using var fileStream = File.Create(cachedPath);
        await entryStream.CopyToAsync(fileStream, ct);

        _logger.LogInformation("Extracted {DllPath} to {CachedPath}", bestDllPath, cachedPath);
        return cachedPath;
    }

    /// <summary>
    /// Selects the best DLL from lib/ entries for the requested TFM.
    /// Priority: exact match > nearest lower/compatible TFM > highest available.
    /// </summary>
    internal static string? SelectBestDll(IEnumerable<string> dllPaths, string? targetFramework)
    {
        // lib/{tfm}/{name}.dll → extract tfm segment
        var entries = dllPaths
            .Select(p =>
            {
                var parts = p.Split('/');
                if (parts.Length != 3)
                    return (tfm: (string?)null, path: p);
                return (tfm: parts[1].ToLowerInvariant(), path: p);
            })
            .Where(e => e.tfm is not null)
            .ToList();

        if (entries.Count == 0)
            return null;

        // Exact match
        if (targetFramework is not null)
        {
            var exact = entries.FirstOrDefault(e => e.tfm == targetFramework.ToLowerInvariant());
            if (exact.path is not null)
                return exact.path;
        }

        // Use precedence list to find the best compatible TFM
        var available = entries
            .Select(e => e.tfm!)
            .Distinct()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (targetFramework is not null)
        {
            var requested = targetFramework.ToLowerInvariant();
            var requestedIdx = Array.IndexOf(TfmPrecedence, requested);

            // Find the highest-precedence TFM that is ≤ requested
            var bestIdx = -1;
            foreach (var tfm in available)
            {
                var idx = Array.IndexOf(TfmPrecedence, tfm);
                if (idx < 0)
                    continue;
                if (requestedIdx >= 0 && idx > requestedIdx)
                    continue; // higher than requested, skip
                if (idx > bestIdx)
                    bestIdx = idx;
            }

            if (bestIdx >= 0)
            {
                var bestTfm = TfmPrecedence[bestIdx];
                return entries.First(e => e.tfm == bestTfm).path;
            }

            // No compatible lower TFM — fall through to return highest available
        }

        // Return the highest available TFM (largest index in TfmPrecedence, or first entry)
        var highestIdx = -1;
        string? highestTfm = null;
        foreach (var tfm in available)
        {
            var idx = Array.IndexOf(TfmPrecedence, tfm);
            if (idx > highestIdx)
            {
                highestIdx = idx;
                highestTfm = tfm;
            }
        }

        if (highestTfm is not null)
            return entries.First(e => e.tfm == highestTfm).path;

        // Unknown TFM strings — just return first
        return entries.First().path;
    }
}

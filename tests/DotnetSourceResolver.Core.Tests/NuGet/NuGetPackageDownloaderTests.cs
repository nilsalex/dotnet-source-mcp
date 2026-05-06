using System.IO.Compression;
using System.Net;
using DotnetSourceResolver.Core.Models.NuGet;
using DotnetSourceResolver.Core.NuGet;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;

namespace DotnetSourceResolver.Core.Tests.NuGet;

public class NuGetPackageDownloaderTests
{
    // -------------------------------------------------------------------------
    // SelectBestDll — static helper
    // -------------------------------------------------------------------------

    [Fact]
    public void SelectBestDll_ExactTfm_ReturnsExact()
    {
        var dlls = new[]
        {
            "lib/net6.0/MyLib.dll",
            "lib/net8.0/MyLib.dll",
            "lib/net10.0/MyLib.dll",
        };

        var result = NuGetPackageDownloader.SelectBestDll(dlls, "net8.0");

        Assert.Equal("lib/net8.0/MyLib.dll", result);
    }

    [Fact]
    public void SelectBestDll_NoExact_ReturnsNearestLower()
    {
        var dlls = new[] { "lib/net6.0/MyLib.dll", "lib/net8.0/MyLib.dll" };

        // Request net9.0 — nearest lower is net8.0
        var result = NuGetPackageDownloader.SelectBestDll(dlls, "net9.0");

        Assert.Equal("lib/net8.0/MyLib.dll", result);
    }

    [Fact]
    public void SelectBestDll_OnlyHigherTfm_ReturnsHighest()
    {
        var dlls = new[] { "lib/net8.0/MyLib.dll", "lib/net9.0/MyLib.dll" };

        // Request net6.0 — no lower available → fall through to highest
        var result = NuGetPackageDownloader.SelectBestDll(dlls, "net6.0");

        Assert.Equal("lib/net9.0/MyLib.dll", result);
    }

    [Fact]
    public void SelectBestDll_NullTfm_ReturnsHighestAvailable()
    {
        var dlls = new[]
        {
            "lib/netstandard2.0/MyLib.dll",
            "lib/net8.0/MyLib.dll",
            "lib/net10.0/MyLib.dll",
        };

        var result = NuGetPackageDownloader.SelectBestDll(dlls, null);

        Assert.Equal("lib/net10.0/MyLib.dll", result);
    }

    [Fact]
    public void SelectBestDll_EmptyList_ReturnsNull()
    {
        var result = NuGetPackageDownloader.SelectBestDll([], "net8.0");

        Assert.Null(result);
    }

    [Fact]
    public void SelectBestDll_NetstandardFallback()
    {
        // Package only ships netstandard2.0
        var dlls = new[] { "lib/netstandard2.0/MyLib.dll" };

        var result = NuGetPackageDownloader.SelectBestDll(dlls, "net10.0");

        Assert.Equal("lib/netstandard2.0/MyLib.dll", result);
    }

    // -------------------------------------------------------------------------
    // DownloadAndExtractAssemblyAsync — mocked HTTP
    // -------------------------------------------------------------------------

    private static byte[] BuildFakeNupkg(string dllEntryPath)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry(dllEntryPath);
            using var stream = entry.Open();
            // Write a tiny fake DLL payload (actual bytes don't matter for these tests)
            stream.WriteByte(0x4D); // 'M'
            stream.WriteByte(0x5A); // 'Z'
        }

        return ms.ToArray();
    }

    private NuGetPackageDownloader BuildDownloader(
        HttpResponseMessage response,
        string? cacheDir = null
    )
    {
        var handler = new Mock<HttpMessageHandler>();
        handler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(response);

        return new NuGetPackageDownloader(
            new HttpClient(handler.Object),
            NullLogger<NuGetPackageDownloader>.Instance,
            new CacheConfiguration
            {
                CacheDirectory =
                    cacheDir ?? Path.Combine(Path.GetTempPath(), $"test-cache-{Guid.NewGuid()}"),
            }
        );
    }

    [Fact]
    public async Task DownloadAndExtractAssemblyAsync_MockedNupkg_ExtractsDll()
    {
        var cacheDir = Path.Combine(Path.GetTempPath(), $"test-cache-{Guid.NewGuid()}");
        var nupkgBytes = BuildFakeNupkg("lib/net8.0/Duende.BFF.dll");

        var downloader = BuildDownloader(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(nupkgBytes),
            },
            cacheDir
        );

        var result = await downloader.DownloadAndExtractAssemblyAsync(
            "Duende.BFF",
            "3.1.0",
            "net8.0",
            default
        );

        Assert.NotNull(result);
        Assert.True(File.Exists(result), $"Expected extracted DLL at {result}");

        // Cleanup
        Directory.Delete(cacheDir, recursive: true);
    }

    [Fact]
    public async Task DownloadAndExtractAssemblyAsync_404_ReturnsNull()
    {
        var downloader = BuildDownloader(new HttpResponseMessage(HttpStatusCode.NotFound));

        var result = await downloader.DownloadAndExtractAssemblyAsync(
            "Unknown.Package",
            "9.9.9",
            "net8.0",
            default
        );

        Assert.Null(result);
    }

    [Fact]
    public async Task DownloadAndExtractAssemblyAsync_NoDllsInPackage_ReturnsNull()
    {
        // Create a .nupkg with no lib/ entries
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("content/readme.txt");
            using var stream = entry.Open();
            stream.WriteByte(0x41);
        }

        var downloader = BuildDownloader(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(ms.ToArray()),
            }
        );

        var result = await downloader.DownloadAndExtractAssemblyAsync(
            "SomePackage",
            "1.0.0",
            "net8.0",
            default
        );

        Assert.Null(result);
    }

    [Fact]
    public async Task DownloadAndExtractAssemblyAsync_CacheHitSkipsHttp()
    {
        var cacheDir = Path.Combine(Path.GetTempPath(), $"test-cache-{Guid.NewGuid()}");

        // Pre-populate cache
        var cachedFolder = Path.Combine(cacheDir, "nuget-packages", "mylib", "1.0.0", "net8.0");
        Directory.CreateDirectory(cachedFolder);
        var cachedFile = Path.Combine(cachedFolder, "MyLib.dll");
        await File.WriteAllBytesAsync(cachedFile, [0x4D, 0x5A]);

        // HTTP should never be called — use a handler that throws
        var handler = new Mock<HttpMessageHandler>();
        handler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ThrowsAsync(new Exception("HTTP should not be called for a cache hit"));

        var downloader = new NuGetPackageDownloader(
            new HttpClient(handler.Object),
            NullLogger<NuGetPackageDownloader>.Instance,
            new CacheConfiguration { CacheDirectory = cacheDir }
        );

        var result = await downloader.DownloadAndExtractAssemblyAsync(
            "MyLib",
            "1.0.0",
            "net8.0",
            default
        );

        Assert.Equal(cachedFile, result);

        Directory.Delete(cacheDir, recursive: true);
    }
}

namespace DotnetSourceResolver.Core.Models.NuGet;

/// <summary>
/// Configuration for the NuGet package download cache.
/// Populated from the RESOLVER_CACHE_DIR environment variable.
/// </summary>
public sealed class CacheConfiguration
{
    public string CacheDirectory { get; init; } =
        Path.Combine(Path.GetTempPath(), "dotnet-source-resolver-cache");
}

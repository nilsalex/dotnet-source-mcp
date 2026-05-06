namespace DotnetSourceResolver.Core.Models.NuGet;

/// <summary>
/// Repository metadata extracted from a .nuspec file.
/// </summary>
public sealed record RepositoryMetadata(
    string? Url,
    string? Commit,
    string? Branch,
    /// <summary>"git", "tfsgit", etc.</summary>
    string? Type
);

namespace DotnetSourceResolver.Core.Models.NuGet;

/// <summary>
/// A resolved source file location derived from Source Link data.
/// </summary>
public sealed record SourceFileLocation(
    string Repository,
    string Commit,
    string FilePath,
    string RawUrl,
    int? StartLine = null,
    int? EndLine = null
);

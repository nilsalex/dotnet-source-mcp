namespace DotnetSourceResolver.Core.Models;

public enum ResolutionConfidence
{
    /// <summary>Exact commit SHA known; source fetched from a GitHub permalink.</summary>
    High,

    /// <summary>Source found via source.dot.net or docs but without an exact commit SHA.</summary>
    Medium,

    /// <summary>Only a file URL without precise line-level location, or decompiled fallback.</summary>
    Low,
}

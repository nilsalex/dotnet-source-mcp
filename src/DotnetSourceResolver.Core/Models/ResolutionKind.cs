namespace DotnetSourceResolver.Core.Models;

public enum ResolutionKind
{
    /// <summary>Resolved via source.dot.net.</summary>
    SourceDotNet,

    /// <summary>Resolved via the Microsoft docs (learn.microsoft.com) source link.</summary>
    Docs,

    /// <summary>Resolved via a direct GitHub permalink (used when the adapter already knows the URL).</summary>
    GitHub,

    /// <summary>Resolved via NuGet package metadata and/or Source Link in the package's PDB.</summary>
    NuGet,

    /// <summary>Could not be resolved by any adapter.</summary>
    Unresolved,
}

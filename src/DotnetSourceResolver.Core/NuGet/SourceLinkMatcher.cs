using System.Text.RegularExpressions;
using DotnetSourceResolver.Core.Models.NuGet;
using Microsoft.Extensions.Logging;

namespace DotnetSourceResolver.Core.NuGet;

/// <summary>
/// Maps a .NET symbol name to a source file URL using Source Link document patterns.
/// Uses heuristics: namespace segments → directory path, symbol short name → file name.
/// </summary>
public class SourceLinkMatcher
{
    private readonly ILogger<SourceLinkMatcher> _logger;

    // Matches generic arity suffix: Dictionary`2, List`1, etc.
    private static readonly Regex GenericArity = new(@"`\d+$", RegexOptions.Compiled);

    // Matches angle-bracket generics in the symbol name: Foo<T, U>
    private static readonly Regex AngleBracketGenerics = new(@"<[^>]*>", RegexOptions.Compiled);

    // A Source Link wildcard pattern ends with /* → the * is a path wildcard
    // Local pattern: C:\src\Project\*   URL pattern: https://raw.github.com/.../src/*
    // The * in the local path matches the same relative sub-path as the * in the URL.

    public SourceLinkMatcher(ILogger<SourceLinkMatcher> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Attempts to match <paramref name="symbol"/> to a source file URL using the provided
    /// <paramref name="sourceLink"/> document. Returns null if no plausible match is found.
    /// </summary>
    public SourceFileLocation? Match(
        string symbol,
        SourceLinkDocument sourceLink,
        string repositoryUrl,
        string commit
    )
    {
        var candidates = GuessFilePathsFromSymbol(symbol);

        foreach (var candidate in candidates)
        {
            var resolvedUrl = ResolveSourceLinkPattern(candidate, sourceLink.Documents);
            if (resolvedUrl is null)
                continue;

            // Derive repo/commit/filePath from the raw URL
            var (repo, resolvedCommit, filePath) = ParseRawGitHubUrl(resolvedUrl);
            var effectiveRepo = repo ?? repositoryUrl;
            var effectiveCommit = resolvedCommit ?? commit;

            _logger.LogDebug(
                "Symbol {Symbol} matched to {Url} via candidate {Candidate}",
                symbol,
                resolvedUrl,
                candidate
            );

            return new SourceFileLocation(
                Repository: effectiveRepo,
                Commit: effectiveCommit,
                FilePath: filePath ?? candidate,
                RawUrl: resolvedUrl
            );
        }

        _logger.LogDebug(
            "No Source Link pattern matched any candidate path for symbol {Symbol}",
            symbol
        );
        return null;
    }

    /// <summary>
    /// Generates candidate file paths for a symbol by converting its namespace
    /// segments to directory separators and its short name to a .cs filename.
    /// Returns multiple candidates for common naming conventions.
    /// </summary>
    internal static IEnumerable<string> GuessFilePathsFromSymbol(string symbol)
    {
        // Strip generics: "Dictionary<TKey, TValue>" → "Dictionary"
        var clean = AngleBracketGenerics.Replace(symbol, "");
        clean = GenericArity.Replace(clean, "");

        // Strip method/property suffixes: "Foo.Bar.MyMethod()" → "Foo.Bar"
        // If parens are present, the last dot-segment is a member name, not a type.
        var parenIdx = clean.IndexOf('(');
        if (parenIdx >= 0)
        {
            clean = clean[..parenIdx];
            // Also drop the method name (last segment after the final dot)
            var lastDot = clean.LastIndexOf('.');
            if (lastDot >= 0)
                clean = clean[..lastDot];
        }

        // Split on dots: "Duende.BFF.DefaultUserService" → ["Duende", "BFF", "DefaultUserService"]
        var parts = clean.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            yield break;

        var shortName = parts[^1];
        var dirParts = parts[..^1];
        var dir = string.Join("/", dirParts);

        // Most common: {namespace/dirs}/{ClassName}.cs
        yield return string.IsNullOrEmpty(dir) ? $"{shortName}.cs" : $"{dir}/{shortName}.cs";

        // Interface naming convention: IFoo → Foo.cs (sometimes grouped in same file)
        if (shortName.Length > 1 && shortName[0] == 'I' && char.IsUpper(shortName[1]))
        {
            var withoutI = shortName[1..];
            yield return string.IsNullOrEmpty(dir) ? $"{withoutI}.cs" : $"{dir}/{withoutI}.cs";
        }

        // Extension classes: FooExtensions → Foo.cs
        if (shortName.EndsWith("Extensions", StringComparison.Ordinal) && shortName.Length > 10)
        {
            var withoutExt = shortName[..^10];
            yield return string.IsNullOrEmpty(dir) ? $"{withoutExt}.cs" : $"{dir}/{withoutExt}.cs";
        }

        // Flattened: some repos put all files in the root of a single dir, no sub-namespacing
        if (dirParts.Length > 0)
            yield return $"{shortName}.cs";
    }

    /// <summary>
    /// Given a relative candidate file path (e.g. "Duende/BFF/DefaultUserService.cs")
    /// and a Source Link document mapping, returns the resolved raw URL or null.
    /// </summary>
    internal static string? ResolveSourceLinkPattern(
        string candidatePath,
        IReadOnlyDictionary<string, string> documents
    )
    {
        // Normalise: forward slashes, lower-case for matching
        var normalised = candidatePath.Replace('\\', '/');

        foreach (var (localPattern, urlPattern) in documents)
        {
            // Source Link patterns end with * — everything before that is the fixed prefix
            if (!localPattern.EndsWith('*'))
            {
                // Exact local path
                var localNorm = localPattern.Replace('\\', '/');
                if (string.Equals(localNorm, normalised, StringComparison.OrdinalIgnoreCase))
                    return urlPattern.Replace('\\', '/');

                continue;
            }

            // Wildcard pattern: split at the *
            var localPrefix = localPattern[..^1].Replace('\\', '/'); // everything before *

            // The URL pattern must also end with * for a proper Source Link entry
            if (!urlPattern.EndsWith('*'))
                continue;

            var urlPrefix = urlPattern[..^1];

            // The candidate must start with some suffix that matches after the local wildcard prefix.
            // The prefix may be a full absolute path (C:\src\...) — strip it and match by
            // the last meaningful directory segment.
            //
            // Strategy: find the candidate path inside the local prefix structure.
            // If localPrefix is "C:\src\MyProject\", we want to match "Duende/BFF/DefaultUserService.cs"
            // by looking at each segment of localPrefix as a potential anchor.

            var resolvedRelative = TryMatchPrefix(normalised, localPrefix);
            if (resolvedRelative is not null)
                return urlPrefix + resolvedRelative;
        }

        return null;
    }

    /// <summary>
    /// Tries to find a relative path within a local Source Link prefix.
    /// The localPrefix is often an absolute build path like "C:/build/src/".
    /// We match the candidate by checking if the candidate can plausibly be
    /// a sub-path of the prefix by matching trailing segments of the prefix
    /// against leading segments of the candidate.
    /// </summary>
    private static string? TryMatchPrefix(string candidatePath, string localPrefix)
    {
        // normalise separators in prefix
        var prefix = localPrefix.Replace('\\', '/').TrimEnd('/');

        // Case 1: prefix is empty — any path matches
        if (string.IsNullOrEmpty(prefix))
            return candidatePath;

        // Case 2: the candidate literally starts with the prefix (after normalisation)
        if (candidatePath.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
            return candidatePath[(prefix.Length + 1)..];

        // Case 3: Anchor matching — try each trailing segment count of the prefix
        // e.g. prefix = "C:/build/src/Duende/BFF" → try "Duende/BFF" as anchor
        var prefixSegments = prefix.Split('/');
        for (int take = 1; take <= prefixSegments.Length; take++)
        {
            var anchor = string.Join("/", prefixSegments[^take..]);
            if (string.IsNullOrEmpty(anchor))
                continue;

            // Skip drive letters and common path roots
            if (anchor.Length <= 2 && anchor.Contains(':'))
                continue;

            if (candidatePath.StartsWith(anchor + "/", StringComparison.OrdinalIgnoreCase))
                return candidatePath[(anchor.Length + 1)..];
        }

        // Case 4: no segments match — just try the candidate directly under the prefix's last segment dir
        // This is the most speculative: the localPrefix gives us a "src root hint"
        // Return the full candidate as-is (the URL wildcard replaces * with it)
        return candidatePath;
    }

    /// <summary>
    /// Parses a raw.githubusercontent.com URL into (repository, commit, filePath).
    /// Returns nulls for fields that cannot be extracted.
    /// </summary>
    private static (string? repository, string? commit, string? filePath) ParseRawGitHubUrl(
        string rawUrl
    )
    {
        // https://raw.githubusercontent.com/{owner}/{repo}/{ref}/{path...}
        if (
            !rawUrl.StartsWith(
                "https://raw.githubusercontent.com/",
                StringComparison.OrdinalIgnoreCase
            )
        )
            return (null, null, null);

        var rest = rawUrl["https://raw.githubusercontent.com/".Length..];
        var parts = rest.Split('/', 4);
        if (parts.Length < 4)
            return (null, null, null);

        var owner = parts[0];
        var repo = parts[1];
        var commit = parts[2];
        var filePath = parts[3];

        return ($"https://github.com/{owner}/{repo}", commit, filePath);
    }
}

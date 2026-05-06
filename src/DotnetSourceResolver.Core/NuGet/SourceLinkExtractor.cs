using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json;
using DotnetSourceResolver.Core.Models.NuGet;
using Microsoft.Extensions.Logging;

namespace DotnetSourceResolver.Core.NuGet;

/// <summary>
/// Extracts Source Link JSON from Portable PDBs embedded in managed assemblies.
/// Uses <c>System.Reflection.Metadata</c> (built into net10.0).
/// </summary>
public class SourceLinkExtractor
{
    // Well-known GUID for Source Link custom debug information.
    // See: https://github.com/dotnet/core/blob/main/Documentation/diagnostics/source_link.md
    internal static readonly Guid SourceLinkId = new("CC110556-A091-4D38-9FEC-25AB9A351A6A");

    private readonly ILogger<SourceLinkExtractor> _logger;

    public SourceLinkExtractor(ILogger<SourceLinkExtractor> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Opens the assembly at <paramref name="assemblyPath"/>, looks for an embedded
    /// Portable PDB, and extracts the Source Link document from it.
    /// Returns null if the assembly has no embedded PDB or no Source Link.
    /// </summary>
    public Task<SourceLinkDocument?> ExtractAsync(string assemblyPath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        try
        {
            using var peStream = new FileStream(
                assemblyPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite
            );
            using var peReader = new PEReader(peStream);

            if (!peReader.HasMetadata)
            {
                _logger.LogDebug("{Path} has no managed metadata", assemblyPath);
                return Task.FromResult<SourceLinkDocument?>(null);
            }

            foreach (var entry in peReader.ReadDebugDirectory())
            {
                if (entry.Type != DebugDirectoryEntryType.EmbeddedPortablePdb)
                    continue;

                try
                {
                    using var pdbProvider = peReader.ReadEmbeddedPortablePdbDebugDirectoryData(
                        entry
                    );
                    var pdbReader = pdbProvider.GetMetadataReader();
                    var json = ReadSourceLinkJsonFromPdb(pdbReader);

                    if (json is null)
                    {
                        _logger.LogDebug(
                            "{Path} has embedded PDB but no Source Link data",
                            assemblyPath
                        );
                        return Task.FromResult<SourceLinkDocument?>(null);
                    }

                    var doc = ParseSourceLink(json);
                    if (doc is null)
                        _logger.LogWarning(
                            "Failed to parse Source Link JSON from {Path}: {Json}",
                            assemblyPath,
                            json
                        );

                    return Task.FromResult(doc);
                }
                catch (BadImageFormatException ex)
                {
                    _logger.LogDebug(ex, "Bad embedded PDB in {Path}", assemblyPath);
                }
            }

            _logger.LogDebug("{Path} has no embedded PDB", assemblyPath);
            return Task.FromResult<SourceLinkDocument?>(null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read assembly {Path}", assemblyPath);
            return Task.FromResult<SourceLinkDocument?>(null);
        }
    }

    /// <summary>
    /// Reads the Source Link JSON string from a <see cref="MetadataReader"/> for a Portable PDB.
    /// Returns null if no Source Link entry is present.
    /// </summary>
    internal static string? ReadSourceLinkJsonFromPdb(MetadataReader reader)
    {
        // Source Link is stored as CustomDebugInformation on the module (parent = ModuleDefinition).
        foreach (var cdiHandle in reader.CustomDebugInformation)
        {
            var cdi = reader.GetCustomDebugInformation(cdiHandle);
            if (reader.GetGuid(cdi.Kind) != SourceLinkId)
                continue;

            var blobReader = reader.GetBlobReader(cdi.Value);
            var bytes = blobReader.ReadBytes(blobReader.Length);
            return Encoding.UTF8.GetString(bytes);
        }

        return null;
    }

    /// <summary>
    /// Searches the embedded PDB for a type whose short name (or FQN short name) matches
    /// <paramref name="symbolShortName"/> and returns its source file path (the raw local
    /// path as stored in the PDB, e.g. <c>/_/bff/src/Bff/Configuration/Foo.cs</c>) and
    /// the starting line number of the first method. Returns null if not found.
    /// </summary>
    public (string LocalPath, int StartLine)? FindTypeInPdb(
        string assemblyPath,
        string symbolShortName
    )
    {
        try
        {
            using var peStream = new FileStream(
                assemblyPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite
            );
            using var peReader = new PEReader(peStream);

            if (!peReader.HasMetadata)
                return null;

            var pdbEntry = peReader
                .ReadDebugDirectory()
                .FirstOrDefault(e => e.Type == DebugDirectoryEntryType.EmbeddedPortablePdb);

            if (pdbEntry.DataSize == 0)
                return null;

            using var pdbProvider = peReader.ReadEmbeddedPortablePdbDebugDirectoryData(pdbEntry);
            var pdbReader = pdbProvider.GetMetadataReader();
            var peMetaReader = peReader.GetMetadataReader();

            return FindTypeInPdbReaders(peMetaReader, pdbReader, symbolShortName);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "FindTypeInPdb failed for {Symbol} in {Path}",
                symbolShortName,
                assemblyPath
            );
            return null;
        }
    }

    /// <summary>
    /// Core type-search logic operating directly on <see cref="MetadataReader"/> instances.
    /// Extracted for testability.
    /// </summary>
    internal static (string LocalPath, int StartLine)? FindTypeInPdbReaders(
        MetadataReader peReader,
        MetadataReader pdbReader,
        string symbolShortName
    )
    {
        // Strip generic arity and method suffixes from the short name for matching
        var shortName = symbolShortName;
        var parenIdx = shortName.IndexOf('(');
        if (parenIdx >= 0)
            shortName = shortName[..parenIdx];
        var dotIdx = shortName.LastIndexOf('.');
        if (dotIdx >= 0)
            shortName = shortName[(dotIdx + 1)..];
        var backtickIdx = shortName.IndexOf('`');
        if (backtickIdx >= 0)
            shortName = shortName[..backtickIdx];
        var angleIdx = shortName.IndexOf('<');
        if (angleIdx >= 0)
            shortName = shortName[..angleIdx];

        foreach (var typeHandle in peReader.TypeDefinitions)
        {
            var typeDef = peReader.GetTypeDefinition(typeHandle);
            var typeName = peReader.GetString(typeDef.Name);

            if (!string.Equals(typeName, shortName, StringComparison.OrdinalIgnoreCase))
                continue;

            // Found the type — get its first method's sequence point for file + line
            foreach (var methodHandle in typeDef.GetMethods())
            {
                try
                {
                    var pdbMethod = pdbReader.GetMethodDebugInformation(methodHandle);
                    if (pdbMethod.Document.IsNil)
                        continue;

                    var doc = pdbReader.GetDocument(pdbMethod.Document);
                    var docName = pdbReader.GetString(doc.Name);
                    if (string.IsNullOrEmpty(docName))
                        continue;

                    var firstVisible = pdbMethod
                        .GetSequencePoints()
                        .FirstOrDefault(sp => !sp.IsHidden);
                    var line = firstVisible.StartLine > 0 ? firstVisible.StartLine : 1;

                    return (docName, line);
                }
                catch
                {
                    // Skip malformed method debug info
                }
            }

            // Type found but no method has debug info — return type name as hint
            return null;
        }

        return null;
    }

    /// <summary>
    /// Parses the Source Link JSON blob into a <see cref="SourceLinkDocument"/>.
    /// Returns null on any parse failure.
    /// </summary>
    internal static SourceLinkDocument? ParseSourceLink(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);

            if (
                !doc.RootElement.TryGetProperty("documents", out var documentsElement)
                || documentsElement.ValueKind != JsonValueKind.Object
            )
                return null;

            var documents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in documentsElement.EnumerateObject())
                documents[prop.Name] = prop.Value.GetString() ?? string.Empty;

            return new SourceLinkDocument(documents);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

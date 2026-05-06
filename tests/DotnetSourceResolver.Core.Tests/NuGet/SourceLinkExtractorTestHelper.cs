using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;
using DotnetSourceResolver.Core.NuGet;

namespace DotnetSourceResolver.Core.Tests.NuGet;

/// <summary>
/// Shared helper for building in-memory managed PE files with embedded Portable PDBs
/// for use across multiple test classes.
/// </summary>
internal static class SourceLinkExtractorTestHelper
{
    private static ImmutableArray<int> EmptyRowCounts() =>
        ImmutableArray.Create(new int[MetadataTokens.TableCount]);

    /// <summary>
    /// Builds a minimal managed PE (.dll) byte array with an embedded Portable PDB
    /// that optionally contains Source Link custom debug information.
    /// </summary>
    public static byte[] BuildAssemblyWithEmbeddedPdb(string? sourceLinkJson)
    {
        // --- PDB metadata ---
        var pdbMetadata = new MetadataBuilder();
        pdbMetadata.AddModule(
            0,
            pdbMetadata.GetOrAddString("TestLib"),
            pdbMetadata.GetOrAddGuid(Guid.NewGuid()),
            pdbMetadata.GetOrAddGuid(Guid.Empty),
            pdbMetadata.GetOrAddGuid(Guid.Empty)
        );

        if (sourceLinkJson is not null)
        {
            var bytes = Encoding.UTF8.GetBytes(sourceLinkJson);
            pdbMetadata.AddCustomDebugInformation(
                parent: EntityHandle.ModuleDefinition,
                kind: pdbMetadata.GetOrAddGuid(SourceLinkExtractor.SourceLinkId),
                value: pdbMetadata.GetOrAddBlob(bytes)
            );
        }

        // --- PE metadata ---
        var peMetadata = new MetadataBuilder();
        peMetadata.AddModule(
            0,
            peMetadata.GetOrAddString("TestLib"),
            peMetadata.GetOrAddGuid(Guid.NewGuid()),
            peMetadata.GetOrAddGuid(Guid.Empty),
            peMetadata.GetOrAddGuid(Guid.Empty)
        );
        peMetadata.AddAssembly(
            peMetadata.GetOrAddString("TestLib"),
            new Version(1, 0, 0, 0),
            culture: default,
            publicKey: default,
            flags: 0,
            hashAlgorithm: System.Reflection.AssemblyHashAlgorithm.None
        );

        // Serialize PDB → blob → embed via DebugDirectoryBuilder
        var pdbBlob = new BlobBuilder();
        new PortablePdbBuilder(pdbMetadata, EmptyRowCounts(), default).Serialize(pdbBlob);

        var debugDirBuilder = new DebugDirectoryBuilder();
        debugDirBuilder.AddEmbeddedPortablePdbEntry(pdbBlob, portablePdbVersion: 0x0100);

        var ilStream = new BlobBuilder();
        var peBuilder = new ManagedPEBuilder(
            header: new PEHeaderBuilder(imageCharacteristics: Characteristics.Dll),
            metadataRootBuilder: new MetadataRootBuilder(peMetadata),
            ilStream: ilStream,
            debugDirectoryBuilder: debugDirBuilder
        );

        var peBlob = new BlobBuilder();
        peBuilder.Serialize(peBlob);
        return peBlob.ToArray();
    }
}

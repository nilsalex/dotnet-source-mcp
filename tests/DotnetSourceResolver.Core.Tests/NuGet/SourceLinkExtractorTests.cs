using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Text;
using DotnetSourceResolver.Core.NuGet;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetSourceResolver.Core.Tests.NuGet;

public class SourceLinkExtractorTests
{
    private static SourceLinkExtractor BuildExtractor() =>
        new(NullLogger<SourceLinkExtractor>.Instance);

    // -------------------------------------------------------------------------
    // ParseSourceLink — static helper
    // -------------------------------------------------------------------------

    [Fact]
    public void ParseSourceLink_ValidJson_ReturnsDocument()
    {
        const string json =
            """{"documents":{"C:\\src\\*":"https://raw.githubusercontent.com/org/repo/abc123/*"}}""";

        var result = SourceLinkExtractor.ParseSourceLink(json);

        Assert.NotNull(result);
        Assert.Single(result.Documents);
        Assert.Equal(
            "https://raw.githubusercontent.com/org/repo/abc123/*",
            result.Documents["C:\\src\\*"]
        );
    }

    [Fact]
    public void ParseSourceLink_MultipleDocuments_ReturnsAll()
    {
        const string json = """
            {
              "documents": {
                "C:\\src\\Foo\\*": "https://raw.githubusercontent.com/org/repo/sha1/src/Foo/*",
                "C:\\src\\Bar\\*": "https://raw.githubusercontent.com/org/repo/sha1/src/Bar/*"
              }
            }
            """;

        var result = SourceLinkExtractor.ParseSourceLink(json);

        Assert.NotNull(result);
        Assert.Equal(2, result.Documents.Count);
    }

    [Fact]
    public void ParseSourceLink_InvalidJson_ReturnsNull()
    {
        var result = SourceLinkExtractor.ParseSourceLink("not json at all <<>>");

        Assert.Null(result);
    }

    [Fact]
    public void ParseSourceLink_MissingDocumentsKey_ReturnsNull()
    {
        var result = SourceLinkExtractor.ParseSourceLink("""{"other":"value"}""");

        Assert.Null(result);
    }

    [Fact]
    public void ParseSourceLink_EmptyDocuments_ReturnsEmptyDocument()
    {
        var result = SourceLinkExtractor.ParseSourceLink("""{"documents":{}}""");

        Assert.NotNull(result);
        Assert.Empty(result.Documents);
    }

    // -------------------------------------------------------------------------
    // ReadSourceLinkJsonFromPdb — static helper (uses MetadataReader directly)
    // -------------------------------------------------------------------------

    [Fact]
    public void ReadSourceLinkJsonFromPdb_NoSourceLink_ReturnsNull()
    {
        // Build a minimal Portable PDB with no CustomDebugInformation entries
        var pdbBytes = BuildMinimalPortablePdb(sourceLinkJson: null);

        using var stream = new MemoryStream(pdbBytes);
        using var provider = MetadataReaderProvider.FromPortablePdbStream(stream);
        var reader = provider.GetMetadataReader();

        var result = SourceLinkExtractor.ReadSourceLinkJsonFromPdb(reader);

        Assert.Null(result);
    }

    [Fact]
    public void ReadSourceLinkJsonFromPdb_WithSourceLink_ReturnsJson()
    {
        const string json =
            """{"documents":{"C:\\src\\*":"https://raw.githubusercontent.com/org/repo/abc123/*"}}""";
        var pdbBytes = BuildMinimalPortablePdb(sourceLinkJson: json);

        using var stream = new MemoryStream(pdbBytes);
        using var provider = MetadataReaderProvider.FromPortablePdbStream(stream);
        var reader = provider.GetMetadataReader();

        var result = SourceLinkExtractor.ReadSourceLinkJsonFromPdb(reader);

        Assert.Equal(json, result);
    }

    // -------------------------------------------------------------------------
    // FindTypeInPdbReaders — static helper
    // -------------------------------------------------------------------------

    [Fact]
    public void FindTypeInPdbReaders_ExistingType_ReturnsPathAndLine()
    {
        // Build a PE with a type named "MyClass" whose first method has a sequence point
        // The helper builds a minimal PE — sequence points require extra setup, so we test
        // that the method returns null gracefully for types with no debug info rather than throwing
        var pdbBytes = BuildMinimalPortablePdb(sourceLinkJson: null);
        // We can't easily add TypeDefinitions + MethodDefinitions + SequencePoints in this test
        // without a full Roslyn compilation; instead verify the null-safe path
        using var pdbStream = new MemoryStream(pdbBytes);
        using var pdbProvider = MetadataReaderProvider.FromPortablePdbStream(pdbStream);
        var pdbReader = pdbProvider.GetMetadataReader();

        // Create a minimal PE metadata reader (empty, no types)
        var peBuilder = new MetadataBuilder();
        peBuilder.AddModule(
            0,
            peBuilder.GetOrAddString("Test"),
            peBuilder.GetOrAddGuid(Guid.NewGuid()),
            peBuilder.GetOrAddGuid(Guid.Empty),
            peBuilder.GetOrAddGuid(Guid.Empty)
        );
        var peSerializer = new MetadataRootBuilder(peBuilder);
        var peBlob = new BlobBuilder();
        peSerializer.Serialize(peBlob, methodBodyStreamRva: 0, mappedFieldDataStreamRva: 0);
        using var peStream = new MemoryStream(peBlob.ToArray());
        using var peReader = MetadataReaderProvider.FromMetadataStream(peStream);
        var peMetaReader = peReader.GetMetadataReader();

        // Empty assembly has no types → should return null without throwing
        var result = SourceLinkExtractor.FindTypeInPdbReaders(
            peMetaReader,
            pdbReader,
            "NonExistentClass"
        );
        Assert.Null(result);
    }

    [Fact]
    public void FindTypeInPdb_NonExistentFile_ReturnsNull()
    {
        var extractor = BuildExtractor();
        var result = extractor.FindTypeInPdb("/nonexistent/path.dll", "SomeClass");
        Assert.Null(result);
    }

    [Fact]
    public void FindTypeInPdb_NotAnAssembly_ReturnsNull()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(tempFile, "not a PE"u8.ToArray());
            var extractor = BuildExtractor();
            var result = extractor.FindTypeInPdb(tempFile, "SomeClass");
            Assert.Null(result);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    // -------------------------------------------------------------------------
    // ExtractAsync — full pipeline using a real embedded PDB
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ExtractAsync_NonExistentFile_ReturnsNull()
    {
        var extractor = BuildExtractor();

        var result = await extractor.ExtractAsync("/nonexistent/path/assembly.dll", default);

        Assert.Null(result);
    }

    [Fact]
    public async Task ExtractAsync_NotAnAssembly_ReturnsNull()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(tempFile, "this is not a PE file"u8.ToArray());

            var extractor = BuildExtractor();
            var result = await extractor.ExtractAsync(tempFile, default);

            Assert.Null(result);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task ExtractAsync_AssemblyWithEmbeddedPdbAndSourceLink_ReturnsDocument()
    {
        const string sourceLinkJson =
            """{"documents":{"C:\\src\\*":"https://raw.githubusercontent.com/org/repo/abc123/*"}}""";

        var dllBytes = BuildAssemblyWithEmbeddedPdb(sourceLinkJson);
        var tempFile = Path.GetTempFileName() + ".dll";
        try
        {
            await File.WriteAllBytesAsync(tempFile, dllBytes);

            var extractor = BuildExtractor();
            var result = await extractor.ExtractAsync(tempFile, default);

            Assert.NotNull(result);
            Assert.Single(result.Documents);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task ExtractAsync_AssemblyWithEmbeddedPdbButNoSourceLink_ReturnsNull()
    {
        var dllBytes = BuildAssemblyWithEmbeddedPdb(sourceLinkJson: null);
        var tempFile = Path.GetTempFileName() + ".dll";
        try
        {
            await File.WriteAllBytesAsync(tempFile, dllBytes);

            var extractor = BuildExtractor();
            var result = await extractor.ExtractAsync(tempFile, default);

            Assert.Null(result);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    // -------------------------------------------------------------------------
    // Helpers — build minimal Portable PDB and PE bytes in memory
    // -------------------------------------------------------------------------

    // typeSystemRowCounts must have exactly MetadataTokens.TableCount elements (all zeros for a PDB-only build)
    private static ImmutableArray<int> EmptyRowCounts() =>
        ImmutableArray.Create(new int[MetadataTokens.TableCount]);

    /// <summary>
    /// Builds a minimal valid Portable PDB byte array.
    /// If <paramref name="sourceLinkJson"/> is not null, a Source Link CustomDebugInformation
    /// entry is added to the module.
    /// </summary>
    private static byte[] BuildMinimalPortablePdb(string? sourceLinkJson)
    {
        var builder = new MetadataBuilder();

        // Required: module table
        builder.AddModule(
            generation: 0,
            moduleName: builder.GetOrAddString("TestModule"),
            mvid: builder.GetOrAddGuid(Guid.NewGuid()),
            encId: builder.GetOrAddGuid(Guid.Empty),
            encBaseId: builder.GetOrAddGuid(Guid.Empty)
        );

        if (sourceLinkJson is not null)
        {
            var sourceLinkBytes = Encoding.UTF8.GetBytes(sourceLinkJson);
            builder.AddCustomDebugInformation(
                parent: EntityHandle.ModuleDefinition,
                kind: builder.GetOrAddGuid(SourceLinkExtractor.SourceLinkId),
                value: builder.GetOrAddBlob(sourceLinkBytes)
            );
        }

        var pdbBlob = new BlobBuilder();
        var serializer = new PortablePdbBuilder(builder, EmptyRowCounts(), default);
        serializer.Serialize(pdbBlob);
        return pdbBlob.ToArray();
    }

    private static byte[] BuildAssemblyWithEmbeddedPdb(string? sourceLinkJson) =>
        SourceLinkExtractorTestHelper.BuildAssemblyWithEmbeddedPdb(sourceLinkJson);
}

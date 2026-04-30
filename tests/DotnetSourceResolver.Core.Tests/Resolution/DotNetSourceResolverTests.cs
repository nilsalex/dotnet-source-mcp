using DotnetSourceResolver.Core.Models;
using DotnetSourceResolver.Core.Resolution;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace DotnetSourceResolver.Core.Tests.Resolution;

public class DotNetSourceResolverTests
{
    private static SourceResult MakeResult(string symbol) =>
        new(
            Resolved: true,
            CanonicalSymbol: symbol,
            ResolutionKind: ResolutionKind.GitHub,
            Confidence: ResolutionConfidence.High,
            Sources: [],
            Snippets: [],
            Diagnostics: [],
            ResolverVersion: "0.0.0"
        );

    private static DotNetSourceResolver Build(params ISourceAdapter[] adapters) =>
        new(adapters, NullLogger<DotNetSourceResolver>.Instance);

    [Fact]
    public async Task FirstAdapterReturnsResult_UsedDirectly()
    {
        var expected = MakeResult("A");
        var adapter = new Mock<ISourceAdapter>();
        adapter
            .Setup(a => a.TryResolveAsync(It.IsAny<SymbolRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var resolver = Build(adapter.Object);
        var result = await resolver.ResolveAsync(new SymbolRequest("A"));

        Assert.Same(expected, result);
    }

    [Fact]
    public async Task FirstAdapterReturnsNull_SecondAdapterUsed()
    {
        var expected = MakeResult("B");

        var first = new Mock<ISourceAdapter>();
        first
            .Setup(a => a.TryResolveAsync(It.IsAny<SymbolRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SourceResult?)null);

        var second = new Mock<ISourceAdapter>();
        second
            .Setup(a => a.TryResolveAsync(It.IsAny<SymbolRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var resolver = Build(first.Object, second.Object);
        var result = await resolver.ResolveAsync(new SymbolRequest("B"));

        Assert.Same(expected, result);
    }

    [Fact]
    public async Task AllAdaptersReturnNull_ReturnsUnresolved()
    {
        var first = new Mock<ISourceAdapter>();
        first
            .Setup(a => a.TryResolveAsync(It.IsAny<SymbolRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SourceResult?)null);

        var second = new Mock<ISourceAdapter>();
        second
            .Setup(a => a.TryResolveAsync(It.IsAny<SymbolRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SourceResult?)null);

        var resolver = Build(first.Object, second.Object);
        var result = await resolver.ResolveAsync(new SymbolRequest("X"));

        Assert.False(result.Resolved);
        Assert.Equal(ResolutionKind.Unresolved, result.ResolutionKind);
    }

    [Fact]
    public async Task AllAdaptersReturnNull_DiagnosticsContainAdapterNames()
    {
        var first = new Mock<ISourceAdapter>();
        first
            .Setup(a => a.TryResolveAsync(It.IsAny<SymbolRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SourceResult?)null);

        var resolver = Build(first.Object);
        var result = await resolver.ResolveAsync(new SymbolRequest("X"));

        Assert.Contains(result.Diagnostics, d => d.Contains("no result"));
    }

    [Fact]
    public async Task AdapterThrows_MovesToNextAdapter()
    {
        var expected = MakeResult("C");

        var throwing = new Mock<ISourceAdapter>();
        throwing
            .Setup(a => a.TryResolveAsync(It.IsAny<SymbolRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("network error"));

        var good = new Mock<ISourceAdapter>();
        good.Setup(a => a.TryResolveAsync(It.IsAny<SymbolRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var resolver = Build(throwing.Object, good.Object);
        var result = await resolver.ResolveAsync(new SymbolRequest("C"));

        Assert.Same(expected, result);
    }

    [Fact]
    public async Task AdapterThrows_DiagnosticRecorded()
    {
        var throwing = new Mock<ISourceAdapter>();
        throwing
            .Setup(a => a.TryResolveAsync(It.IsAny<SymbolRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var resolver = Build(throwing.Object);
        var result = await resolver.ResolveAsync(new SymbolRequest("D"));

        Assert.Contains(result.Diagnostics, d => d.Contains("InvalidOperationException"));
    }

    [Fact]
    public async Task CancellationRequested_Propagates()
    {
        var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var adapter = new Mock<ISourceAdapter>();
        adapter
            .Setup(a => a.TryResolveAsync(It.IsAny<SymbolRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SourceResult?)null);

        var resolver = Build(adapter.Object);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            resolver.ResolveAsync(new SymbolRequest("E"), cts.Token)
        );
    }

    [Fact]
    public async Task NoAdapters_ReturnsUnresolved()
    {
        var resolver = Build();
        var result = await resolver.ResolveAsync(new SymbolRequest("F"));
        Assert.False(result.Resolved);
    }
}

using DotnetSourceResolver.Core.Models;
using Xunit;

namespace DotnetSourceResolver.Core.Tests.Models;

public class SourceResultTests
{
    [Fact]
    public void Unresolved_SetsResolvedFalse()
    {
        var result = SourceResult.Unresolved("MySymbol", []);
        Assert.False(result.Resolved);
    }

    [Fact]
    public void Unresolved_SetsCanonicalSymbol()
    {
        var result = SourceResult.Unresolved("System.String", []);
        Assert.Equal("System.String", result.CanonicalSymbol);
    }

    [Fact]
    public void Unresolved_SetsResolutionKindUnresolved()
    {
        var result = SourceResult.Unresolved("X", []);
        Assert.Equal(ResolutionKind.Unresolved, result.ResolutionKind);
    }

    [Fact]
    public void Unresolved_SetsConfidenceLow()
    {
        var result = SourceResult.Unresolved("X", []);
        Assert.Equal(ResolutionConfidence.Low, result.Confidence);
    }

    [Fact]
    public void Unresolved_HasEmptySourcesAndSnippets()
    {
        var result = SourceResult.Unresolved("X", []);
        Assert.Empty(result.Sources);
        Assert.Empty(result.Snippets);
    }

    [Fact]
    public void Unresolved_IncludesDiagnostics()
    {
        var result = SourceResult.Unresolved("X", ["adapter A failed", "adapter B failed"]);
        Assert.Equal(2, result.Diagnostics.Count);
        Assert.Contains("adapter A failed", result.Diagnostics);
        Assert.Contains("adapter B failed", result.Diagnostics);
    }

    [Fact]
    public void Unresolved_HasNonEmptyResolverVersion()
    {
        var result = SourceResult.Unresolved("X", []);
        Assert.False(string.IsNullOrWhiteSpace(result.ResolverVersion));
    }
}

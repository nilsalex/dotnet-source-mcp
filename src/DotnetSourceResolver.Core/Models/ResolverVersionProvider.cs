using System.Reflection;

namespace DotnetSourceResolver.Core;

public static class ResolverVersionProvider
{
    public static readonly string Version =
        typeof(ResolverVersionProvider)
            .Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
        ?? "0.0.0";
}

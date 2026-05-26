using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Foundation.Core.TypeSystem;

/// <summary>
/// Default <see cref="ILegacyAgentClrTypeResolver"/> using
/// <see cref="Type.GetType(string, bool)"/> with a fallback to
/// <c>AppDomain.GetAssemblies()</c> — the same reflection probe
/// <c>RuntimeActorGrain.ResolveAgentType</c> previously held inline. Lifted
/// here so the grain itself depends only on
/// <see cref="IAgentKindRegistry"/> + this transitional port.
/// </summary>
/// <remarks>
/// Stateless: the resolver does not capture an <see cref="IServiceProvider"/>;
/// the activation-time provider is supplied via
/// <see cref="AgentImplementation.Factory"/> so grain-scoped dependencies
/// resolve in the grain's own container.
/// </remarks>
public sealed class ReflectionLegacyAgentClrTypeResolver : ILegacyAgentClrTypeResolver
{
    public bool TryResolve(string clrTypeName, out AgentImplementation implementation)
    {
        if (string.IsNullOrWhiteSpace(clrTypeName))
        {
            implementation = null!;
            return false;
        }

        var resolvedType = ResolveType(clrTypeName);
        if (resolvedType == null || !typeof(IAgent).IsAssignableFrom(resolvedType))
        {
            implementation = null!;
            return false;
        }

        implementation = new AgentImplementation(
            Factory: services => CreateInstance(services, resolvedType),
            StateContractType: typeof(object),
            Metadata: new AgentImplementationMetadata(
                Kind: string.Empty,
                ImplementationClrTypeName: resolvedType.FullName ?? resolvedType.Name,
                LegacyKinds: Array.Empty<string>(),
                LegacyClrTypeNames: new[] { clrTypeName }));
        return true;
    }

    private static IAgent CreateInstance(IServiceProvider services, Type agentType)
    {
        ArgumentNullException.ThrowIfNull(services);
        var instance = ActivatorUtilities.CreateInstance(services, agentType);
        return instance as IAgent
            ?? throw new InvalidOperationException(
                $"Resolved type '{agentType.FullName}' does not implement IAgent.");
    }

    private static Type? ResolveType(string clrTypeName)
    {
        var direct = Type.GetType(clrTypeName, throwOnError: false);
        if (direct != null)
            return direct;

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            var resolved = assembly.GetType(clrTypeName, throwOnError: false);
            if (resolved != null)
                return resolved;
        }

        return null;
    }
}

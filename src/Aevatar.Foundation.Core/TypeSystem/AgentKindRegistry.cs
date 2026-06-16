using System.Reflection;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Foundation.Core.TypeSystem;

/// <summary>
/// Default <see cref="IAgentKindRegistry"/> backed by
/// primary <see cref="GAgentAttribute"/> declarations on agent classes.
/// </summary>
/// <remarks>
/// Registration is one-shot: builders capture types at host startup; the
/// registry itself is read-only after construction so the activation hot
/// path is lock-free dictionary lookup. <see cref="AgentImplementation"/>
/// instances are pre-built per registration so resolution allocates nothing
/// on the activation path.
/// </remarks>
public sealed class AgentKindRegistry : IAgentKindRegistry
{
    private readonly Dictionary<string, AgentImplementation> _implByKind;
    private readonly Dictionary<Type, string> _kindByAgentType;

    public AgentKindRegistry(IEnumerable<AgentRegistration> registrations)
    {
        ArgumentNullException.ThrowIfNull(registrations);

        var byKind = new Dictionary<string, AgentRegistration>(StringComparer.Ordinal);
        _implByKind = new Dictionary<string, AgentImplementation>(StringComparer.Ordinal);
        _kindByAgentType = new Dictionary<Type, string>();

        var snapshot = registrations as IReadOnlyCollection<AgentRegistration> ?? registrations.ToList();
        foreach (var registration in snapshot)
            AddPrimary(byKind, registration);
    }

    public AgentImplementation Resolve(string kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);

        if (TryResolve(kind, out var implementation))
            return implementation;

        throw new UnknownAgentKindException(kind);
    }

    public bool TryResolve(string kind, out AgentImplementation implementation)
    {
        if (string.IsNullOrWhiteSpace(kind))
        {
            implementation = null!;
            return false;
        }

        if (_implByKind.TryGetValue(kind, out var direct))
        {
            implementation = direct;
            return true;
        }

        implementation = null!;
        return false;
    }

    public bool TryGetKindForAgentType(Type agentType, out string kind)
    {
        ArgumentNullException.ThrowIfNull(agentType);

        if (_kindByAgentType.TryGetValue(agentType, out kind!))
            return true;

        foreach (var (registeredType, registeredKind) in _kindByAgentType)
        {
            if (registeredType.IsAssignableFrom(agentType))
            {
                kind = registeredKind;
                return true;
            }
        }

        kind = string.Empty;
        return false;
    }

    public bool TryGetKind(AgentImplementation implementation, out string kind)
    {
        ArgumentNullException.ThrowIfNull(implementation);
        kind = implementation.Metadata.Kind;
        return _implByKind.ContainsKey(kind);
    }

    private void AddPrimary(
        Dictionary<string, AgentRegistration> byKind,
        AgentRegistration registration)
    {
        if (byKind.TryGetValue(registration.Kind, out var existing))
        {
            throw new InvalidOperationException(
                $"Duplicate agent kind '{registration.Kind}': already registered for " +
                $"'{existing.ImplementationType.FullName}', cannot also register for " +
                $"'{registration.ImplementationType.FullName}'.");
        }

        byKind[registration.Kind] = registration;
        _implByKind[registration.Kind] = registration.BuildImplementation();
        _kindByAgentType[registration.ImplementationType] = registration.Kind;
    }
}

/// <summary>
/// Captured state for one agent kind registration. Built by
/// <see cref="AgentKindRegistryBuilder"/> from primary
/// <see cref="GAgentAttribute"/> on the agent class.
/// </summary>
public sealed record AgentRegistration(
    string Kind,
    Type ImplementationType,
    Type StateContractType)
{
    /// <summary>
    /// Builds the <see cref="AgentImplementation"/> handle once at registry
    /// construction. The factory closes over the agent's CLR type only;
    /// dependency resolution happens against the activation-time
    /// <see cref="IServiceProvider"/> the caller passes in, so grain-scoped
    /// services bind through the grain's container instead of the silo
    /// root container.
    /// </summary>
    public AgentImplementation BuildImplementation()
    {
        var implType = ImplementationType;
        var kind = Kind;
        return new AgentImplementation(
            Factory: services => CreateInstance(services, implType, kind),
            StateContractType: StateContractType,
            Metadata: new AgentImplementationMetadata(
                Kind: Kind,
                ImplementationClrTypeName: ImplementationType.FullName ?? ImplementationType.Name));
    }

    private static IAgent CreateInstance(IServiceProvider services, Type implementationType, string kind)
    {
        ArgumentNullException.ThrowIfNull(services);
        var instance = ActivatorUtilities.CreateInstance(services, implementationType);
        return instance as IAgent
            ?? throw new InvalidOperationException(
                $"Agent class '{implementationType.FullName}' for kind '{kind}' does not implement IAgent.");
    }

    public static AgentRegistration FromAgentType(Type agentType)
    {
        ArgumentNullException.ThrowIfNull(agentType);

        if (!typeof(IAgent).IsAssignableFrom(agentType))
        {
            throw new InvalidOperationException(
                $"Type '{agentType.FullName}' is decorated with [GAgent] but does not implement IAgent.");
        }

        var gAgent = agentType.GetCustomAttribute<GAgentAttribute>(inherit: false)
            ?? throw new InvalidOperationException(
                $"Type '{agentType.FullName}' has no [GAgent] attribute.");

        var stateContract = ResolveStateContract(agentType);

        return new AgentRegistration(
            Kind: gAgent.Kind,
            ImplementationType: agentType,
            StateContractType: stateContract);
    }

    private static Type ResolveStateContract(Type agentType)
    {
        // Type.GetInterfaces() already returns the full interface set across
        // the inheritance chain, so a single scan suffices.
        foreach (var iface in agentType.GetInterfaces())
        {
            if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IAgent<>))
                return iface.GetGenericArguments()[0];
        }

        return typeof(object);
    }
}

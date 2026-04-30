using System.Reflection;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Compatibility;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Foundation.Core.TypeSystem;

/// <summary>
/// Default <see cref="IAgentKindRegistry"/> backed by
/// <see cref="GAgentAttribute"/> / <see cref="LegacyAgentKindAttribute"/> /
/// <see cref="LegacyClrTypeNameAttribute"/> declarations on agent classes.
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
    private readonly Dictionary<string, string> _legacyKindToKind;
    private readonly Dictionary<string, string> _clrTypeNameToKind;

    public AgentKindRegistry(IEnumerable<AgentRegistration> registrations)
    {
        ArgumentNullException.ThrowIfNull(registrations);

        var byKind = new Dictionary<string, AgentRegistration>(StringComparer.Ordinal);
        _implByKind = new Dictionary<string, AgentImplementation>(StringComparer.Ordinal);
        _legacyKindToKind = new Dictionary<string, string>(StringComparer.Ordinal);
        _clrTypeNameToKind = new Dictionary<string, string>(StringComparer.Ordinal);

        // Two-pass build: collect all primary kinds first so legacy-alias
        // collision checks (alias colliding with a primary) see the full set.
        var snapshot = registrations as IReadOnlyCollection<AgentRegistration> ?? registrations.ToList();
        foreach (var registration in snapshot)
            AddPrimary(byKind, registration);

        foreach (var registration in snapshot)
            AddAliases(byKind, registration);
    }

    public AgentImplementation Resolve(string kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);

        if (_implByKind.TryGetValue(kind, out var direct))
            return direct;

        if (_legacyKindToKind.TryGetValue(kind, out var canonical) &&
            _implByKind.TryGetValue(canonical, out var aliased))
            return aliased;

        throw new UnknownAgentKindException(kind);
    }

    public bool TryResolveKindByClrTypeName(string clrFullName, out string kind)
    {
        if (string.IsNullOrWhiteSpace(clrFullName))
        {
            kind = string.Empty;
            return false;
        }

        return _clrTypeNameToKind.TryGetValue(clrFullName, out kind!);
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

        var implClrName = registration.ImplementationType.FullName
            ?? throw new InvalidOperationException(
                $"Agent class '{registration.ImplementationType}' has no FullName; cannot register CLR-name lookup.");

        TryAddClrTypeNameLookup(implClrName, registration.Kind);
        foreach (var legacyClrName in registration.LegacyClrTypeNames)
            TryAddClrTypeNameLookup(legacyClrName, registration.Kind);
    }

    private void AddAliases(
        Dictionary<string, AgentRegistration> byKind,
        AgentRegistration registration)
    {
        foreach (var legacyKind in registration.LegacyKinds)
        {
            // A legacy alias colliding with an existing primary would be
            // shadowed in Resolve (primary lookup wins), silently routing
            // some callers to the wrong implementation. Surface the conflict
            // at registration time instead.
            if (byKind.TryGetValue(legacyKind, out var primaryOwner) &&
                !string.Equals(primaryOwner.Kind, registration.Kind, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Legacy agent kind '{legacyKind}' is also a primary kind owned by " +
                    $"'{primaryOwner.ImplementationType.FullName}'. A legacy alias and a primary kind " +
                    "cannot share the same token; pick a different alias or rename the primary.");
            }

            if (_legacyKindToKind.TryGetValue(legacyKind, out var owner) &&
                !string.Equals(owner, registration.Kind, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Legacy agent kind '{legacyKind}' is claimed by both '{owner}' and " +
                    $"'{registration.Kind}'. Each legacy alias must map to exactly one canonical kind.");
            }

            _legacyKindToKind[legacyKind] = registration.Kind;
        }
    }

    private void TryAddClrTypeNameLookup(string clrName, string kind)
    {
        if (_clrTypeNameToKind.TryGetValue(clrName, out var existingKind) &&
            !string.Equals(existingKind, kind, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"CLR type name '{clrName}' is claimed by both kinds '{existingKind}' and '{kind}'. " +
                "Each CLR full name (current or [LegacyClrTypeName]) must map to one kind.");
        }

        _clrTypeNameToKind[clrName] = kind;
    }
}

/// <summary>
/// Captured state for one agent kind registration. Built by
/// <see cref="AgentKindRegistryBuilder"/> from
/// <see cref="GAgentAttribute"/> / <see cref="LegacyAgentKindAttribute"/> /
/// <see cref="LegacyClrTypeNameAttribute"/> on the agent class.
/// </summary>
public sealed record AgentRegistration(
    string Kind,
    Type ImplementationType,
    Type StateContractType,
    IReadOnlyList<string> LegacyKinds,
    IReadOnlyList<string> LegacyClrTypeNames)
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
                ImplementationClrTypeName: ImplementationType.FullName ?? ImplementationType.Name,
                LegacyKinds: LegacyKinds,
                LegacyClrTypeNames: LegacyClrTypeNames));
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

        var legacyKinds = agentType.GetCustomAttributes<LegacyAgentKindAttribute>(inherit: false)
            .Select(static attr => attr.LegacyKind)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var legacyClrTypeNames = agentType.GetCustomAttributes<LegacyClrTypeNameAttribute>(inherit: false)
            .Select(static attr => attr.FullName)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var stateContract = ResolveStateContract(agentType);

        return new AgentRegistration(
            Kind: gAgent.Kind,
            ImplementationType: agentType,
            StateContractType: stateContract,
            LegacyKinds: legacyKinds,
            LegacyClrTypeNames: legacyClrTypeNames);
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

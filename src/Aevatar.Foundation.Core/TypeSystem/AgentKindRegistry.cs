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
/// path is lock-free dictionary lookup.
/// </remarks>
public sealed class AgentKindRegistry : IAgentKindRegistry
{
    private readonly IServiceProvider _services;
    private readonly Dictionary<string, AgentRegistration> _byKind;
    private readonly Dictionary<string, string> _legacyKindToKind;
    private readonly Dictionary<string, string> _clrTypeNameToKind;

    public AgentKindRegistry(IServiceProvider services, IEnumerable<AgentRegistration> registrations)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(registrations);

        _services = services;
        _byKind = new Dictionary<string, AgentRegistration>(StringComparer.Ordinal);
        _legacyKindToKind = new Dictionary<string, string>(StringComparer.Ordinal);
        _clrTypeNameToKind = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var registration in registrations)
            Add(registration);
    }

    public AgentImplementation Resolve(string kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);

        if (_byKind.TryGetValue(kind, out var direct))
            return direct.ToImplementation(_services);

        if (_legacyKindToKind.TryGetValue(kind, out var canonical) &&
            _byKind.TryGetValue(canonical, out var aliased))
            return aliased.ToImplementation(_services);

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
        return _byKind.ContainsKey(kind);
    }

    private void Add(AgentRegistration registration)
    {
        if (_byKind.TryGetValue(registration.Kind, out var existing))
        {
            throw new InvalidOperationException(
                $"Duplicate agent kind '{registration.Kind}': already registered for " +
                $"'{existing.ImplementationType.FullName}', cannot also register for " +
                $"'{registration.ImplementationType.FullName}'.");
        }

        _byKind[registration.Kind] = registration;

        var implClrName = registration.ImplementationType.FullName
            ?? throw new InvalidOperationException(
                $"Agent class '{registration.ImplementationType}' has no FullName; cannot register CLR-name lookup.");

        TryAddClrTypeNameLookup(implClrName, registration.Kind);
        foreach (var legacyClrName in registration.LegacyClrTypeNames)
            TryAddClrTypeNameLookup(legacyClrName, registration.Kind);

        foreach (var legacyKind in registration.LegacyKinds)
        {
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
    public AgentImplementation ToImplementation(IServiceProvider services) =>
        new(
            Factory: () => CreateInstance(services),
            StateContractType: StateContractType,
            Metadata: new AgentImplementationMetadata(
                Kind: Kind,
                ImplementationClrTypeName: ImplementationType.FullName ?? ImplementationType.Name,
                LegacyKinds: LegacyKinds,
                LegacyClrTypeNames: LegacyClrTypeNames));

    private IAgent CreateInstance(IServiceProvider services)
    {
        var instance = ActivatorUtilities.CreateInstance(services, ImplementationType);
        return instance as IAgent
            ?? throw new InvalidOperationException(
                $"Agent class '{ImplementationType.FullName}' for kind '{Kind}' does not implement IAgent.");
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
        // Walk the inheritance chain looking for IAgent<TState>. State contract is
        // diagnostic metadata; an agent without one falls back to typeof(object).
        for (var current = agentType; current != null; current = current.BaseType)
        {
            foreach (var iface in current.GetInterfaces())
            {
                if (!iface.IsGenericType)
                    continue;

                if (iface.GetGenericTypeDefinition() != typeof(IAgent<>))
                    continue;

                return iface.GetGenericArguments()[0];
            }
        }

        return typeof(object);
    }
}

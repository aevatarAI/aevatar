using System.Reflection;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.TypeSystem;

namespace Aevatar.Foundation.Core.TypeSystem;

/// <summary>
/// Collects <see cref="AgentRegistration"/> entries from assemblies and
/// explicit calls; consumed once at host startup to build
/// <see cref="AgentKindRegistry"/>.
/// </summary>
public sealed class AgentKindRegistryBuilder
{
    private readonly Dictionary<string, AgentRegistration> _byKind = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Type>> _migrationTypesByKind = new(StringComparer.Ordinal);

    /// <summary>
    /// Scans <paramref name="assemblies"/> for non-abstract concrete
    /// <see cref="IAgent"/> types decorated with
    /// <see cref="GAgentAttribute"/> and adds each as a registration.
    /// Idempotent: registering the same type from multiple assemblies is a
    /// no-op; conflicting kinds raise.
    /// </summary>
    public AgentKindRegistryBuilder ScanAssemblies(params Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        foreach (var assembly in assemblies)
            ScanAgentAssembly(assembly);
        foreach (var assembly in assemblies)
            ScanMigrationAssembly(assembly);
        return this;
    }

    /// <summary>
    /// Registers a single agent class explicitly (test / dynamic-loading
    /// case). The class must carry <see cref="GAgentAttribute"/>.
    /// </summary>
    public AgentKindRegistryBuilder Register(Type agentType)
    {
        var registration = AgentRegistration.FromAgentType(agentType);
        AddInternal(registration);
        return this;
    }

    public AgentKindRegistryBuilder Register<TAgent>() where TAgent : IAgent =>
        Register(typeof(TAgent));

    /// <summary>
    /// Adds a fully-built <see cref="AgentRegistration"/>. Useful when the
    /// kind needs to be supplied externally for tests or dynamic loading.
    /// </summary>
    public AgentKindRegistryBuilder Register(AgentRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        AddInternal(registration);
        return this;
    }

    /// <summary>
    /// Snapshots the registrations into an immutable list. Returning a copy
    /// rather than the live <c>Dictionary.ValueCollection</c> ensures
    /// post-build mutations to the builder do not leak into the constructed
    /// <see cref="AgentKindRegistry"/>.
    /// </summary>
    public IReadOnlyCollection<AgentRegistration> Build()
    {
        var registrations = new List<AgentRegistration>(_byKind.Count);
        foreach (var registration in _byKind.Values)
        {
            var discovered = _migrationTypesByKind.GetValueOrDefault(registration.Kind) ?? [];
            var declared = registration.StateMigrationTypes ?? [];
            registrations.Add(registration with
            {
                StateMigrationTypes = declared
                    .Concat(discovered)
                    .Distinct()
                    .ToArray(),
            });
        }

        var unknownKinds = _migrationTypesByKind.Keys
            .Where(kind => !_byKind.ContainsKey(kind))
            .OrderBy(static kind => kind, StringComparer.Ordinal)
            .ToArray();
        if (unknownKinds.Length > 0)
        {
            throw new InvalidOperationException(
                $"State migrations target unregistered agent kinds: {string.Join(", ", unknownKinds)}.");
        }

        return registrations;
    }

    private void ScanAgentAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        foreach (var type in SafeGetTypes(assembly))
        {
            if (type.IsAbstract || type.IsInterface || type.IsGenericTypeDefinition)
                continue;

            if (!typeof(IAgent).IsAssignableFrom(type))
                continue;

            if (type.GetCustomAttribute<GAgentAttribute>(inherit: false) == null)
                continue;

            var registration = AgentRegistration.FromAgentType(type);
            AddInternal(registration);
        }
    }

    private void ScanMigrationAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        foreach (var type in SafeGetTypes(assembly))
        {
            if (type.IsAbstract || type.IsInterface || type.IsGenericTypeDefinition)
                continue;

            var migration = type.GetCustomAttribute<ActorStateMigrationAttribute>(inherit: false);
            if (migration == null)
                continue;

            if (!_migrationTypesByKind.TryGetValue(migration.AgentKind, out var types))
            {
                types = [];
                _migrationTypesByKind[migration.AgentKind] = types;
            }

            if (!types.Contains(type))
                types.Add(type);
        }
    }

    private void AddInternal(AgentRegistration registration)
    {
        if (_byKind.TryGetValue(registration.Kind, out var existing))
        {
            if (existing.ImplementationType == registration.ImplementationType)
                return;

            throw new InvalidOperationException(
                $"Duplicate agent kind '{registration.Kind}': '{existing.ImplementationType.FullName}' " +
                $"and '{registration.ImplementationType.FullName}' both claim it.");
        }

        _byKind[registration.Kind] = registration;
    }

    // Reflection-only assembly scans can throw ReflectionTypeLoadException
    // when downstream optional dependencies are missing; gracefully use the
    // partial type list rather than failing host bootstrap.
    private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t != null)!;
        }
    }
}

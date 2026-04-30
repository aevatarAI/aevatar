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
            ScanAssembly(assembly);
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
    /// agent class predates the <see cref="GAgentAttribute"/> decoration
    /// and the kind needs to be supplied externally (test fixtures, dynamic
    /// loading, etc.).
    /// </summary>
    public AgentKindRegistryBuilder Register(AgentRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        AddInternal(registration);
        return this;
    }

    /// <summary>
    /// Snapshots the registrations into an immutable collection; the builder
    /// is intentionally not reusable after this call.
    /// </summary>
    public IReadOnlyCollection<AgentRegistration> Build() => _byKind.Values;

    private void ScanAssembly(Assembly assembly)
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

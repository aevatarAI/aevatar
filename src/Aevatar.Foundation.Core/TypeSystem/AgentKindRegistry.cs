using System.Reflection;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Microsoft.Extensions.DependencyInjection;
using Google.Protobuf;
using Aevatar.Foundation.Abstractions.Runtime;

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
    Type StateContractType,
    int StateSchemaVersion = 0,
    IReadOnlyList<Type>? StateMigrationTypes = null,
    IReadOnlyList<ActorStateMigrationStep>? PrebuiltStateMigrationSteps = null)
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
        if (StateSchemaVersion < 0)
        {
            throw new InvalidOperationException(
                $"Agent kind '{Kind}' declares negative state schema version {StateSchemaVersion}.");
        }
        var migrations = BuildMigrationChain();
        var implType = ImplementationType;
        var kind = Kind;
        return new AgentImplementation(
            Factory: services => CreateInstance(services, implType, kind),
            StateContractType: StateContractType,
            Metadata: new AgentImplementationMetadata(
                Kind: Kind,
                ImplementationClrTypeName: ImplementationType.FullName ?? ImplementationType.Name,
                StateSchemaVersion: StateSchemaVersion),
            StateMigrations: migrations);
    }

    private IReadOnlyList<ActorStateMigrationStep> BuildMigrationChain()
    {
        var migrationTypes = StateMigrationTypes ?? [];
        var prebuiltSteps = PrebuiltStateMigrationSteps ?? [];
        if (StateSchemaVersion == 0)
        {
            if (migrationTypes.Count > 0 || prebuiltSteps.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Agent kind '{Kind}' declares migrations but supports only state schema version zero.");
            }

            return [];
        }

        if (!typeof(IMessage).IsAssignableFrom(StateContractType))
        {
            throw new InvalidOperationException(
                $"Agent kind '{Kind}' declares state migrations for non-protobuf state contract '{StateContractType.FullName}'.");
        }

        var steps = migrationTypes
            .Select(BuildMigrationStep)
            .Concat(prebuiltSteps.Select(ValidatePrebuiltMigrationStep))
            .OrderBy(static step => step.FromStateVersion)
            .ToArray();
        var byFromVersion = new Dictionary<int, ActorStateMigrationStep>();
        foreach (var step in steps)
        {
            if (!byFromVersion.TryAdd(step.FromStateVersion, step))
            {
                throw new InvalidOperationException(
                    $"Agent kind '{Kind}' declares multiple migrations from state schema version {step.FromStateVersion}.");
            }
        }

        for (var version = 0; version < StateSchemaVersion; version++)
        {
            if (!byFromVersion.TryGetValue(version, out var step) ||
                step.ToStateVersion != version + 1)
            {
                throw new InvalidOperationException(
                    $"Agent kind '{Kind}' has no complete typed state migration chain from version {version} to {version + 1}.");
            }
        }

        if (steps.Any(step => step.ToStateVersion > StateSchemaVersion))
        {
            throw new InvalidOperationException(
                $"Agent kind '{Kind}' declares a migration beyond supported state schema version {StateSchemaVersion}.");
        }

        return steps;
    }

    private ActorStateMigrationStep ValidatePrebuiltMigrationStep(ActorStateMigrationStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        if (step.StateContractType != StateContractType)
        {
            throw new InvalidOperationException(
                $"State migration '{step.MigrationType.FullName}' targets '{step.StateContractType.FullName}', " +
                $"but agent kind '{Kind}' owns '{StateContractType.FullName}'.");
        }
        if (step.FromStateVersion < 0 || step.ToStateVersion != step.FromStateVersion + 1)
        {
            throw new InvalidOperationException(
                $"State migration '{step.MigrationType.FullName}' must declare one consecutive non-negative version step.");
        }
        ValidateMigrationAdmissionContract(step);
        return step;
    }

    private ActorStateMigrationStep BuildMigrationStep(Type migrationType)
    {
        var contracts = migrationType.GetInterfaces()
            .Where(static candidate =>
                candidate.IsGenericType &&
                candidate.GetGenericTypeDefinition() == typeof(IActorStateMigration<>))
            .ToArray();
        if (contracts.Length != 1)
        {
            throw new InvalidOperationException(
                $"State migration '{migrationType.FullName}' must implement exactly one IActorStateMigration<TState> contract.");
        }

        var contract = contracts[0];
        var migrationStateType = contract.GetGenericArguments()[0];
        if (migrationStateType != StateContractType)
        {
            throw new InvalidOperationException(
                $"State migration '{migrationType.FullName}' targets '{migrationStateType.FullName}', " +
                $"but agent kind '{Kind}' owns '{StateContractType.FullName}'.");
        }

        var constructors = migrationType.GetConstructors(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (constructors.Length != 1 || constructors[0].GetParameters().Length != 0)
        {
            throw new InvalidOperationException(
                $"State migration '{migrationType.FullName}' must have exactly one zero-dependency constructor.");
        }

        var instance = Activator.CreateInstance(migrationType, nonPublic: true)
            ?? throw new InvalidOperationException(
                $"State migration '{migrationType.FullName}' could not be constructed.");
        var fromVersion = (int)(contract.GetProperty("FromStateVersion")!
            .GetValue(instance) ?? -1);
        var toVersion = (int)(contract.GetProperty("ToStateVersion")!
            .GetValue(instance) ?? -1);
        if (fromVersion < 0 || toVersion != fromVersion + 1)
        {
            throw new InvalidOperationException(
                $"State migration '{migrationType.FullName}' must declare one consecutive non-negative version step.");
        }

        var declaration = migrationType.GetCustomAttribute<ActorStateMigrationAttribute>(inherit: false)
            ?? throw new InvalidOperationException(
                $"State migration '{migrationType.FullName}' has no [ActorStateMigration] declaration.");
        if (!string.Equals(declaration.AgentKind, Kind, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"State migration '{migrationType.FullName}' declares agent kind " +
                $"'{declaration.AgentKind}', but is registered for agent kind '{Kind}'.");
        }

        var applyMethod = contract.GetMethod("Apply")!;
        var step = new ActorStateMigrationStep(
            fromVersion,
            toVersion,
            StateContractType,
            migrationType,
            bytes =>
            {
                var applyInstance = Activator.CreateInstance(migrationType, nonPublic: true)
                    ?? throw new InvalidOperationException(
                        $"State migration '{migrationType.FullName}' could not be constructed.");
                var input = Activator.CreateInstance(StateContractType) as IMessage
                    ?? throw new InvalidOperationException(
                        $"State contract '{StateContractType.FullName}' could not be constructed.");
                input.MergeFrom(bytes);
                var output = applyMethod.Invoke(applyInstance, [input]) as IMessage
                    ?? throw new InvalidOperationException(
                        $"State migration '{migrationType.FullName}' returned no protobuf state.");
                if (output.GetType() != StateContractType)
                {
                    throw new InvalidOperationException(
                        $"State migration '{migrationType.FullName}' returned '{output.GetType().FullName}', " +
                        $"expected '{StateContractType.FullName}'.");
                }

                return output.ToByteArray();
            },
            declaration.RequiredCapability,
            declaration.RequiredContractId.Trim(),
            declaration.RequiredContractVersion,
            declaration.RequiredGateStatus);
        ValidateMigrationAdmissionContract(step);
        return step;
    }

    private void ValidateMigrationAdmissionContract(ActorStateMigrationStep step)
    {
        if (step.RequiredCapability == RuntimeFleetCapability.Unspecified ||
            !System.Enum.IsDefined(step.RequiredCapability))
        {
            throw new InvalidOperationException(
                $"State migration '{step.MigrationType.FullName}' must declare an exact fleet capability.");
        }
        if (string.IsNullOrWhiteSpace(step.RequiredContractId) ||
            step.RequiredContractVersion <= 0)
        {
            throw new InvalidOperationException(
                $"State migration '{step.MigrationType.FullName}' must declare an exact versioned reader contract.");
        }
        if (step.RequiredGateStatus is not RuntimeFleetCapabilityGateStatus.Open and
            not RuntimeFleetCapabilityGateStatus.Quiesced)
        {
            throw new InvalidOperationException(
                $"State migration '{step.MigrationType.FullName}' must require OPEN or QUIESCED fleet evidence.");
        }
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
            StateContractType: stateContract,
            StateSchemaVersion: gAgent.StateSchemaVersion);
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

using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.Scripting.Projection.Orchestration;
using Aevatar.Scripting.Projection.Projectors;
using Aevatar.Scripting.Projection.ReadModels;
using Aevatar.Scripting.Projection.Reducers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.Scripting.Projection.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddScriptingProjectionComponents(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IProjectionEventReducer<ScriptExecutionReadModel, ScriptProjectionContext>,
            ScriptRunDomainEventCommittedReducer>());

        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IProjectionEventReducer<ScriptEvolutionReadModel, ScriptEvolutionProjectionContext>,
            ScriptEvolutionProposedEventReducer>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IProjectionEventReducer<ScriptEvolutionReadModel, ScriptEvolutionProjectionContext>,
            ScriptEvolutionValidatedEventReducer>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IProjectionEventReducer<ScriptEvolutionReadModel, ScriptEvolutionProjectionContext>,
            ScriptEvolutionRejectedEventReducer>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IProjectionEventReducer<ScriptEvolutionReadModel, ScriptEvolutionProjectionContext>,
            ScriptEvolutionPromotedEventReducer>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IProjectionEventReducer<ScriptEvolutionReadModel, ScriptEvolutionProjectionContext>,
            ScriptEvolutionRolledBackEventReducer>());

        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IProjectionProjector<ScriptProjectionContext, IReadOnlyList<string>>,
            ScriptExecutionReadModelProjector>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IProjectionProjector<ScriptEvolutionProjectionContext, IReadOnlyList<string>>,
            ScriptEvolutionReadModelProjector>());

        return services;
    }
}

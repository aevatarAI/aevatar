using Aevatar.App.Application.Projection.Orchestration;
using Aevatar.App.Application.Projection.ReadModels;
using Aevatar.App.Application.Projection.Stores;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.DependencyInjection;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Core.Streaming;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.App.Application.Projection.DependencyInjection;

public static class AppProjectionServiceCollectionExtensions
{
    private static readonly Type ReducerContract = typeof(IProjectionEventReducer<,>);
    private static readonly Type ProjectorContract = typeof(IProjectionProjector<,>);

    public static IServiceCollection AddAppProjection(this IServiceCollection services)
    {
        services.TryAddSingleton<IProjectionDocumentStore<AppUserAccountReadModel, string>>(
            _ => new AppInMemoryDocumentStore<AppUserAccountReadModel, string>(m => m.Id));

        services.TryAddSingleton<IProjectionDocumentStore<AppUserProfileReadModel, string>>(
            _ => new AppInMemoryDocumentStore<AppUserProfileReadModel, string>(m => m.Id));

        services.TryAddSingleton<IProjectionDocumentStore<AppSyncEntityReadModel, string>>(
            _ => new AppInMemoryDocumentStore<AppSyncEntityReadModel, string>(m => m.Id));

        services.TryAddSingleton<IProjectionDocumentStore<AppAuthLookupReadModel, string>>(
            _ => new AppInMemoryDocumentStore<AppAuthLookupReadModel, string>(m => m.Id));

        services.TryAddSingleton<IProjectionDocumentStore<AppSyncEntityLastResultReadModel, string>>(
            _ => new AppInMemoryDocumentStore<AppSyncEntityLastResultReadModel, string>(m => m.Id));

        services.TryAddSingleton<IProjectionDocumentStore<AppUserAffiliateReadModel, string>>(
            _ => new AppInMemoryDocumentStore<AppUserAffiliateReadModel, string>(m => m.Id));

        services.TryAddSingleton<IProjectionDocumentStore<AppPaymentTransactionReadModel, string>>(
            _ => new AppInMemoryDocumentStore<AppPaymentTransactionReadModel, string>(m => m.Id));

        RegisterFromAssembly(services, typeof(AppProjectionServiceCollectionExtensions).Assembly);

        services.TryAddSingleton<IAppProjectionContextFactory, DefaultAppProjectionContextFactory>();
        services.TryAddSingleton<IProjectionCoordinator<AppProjectionContext, object?>, ProjectionCoordinator<AppProjectionContext, object?>>();
        services.TryAddSingleton<IProjectionDispatcher<AppProjectionContext>, ProjectionDispatcher<AppProjectionContext, object?>>();
        services.TryAddSingleton<IProjectionSubscriptionRegistry<AppProjectionContext>, ProjectionSubscriptionRegistry<AppProjectionContext>>();
        services.TryAddSingleton(typeof(IActorStreamSubscriptionHub<>), typeof(ActorStreamSubscriptionHub<>));
        services.TryAddSingleton<IProjectionLifecycleService<AppProjectionContext, object?>, ProjectionLifecycleService<AppProjectionContext, object?>>();
        services.TryAddSingleton<IAppProjectionManager, AppProjectionManager>();

        return services;
    }

    private static void RegisterFromAssembly(IServiceCollection services, System.Reflection.Assembly assembly)
    {
        ProjectionAssemblyRegistration.RegisterProjectionExtensionsFromAssembly(
            services,
            assembly,
            typeof(IProjectionEventReducer<AppUserAccountReadModel, AppProjectionContext>),
            typeof(IProjectionProjector<AppProjectionContext, object?>),
            ReducerContract,
            ProjectorContract);

        ProjectionAssemblyRegistration.RegisterProjectionExtensionsFromAssembly(
            services,
            assembly,
            typeof(IProjectionEventReducer<AppUserProfileReadModel, AppProjectionContext>),
            typeof(IProjectionProjector<AppProjectionContext, object?>),
            ReducerContract,
            ProjectorContract);

        ProjectionAssemblyRegistration.RegisterProjectionExtensionsFromAssembly(
            services,
            assembly,
            typeof(IProjectionEventReducer<AppSyncEntityReadModel, AppProjectionContext>),
            typeof(IProjectionProjector<AppProjectionContext, object?>),
            ReducerContract,
            ProjectorContract);

        ProjectionAssemblyRegistration.RegisterProjectionExtensionsFromAssembly(
            services,
            assembly,
            typeof(IProjectionEventReducer<AppAuthLookupReadModel, AppProjectionContext>),
            typeof(IProjectionProjector<AppProjectionContext, object?>),
            ReducerContract,
            ProjectorContract);

        ProjectionAssemblyRegistration.RegisterProjectionExtensionsFromAssembly(
            services,
            assembly,
            typeof(IProjectionEventReducer<AppUserAffiliateReadModel, AppProjectionContext>),
            typeof(IProjectionProjector<AppProjectionContext, object?>),
            ReducerContract,
            ProjectorContract);

        ProjectionAssemblyRegistration.RegisterProjectionExtensionsFromAssembly(
            services,
            assembly,
            typeof(IProjectionEventReducer<AppPaymentTransactionReadModel, AppProjectionContext>),
            typeof(IProjectionProjector<AppProjectionContext, object?>),
            ReducerContract,
            ProjectorContract);
    }
}

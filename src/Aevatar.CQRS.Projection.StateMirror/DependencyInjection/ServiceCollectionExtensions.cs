using Aevatar.CQRS.Projection.StateMirror.Abstractions;
using Aevatar.CQRS.Projection.StateMirror.Configuration;
using Aevatar.CQRS.Projection.StateMirror.Services;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.CQRS.Projection.StateMirror.DependencyInjection;

public static class StateMirrorServiceCollectionExtensions
{
    public static IServiceCollection AddJsonStateMirrorProjection<TState, TReadModel>(
        this IServiceCollection services,
        Action<StateMirrorProjectionOptions>? configure = null)
        where TState : class
        where TReadModel : class
    {
        services.TryAddSingleton<IStateMirrorProjection<TState, TReadModel>>(_ =>
        {
            var options = new StateMirrorProjectionOptions();
            configure?.Invoke(options);
            return new JsonStateMirrorProjection<TState, TReadModel>(options);
        });
        return services;
    }

    public static IServiceCollection AddJsonStateMirrorReadModelProjector<TState, TReadModel>(
        this IServiceCollection services,
        Action<StateMirrorProjectionOptions>? configure = null)
        where TState : class
        where TReadModel : class, IProjectionReadModel
    {
        return services.AddJsonStateMirrorReadModelProjector<TState, TReadModel, string>(configure);
    }

    public static IServiceCollection AddJsonStateMirrorReadModelProjector<TState, TReadModel, TKey>(
        this IServiceCollection services,
        Action<StateMirrorProjectionOptions>? configure = null)
        where TState : class
        where TReadModel : class, IProjectionReadModel
    {
        services.AddJsonStateMirrorProjection<TState, TReadModel>(configure);
        services.TryAddSingleton<IStateMirrorReadModelProjector<TState, TReadModel, TKey>,
            StateMirrorReadModelProjector<TState, TReadModel, TKey>>();
        return services;
    }
}

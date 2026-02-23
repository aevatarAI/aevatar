using Aevatar.CQRS.Projection.StateMirror.Abstractions;
using Aevatar.CQRS.Projection.StateMirror.Configuration;
using Aevatar.CQRS.Projection.StateMirror.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.CQRS.Projection.StateMirror.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddJsonStateMirrorProjection<TState, TReadModel>(
        this IServiceCollection services,
        Action<StateMirrorProjectionOptions>? configure = null)
        where TState : class
        where TReadModel : class
    {
        var options = new StateMirrorProjectionOptions();
        configure?.Invoke(options);

        services.TryAddSingleton(options);
        services.TryAddSingleton<IStateMirrorProjection<TState, TReadModel>,
            JsonStateMirrorProjection<TState, TReadModel>>();
        return services;
    }
}

using Aevatar.AI.Projection.Reducers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.AI.Projection.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAITextMessageStartProjectionReducer<TReadModel, TContext>(
        this IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IProjectionEventReducer<TReadModel, TContext>, TextMessageStartProjectionReducer<TReadModel, TContext>>());
        return services;
    }

    public static IServiceCollection AddAITextMessageContentProjectionReducer<TReadModel, TContext>(
        this IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IProjectionEventReducer<TReadModel, TContext>, TextMessageContentProjectionReducer<TReadModel, TContext>>());
        return services;
    }

    public static IServiceCollection AddAITextMessageEndProjectionReducer<TReadModel, TContext>(
        this IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IProjectionEventReducer<TReadModel, TContext>, TextMessageEndProjectionReducer<TReadModel, TContext>>());
        return services;
    }

    public static IServiceCollection AddAIToolCallProjectionReducer<TReadModel, TContext>(
        this IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IProjectionEventReducer<TReadModel, TContext>, ToolCallProjectionReducer<TReadModel, TContext>>());
        return services;
    }

    public static IServiceCollection AddAIToolResultProjectionReducer<TReadModel, TContext>(
        this IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IProjectionEventReducer<TReadModel, TContext>, ToolResultProjectionReducer<TReadModel, TContext>>());
        return services;
    }

    public static IServiceCollection AddAllAIProjectionEventReducers<TReadModel, TContext>(
        this IServiceCollection services)
    {
        return services
            .AddAITextMessageStartProjectionReducer<TReadModel, TContext>()
            .AddAITextMessageContentProjectionReducer<TReadModel, TContext>()
            .AddAITextMessageEndProjectionReducer<TReadModel, TContext>()
            .AddAIToolCallProjectionReducer<TReadModel, TContext>()
            .AddAIToolResultProjectionReducer<TReadModel, TContext>();
    }
}

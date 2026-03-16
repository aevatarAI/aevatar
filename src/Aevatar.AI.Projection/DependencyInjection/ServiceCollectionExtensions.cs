using Aevatar.AI.Abstractions;
using Aevatar.AI.Projection.Appliers;
using Aevatar.Foundation.Projection.ReadModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.AI.Projection.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAITextMessageStartProjectionApplier<TReadModel, TContext>(
        this IServiceCollection services)
        where TReadModel : class, IHasProjectionTimeline
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IProjectionEventApplier<TReadModel, TContext, TextMessageStartEvent>, AITextMessageStartProjectionApplier<TReadModel, TContext>>());
        return services;
    }

    public static IServiceCollection AddAITextMessageContentProjectionApplier<TReadModel, TContext>(
        this IServiceCollection services)
        where TReadModel : class, IHasProjectionTimeline
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IProjectionEventApplier<TReadModel, TContext, TextMessageContentEvent>, AITextMessageContentProjectionApplier<TReadModel, TContext>>());
        return services;
    }

    public static IServiceCollection AddAITextMessageEndProjectionApplier<TReadModel, TContext>(
        this IServiceCollection services)
        where TReadModel : class, IHasProjectionTimeline, IHasProjectionRoleReplies
        where TContext : class, IProjectionContext
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IProjectionEventApplier<TReadModel, TContext, TextMessageEndEvent>, AITextMessageEndProjectionApplier<TReadModel, TContext>>());
        return services;
    }

    public static IServiceCollection AddAIToolCallProjectionApplier<TReadModel, TContext>(
        this IServiceCollection services)
        where TReadModel : class, IHasProjectionTimeline
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IProjectionEventApplier<TReadModel, TContext, ToolCallEvent>, AIToolCallProjectionApplier<TReadModel, TContext>>());
        return services;
    }

    public static IServiceCollection AddAIToolResultProjectionApplier<TReadModel, TContext>(
        this IServiceCollection services)
        where TReadModel : class, IHasProjectionTimeline
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IProjectionEventApplier<TReadModel, TContext, ToolResultEvent>, AIToolResultProjectionApplier<TReadModel, TContext>>());
        return services;
    }

    public static IServiceCollection AddAIDefaultProjectionAppliers<TReadModel, TContext>(
        this IServiceCollection services)
        where TReadModel : class, IHasProjectionTimeline, IHasProjectionRoleReplies
        where TContext : class, IProjectionContext
    {
        return services
            .AddAITextMessageStartProjectionApplier<TReadModel, TContext>()
            .AddAITextMessageContentProjectionApplier<TReadModel, TContext>()
            .AddAITextMessageEndProjectionApplier<TReadModel, TContext>()
            .AddAIToolCallProjectionApplier<TReadModel, TContext>()
            .AddAIToolResultProjectionApplier<TReadModel, TContext>();
    }
}

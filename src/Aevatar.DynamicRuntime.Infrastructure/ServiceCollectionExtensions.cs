using Aevatar.DynamicRuntime.Abstractions.Contracts;
using Aevatar.DynamicRuntime.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.DynamicRuntime.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddDynamicRuntime(this IServiceCollection services)
    {
        services.TryAddSingleton<IDynamicRuntimeReadStore, InMemoryDynamicRuntimeReadStore>();
        services.TryAddSingleton<IIdempotencyPort, InMemoryIdempotencyPort>();
        services.TryAddSingleton<IConcurrencyTokenPort, InMemoryConcurrencyTokenPort>();
        services.TryAddSingleton<IImageReferenceResolver, DefaultImageReferenceResolver>();
        services.TryAddSingleton<IScriptComposeSpecValidator, DefaultScriptComposeSpecValidator>();
        services.TryAddSingleton<IScriptComposeReconcilePort, DefaultScriptComposeReconcilePort>();
        services.TryAddSingleton<DynamicRuntimeApplicationService>();
        services.TryAddSingleton<IDynamicRuntimeCommandService>(sp => sp.GetRequiredService<DynamicRuntimeApplicationService>());
        services.TryAddSingleton<IDynamicRuntimeQueryService>(sp => sp.GetRequiredService<DynamicRuntimeApplicationService>());
        return services;
    }
}

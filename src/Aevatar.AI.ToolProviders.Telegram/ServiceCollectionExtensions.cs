using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.AI.ToolProviders.Telegram;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddTelegramTools(
        this IServiceCollection services,
        Action<TelegramToolOptions>? configure = null)
    {
        var options = new TelegramToolOptions();
        configure?.Invoke(options);

        services.TryAddSingleton(options);
        services.TryAddSingleton<NyxIdToolOptions>();
        services.TryAddSingleton<NyxIdApiClient>();
        services.TryAddSingleton<ITelegramNyxClient, TelegramNyxClient>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAgentToolSource, TelegramAgentToolSource>());

        return services;
    }
}

using Aevatar.Workflow.Sdk.Options;
using Aevatar.Workflow.Sdk.Streaming;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Aevatar.Workflow.Sdk.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAevatarWorkflowSdk(
        this IServiceCollection services,
        Action<AevatarWorkflowClientOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var optionsBuilder = services.AddOptions<AevatarWorkflowClientOptions>();
        if (configure != null)
            optionsBuilder.Configure(configure);

        services.TryAddSingleton<IWorkflowChatTransport, SseChatTransport>();
        services.AddHttpClient<IAevatarWorkflowClient, AevatarWorkflowClient>((sp, httpClient) =>
        {
            var options = sp.GetRequiredService<IOptions<AevatarWorkflowClientOptions>>().Value;
            if (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var baseUri))
            {
                throw new InvalidOperationException(
                    $"Invalid Aevatar Workflow SDK base url: '{options.BaseUrl}'.");
            }

            httpClient.BaseAddress = baseUri;
            foreach (var (key, value) in options.DefaultHeaders)
                httpClient.DefaultRequestHeaders.TryAddWithoutValidation(key, value);
        });

        return services;
    }

    public static IServiceCollection AddAevatarWorkflowSdk(
        this IServiceCollection services,
        string baseUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        return services.AddAevatarWorkflowSdk(options => options.BaseUrl = baseUrl.Trim());
    }
}

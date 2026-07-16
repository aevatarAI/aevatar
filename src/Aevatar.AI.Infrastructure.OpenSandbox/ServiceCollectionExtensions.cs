using Aevatar.AI.Abstractions.CodexExecution;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Aevatar.AI.Infrastructure.OpenSandbox;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddOpenSandboxCodexExecution(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<OpenSandboxCodexOptions>()
            .Bind(configuration.GetSection(OpenSandboxCodexOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IValidateOptions<OpenSandboxCodexOptions>,
            OpenSandboxCodexOptionsValidator>());
        services.TryAddSingleton<IManagedCodexCredentialProvider, NyxIdManagedCodexCredentialProvider>();
        services.TryAddSingleton<IOpenSandboxCodexClient, SdkOpenSandboxCodexClient>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            ICodexExecutionPort,
            OpenSandboxCodexExecutionAdapter>());
        return services;
    }
}

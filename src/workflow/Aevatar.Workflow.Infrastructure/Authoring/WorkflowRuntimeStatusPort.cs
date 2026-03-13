using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.Configuration;
using Aevatar.Workflow.Application.Abstractions.Authoring;
using Microsoft.Extensions.Configuration;

namespace Aevatar.Workflow.Infrastructure.Authoring;

internal sealed class WorkflowRuntimeStatusPort : IWorkflowRuntimeStatusPort
{
    private readonly ILLMProviderFactory? _providerFactory;
    private readonly IAevatarSecretsStore _secretsStore;
    private readonly IConfiguration _configuration;

    public WorkflowRuntimeStatusPort(
        IEnumerable<ILLMProviderFactory> providerFactories,
        IEnumerable<IAevatarSecretsStore> secretsStores,
        IConfiguration configuration)
    {
        _providerFactory = providerFactories?.FirstOrDefault();
        _secretsStore = secretsStores?.FirstOrDefault()
            ?? new AevatarSecretsStore();
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    }

    public Task<WorkflowLlmStatus> GetStatusAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (_providerFactory == null)
        {
            return Task.FromResult(new WorkflowLlmStatus
            {
                Available = false,
            });
        }

        try
        {
            var providers = _providerFactory.GetAvailableProviders()
                .Where(provider => !string.IsNullOrWhiteSpace(provider))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(provider => provider, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (providers.Count == 0)
            {
                return Task.FromResult(new WorkflowLlmStatus
                {
                    Available = false,
                    Providers = providers,
                });
            }

            var provider = _providerFactory.GetDefault();
            return Task.FromResult(new WorkflowLlmStatus
            {
                Available = true,
                Provider = provider.Name,
                Model = ResolveDefaultProviderModel(provider.Name),
                Providers = providers,
            });
        }
        catch
        {
            return Task.FromResult(new WorkflowLlmStatus
            {
                Available = false,
            });
        }
    }

    private string? ResolveDefaultProviderModel(string providerName)
    {
        var configuredModel = _secretsStore.Get($"LLMProviders:Providers:{providerName}:Model");
        if (!string.IsNullOrWhiteSpace(configuredModel))
            return configuredModel.Trim();

        return _configuration["Models:DefaultModel"];
    }
}

using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.Ornn;
using Aevatar.GAgents.Channel.Identity.Broker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Aevatar.Mainnet.Host.Api.Hosting;

internal sealed class MainnetNyxIdResourcePolicyValidator(
    IConfiguration configuration,
    OrnnOptions ornnOptions,
    NyxIdToolOptions nyxIdToolOptions) : IValidateOptions<NyxIdBrokerOptions>
{
    public ValidateOptionsResult Validate(string? name, NyxIdBrokerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();
        var providerLlmSlug = ResolveConfiguredSlug(
            configuration["Aevatar:NyxId:DefaultRoute"],
            LlmDefaults.NyxIdRoute);
        if (!string.Equals(
                options.RequiredLlmServiceSlug?.Trim(),
                providerLlmSlug,
                StringComparison.Ordinal))
        {
            failures.Add(
                $"{nameof(NyxIdBrokerOptions.RequiredLlmServiceSlug)} must match the NyxID LLM provider route " +
                $"'{providerLlmSlug}' resolved from Aevatar:NyxId:DefaultRoute.");
        }

        var providerOrnnSlug = ornnOptions.NyxIdSlug?.Trim() ?? string.Empty;
        var requiredAdditionalSlugs = options.AdditionalRequiredServiceSlugs?
            .Select(static serviceSlug => serviceSlug?.Trim())
            .Where(static serviceSlug => !string.IsNullOrEmpty(serviceSlug))
            .Select(static serviceSlug => serviceSlug!)
            .ToHashSet(StringComparer.Ordinal)
            ?? [];
        if (!requiredAdditionalSlugs.Contains(providerOrnnSlug))
        {
            failures.Add(
                $"{nameof(NyxIdBrokerOptions.AdditionalRequiredServiceSlugs)} must contain the Ornn provider route " +
                $"'{providerOrnnSlug}' resolved from Aevatar:Ornn:NyxIdSlug.");
        }

        var providerSandboxSlug = nyxIdToolOptions.SandboxServiceSlug?.Trim() ?? string.Empty;
        if (!requiredAdditionalSlugs.Contains(providerSandboxSlug))
        {
            failures.Add(
                $"{nameof(NyxIdBrokerOptions.AdditionalRequiredServiceSlugs)} must contain the sandbox provider route " +
                $"'{providerSandboxSlug}' resolved from Aevatar:NyxId:SandboxServiceSlug.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static string ResolveConfiguredSlug(string? configured, string fallback) =>
        string.IsNullOrWhiteSpace(configured) ? fallback : configured.Trim();
}

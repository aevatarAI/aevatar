using Microsoft.Extensions.Options;

namespace Aevatar.GAgents.Channel.Identity.Broker;

internal sealed class NyxIdBrokerOptionsValidator : IValidateOptions<NyxIdBrokerOptions>
{
    public ValidateOptionsResult Validate(string? name, NyxIdBrokerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();
        ValidateServiceSlug(
            options.RequiredLlmServiceSlug,
            nameof(NyxIdBrokerOptions.RequiredLlmServiceSlug),
            optional: true,
            failures);

        if (options.AdditionalRequiredServiceSlugs is null)
        {
            failures.Add($"{nameof(NyxIdBrokerOptions.AdditionalRequiredServiceSlugs)} must not be null.");
        }
        else
        {
            for (var index = 0; index < options.AdditionalRequiredServiceSlugs.Length; index++)
            {
                ValidateServiceSlug(
                    options.AdditionalRequiredServiceSlugs[index],
                    $"{nameof(NyxIdBrokerOptions.AdditionalRequiredServiceSlugs)}[{index}]",
                    optional: false,
                    failures);
            }
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidateServiceSlug(
        string? serviceSlug,
        string optionName,
        bool optional,
        ICollection<string> failures)
    {
        if (optional && string.IsNullOrWhiteSpace(serviceSlug))
            return;

        if (!AevatarOAuthClientResources.IsValidServiceSlug(serviceSlug))
        {
            failures.Add(
                $"{optionName} must be a 1-80 character NyxID service slug containing lowercase ASCII " +
                "letters, digits, and single inner hyphens.");
        }
    }
}

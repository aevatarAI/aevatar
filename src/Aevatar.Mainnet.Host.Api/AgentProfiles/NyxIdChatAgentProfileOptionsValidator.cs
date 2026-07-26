using Microsoft.Extensions.Options;

namespace Aevatar.Mainnet.Host.Api.AgentProfiles;

public sealed class NyxIdChatAgentProfileOptionsValidator
    : IValidateOptions<NyxIdChatAgentProfileOptions>
{
    public ValidateOptionsResult Validate(
        string? name,
        NyxIdChatAgentProfileOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Enabled && string.IsNullOrWhiteSpace(options.ReleaseSpecPath))
        {
            return ValidateOptionsResult.Fail(
                "An enabled NyxID chat Agent Profile rollout requires ReleaseSpecPath.");
        }

        if (!options.Enabled && !string.IsNullOrWhiteSpace(options.ReleaseSpecPath))
        {
            return ValidateOptionsResult.Fail(
                "A disabled NyxID chat Agent Profile rollout cannot configure ReleaseSpecPath.");
        }

        return ValidateOptionsResult.Success;
    }
}

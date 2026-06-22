using Microsoft.Extensions.Options;

namespace Aevatar.Foundation.VoicePresence.Hosting;

public sealed class VoiceWebSocketAttachOptionsValidator : IValidateOptions<VoiceWebSocketAttachOptions>
{
    public ValidateOptionsResult Validate(string? name, VoiceWebSocketAttachOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();
        if (options.AttachTimeout <= TimeSpan.Zero)
            failures.Add($"{nameof(VoiceWebSocketAttachOptions.AttachTimeout)} must be positive.");

        if (options.CloseWaitTimeout <= TimeSpan.Zero)
            failures.Add($"{nameof(VoiceWebSocketAttachOptions.CloseWaitTimeout)} must be positive.");

        if (options.PolicyViolationCloseTimeout <= TimeSpan.Zero)
            failures.Add($"{nameof(VoiceWebSocketAttachOptions.PolicyViolationCloseTimeout)} must be positive.");

        if (options.ConflictRetryAfterSeconds <= 0)
            failures.Add($"{nameof(VoiceWebSocketAttachOptions.ConflictRetryAfterSeconds)} must be positive.");

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}

using Microsoft.Extensions.Options;

namespace Aevatar.Audit.Core.Identity;

internal sealed class AuditActorIdentityHasherOptionsValidator : IValidateOptions<AuditActorIdentityHasherOptions>
{
    public ValidateOptionsResult Validate(string? name, AuditActorIdentityHasherOptions options)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(options.ActiveKeyId))
        {
            errors.Add("ActiveKeyId is required.");
        }

        if (options.Keys.Count == 0)
        {
            errors.Add("At least one key is required.");
        }

        var keyIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in options.Keys)
        {
            if (string.IsNullOrWhiteSpace(key.KeyId))
            {
                errors.Add("Every key requires KeyId.");
                continue;
            }

            if (!keyIds.Add(key.KeyId.Trim()))
            {
                errors.Add($"Duplicate key id '{key.KeyId.Trim()}'.");
            }

            var hasPlainKey = !string.IsNullOrWhiteSpace(key.Key);
            var hasBase64Key = !string.IsNullOrWhiteSpace(key.KeyBase64);
            if (hasPlainKey == hasBase64Key)
            {
                errors.Add($"Key '{key.KeyId.Trim()}' must configure exactly one of Key or KeyBase64.");
                continue;
            }

            try
            {
                if (AuditActorIdentityHasherKeyMaterial.Resolve(key).Length < AuditActorIdentityHasherKeyMaterial.MinimumKeyBytes)
                {
                    errors.Add($"Key '{key.KeyId.Trim()}' is too short.");
                }
            }
            catch (FormatException)
            {
                errors.Add($"Key '{key.KeyId.Trim()}' KeyBase64 is not valid base64.");
            }
        }

        if (!string.IsNullOrWhiteSpace(options.ActiveKeyId) &&
            !keyIds.Contains(options.ActiveKeyId.Trim()))
        {
            errors.Add("ActiveKeyId must match a configured key.");
        }

        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);
    }
}

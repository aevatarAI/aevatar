namespace Aevatar.Foundation.VoicePresence.Abstractions;

public static class VoiceWireContractDefaults
{
    public const string CurrentWireContractVersion = "1.0";
    public const int MaxInputImageBytes = 512000;
    public const int MaxInputImageControlFrameBytes = ((MaxInputImageBytes + 2) / 3 * 4) + 1024;

    public static IReadOnlyList<string> SupportedInputImageMediaTypes { get; } =
    [
        "image/jpeg",
        "image/png",
    ];

    public static VoiceInputImagePolicy CreateInputImagePolicy()
    {
        var policy = new VoiceInputImagePolicy
        {
            MaxBytes = MaxInputImageBytes,
        };
        policy.AllowedMediaTypes.Add(SupportedInputImageMediaTypes);
        return policy;
    }

    public static bool IsSupportedInputImageMediaType(string? mediaType)
    {
        if (string.IsNullOrWhiteSpace(mediaType))
            return false;

        return SupportedInputImageMediaTypes.Contains(mediaType.Trim(), StringComparer.OrdinalIgnoreCase);
    }

    public static string FormatSupportedInputImageMediaTypes() =>
        string.Join(" or ", SupportedInputImageMediaTypes);
}

using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.Channel.Abstractions;

/// <summary>
/// Helpers for constructing transport emit results.
/// </summary>
public sealed partial class EmitResult
{
    /// <summary>
    /// Gets or sets the retry delay as a CLR <see cref="TimeSpan"/>.
    /// </summary>
    public TimeSpan? RetryAfterTimeSpan
    {
        get => RetryAfter == null ? null : RetryAfter.ToTimeSpan();
        set => RetryAfter = value.HasValue ? Duration.FromTimeSpan(value.Value) : null;
    }

    /// <summary>
    /// Creates one successful emit result.
    /// </summary>
    public static EmitResult Sent(
        string sentActivityId,
        ComposeCapability capability = ComposeCapability.Exact,
        string? platformMessageId = null) => new()
    {
        Success = true,
        SentActivityId = NormalizeRequired(sentActivityId, nameof(sentActivityId)),
        Capability = capability,
        PlatformMessageId = string.IsNullOrWhiteSpace(platformMessageId) ? string.Empty : platformMessageId.Trim(),
    };

    /// <summary>
    /// Creates one failed emit result.
    /// </summary>
    public static EmitResult Failed(
        string errorCode,
        string? errorMessage = null,
        TimeSpan? retryAfter = null,
        ComposeCapability capability = ComposeCapability.Unsupported) =>
        Failed(
            errorCode,
            errorMessage,
            retryAfter,
            capability,
            FailureKind.Unspecified);

    /// <summary>
    /// Creates one failed emit result with typed adapter diagnostics.
    /// </summary>
    public static EmitResult Failed(
        string errorCode,
        string? errorMessage,
        TimeSpan? retryAfter,
        ComposeCapability capability,
        FailureKind failureKind,
        int httpStatus = 0,
        string? rawErrorKey = null,
        int rawErrorCode = 0)
    {
        var result = new EmitResult
        {
            Success = false,
            ErrorCode = NormalizeRequired(errorCode, nameof(errorCode)),
            ErrorMessage = string.IsNullOrWhiteSpace(errorMessage) ? string.Empty : errorMessage.Trim(),
            Capability = capability,
            FailureKind = failureKind,
            HttpStatus = httpStatus,
            RawErrorKey = string.IsNullOrWhiteSpace(rawErrorKey) ? string.Empty : rawErrorKey.Trim(),
            RawErrorCode = rawErrorCode,
        };
        result.RetryAfterTimeSpan = retryAfter;
        return result;
    }

    private static string NormalizeRequired(string value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Value cannot be empty.", paramName);

        return value.Trim();
    }
}

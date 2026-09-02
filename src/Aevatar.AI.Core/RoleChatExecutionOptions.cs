namespace Aevatar.AI.Core;

public sealed class RoleChatExecutionOptions
{
    public const int DefaultMaxTurnDeadlineMs = 120_000;
    public const int DefaultPostCommitConfigRefreshTimeoutMs = 30_000;
    public const int DefaultPostTurnProcessingTimeoutMs = 30_000;

    public RoleChatExecutionOptions(
        int maxTurnDeadlineMs = DefaultMaxTurnDeadlineMs,
        int postCommitConfigRefreshTimeoutMs = DefaultPostCommitConfigRefreshTimeoutMs,
        int postTurnProcessingTimeoutMs = DefaultPostTurnProcessingTimeoutMs)
    {
        if (maxTurnDeadlineMs <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxTurnDeadlineMs),
                maxTurnDeadlineMs,
                "The maximum role chat turn deadline must be positive.");
        }

        if (postCommitConfigRefreshTimeoutMs <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(postCommitConfigRefreshTimeoutMs),
                postCommitConfigRefreshTimeoutMs,
                "The post-commit config refresh timeout must be positive.");
        }

        if (postTurnProcessingTimeoutMs <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(postTurnProcessingTimeoutMs),
                postTurnProcessingTimeoutMs,
                "The post-turn processing timeout must be positive.");
        }

        MaxTurnDeadlineMs = maxTurnDeadlineMs;
        PostCommitConfigRefreshTimeoutMs = postCommitConfigRefreshTimeoutMs;
        PostTurnProcessingTimeoutMs = postTurnProcessingTimeoutMs;
    }

    public int MaxTurnDeadlineMs { get; }
    public int PostCommitConfigRefreshTimeoutMs { get; }
    public int PostTurnProcessingTimeoutMs { get; }
}

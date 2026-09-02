namespace Aevatar.Foundation.VoicePresence.Hosting;

public sealed class VoiceWebSocketAttachOptions
{
    public TimeSpan AttachTimeout { get; set; } = TimeSpan.FromSeconds(10);

    public TimeSpan CloseWaitTimeout { get; set; } = TimeSpan.FromMinutes(5);

    public TimeSpan PolicyViolationCloseTimeout { get; set; } = TimeSpan.FromSeconds(2);

    public int ConflictRetryAfterSeconds { get; set; } = 1;
}

using Aevatar.AI.Abstractions;

namespace Aevatar.AI.Abstractions.ToolProviders;

public interface IWorkflowRunBackgroundDeliveryRegistrationPort
{
    Task<WorkflowRunBackgroundDeliveryReservationReceipt> ReserveAsync(
        WorkflowRunBackgroundDeliveryReservation reservation,
        CancellationToken ct = default);

    Task<WorkflowRunBackgroundDeliveryReceipt> RegisterAsync(
        WorkflowRunBackgroundDeliveryReservationReceipt reservationReceipt,
        WorkflowRunBackgroundDeliveryRegistration registration,
        CancellationToken ct = default);

    Task AbandonAsync(
        WorkflowRunBackgroundDeliveryReservationReceipt reservationReceipt,
        string reason,
        CancellationToken ct = default);
}

public sealed record WorkflowRunBackgroundDeliveryReservation
{
    public WorkflowRunBackgroundDeliveryReservation(
        string deliveryId,
        string expectedWorkflowCommandId,
        string channelPlatform,
        string replyMessageId,
        string platformMessageId,
        ChannelWorkflowResultDeliveryCredential workflowResultDeliveryCredential,
        string registrationScopeId,
        string botRegistrationId,
        long expiresAtUnixMs)
    {
        DeliveryId = Require(deliveryId, nameof(deliveryId));
        ExpectedWorkflowCommandId = Require(expectedWorkflowCommandId, nameof(expectedWorkflowCommandId));
        ChannelPlatform = Require(channelPlatform, nameof(channelPlatform));
        ReplyMessageId = Require(replyMessageId, nameof(replyMessageId));
        ArgumentNullException.ThrowIfNull(workflowResultDeliveryCredential);
        if (string.IsNullOrWhiteSpace(workflowResultDeliveryCredential.SecretReference?.Ref) ||
            string.IsNullOrWhiteSpace(workflowResultDeliveryCredential.SubjectId))
        {
            throw new ArgumentException(
                "Workflow background delivery reservation requires a typed credential handle.",
                nameof(workflowResultDeliveryCredential));
        }

        if (expiresAtUnixMs <= 0)
            throw new ArgumentOutOfRangeException(nameof(expiresAtUnixMs), "Reservation expiry must be positive.");
        if (expiresAtUnixMs > DateTimeOffset.MaxValue.ToUnixTimeMilliseconds())
            throw new ArgumentOutOfRangeException(
                nameof(expiresAtUnixMs),
                "Reservation expiry is outside the Unix timestamp range.");

        PlatformMessageId = platformMessageId?.Trim() ?? string.Empty;
        WorkflowResultDeliveryCredential = workflowResultDeliveryCredential.Clone();
        RegistrationScopeId = registrationScopeId?.Trim() ?? string.Empty;
        BotRegistrationId = botRegistrationId?.Trim() ?? string.Empty;
        ExpiresAtUnixMs = expiresAtUnixMs;
    }

    public string DeliveryId { get; }
    public string ExpectedWorkflowCommandId { get; }
    public string ChannelPlatform { get; }
    public string ReplyMessageId { get; }
    public string PlatformMessageId { get; }
    public ChannelWorkflowResultDeliveryCredential WorkflowResultDeliveryCredential { get; }
    public string RegistrationScopeId { get; }
    public string BotRegistrationId { get; }
    public long ExpiresAtUnixMs { get; }

    private static string Require(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value.Trim();
    }
}

public sealed record WorkflowRunBackgroundDeliveryReservationReceipt(
    string DeliveryActorId,
    string DeliveryId,
    string WorkflowCommandId);

public sealed record WorkflowRunBackgroundDeliveryRegistration(
    string DeliveryId,
    string WorkflowActorId,
    string WorkflowRunId,
    string WorkflowCommandId,
    string WorkflowCorrelationId,
    string StreamTopic,
    string ChannelPlatform,
    string ReplyMessageId,
    string PlatformMessageId,
    ChannelWorkflowResultDeliveryCredential WorkflowResultDeliveryCredential,
    string RegistrationScopeId,
    string BotRegistrationId);

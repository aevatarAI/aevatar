namespace Aevatar.Studio.Application.Studio.Abstractions;

public static class UserConfigCommandAckStage
{
    public const string Accepted = "accepted";
    public const string AdmissionRejected = "admission_rejected";
}

public sealed record UserConfigSaveReceipt(
    bool Accepted,
    string CommandId,
    string AckStage,
    string ActorId,
    string CorrelationId,
    DateTimeOffset AckedAtUtc);

public sealed record SaveUserConfigCommand(
    string? RuntimeMode = null,
    string? LocalRuntimeBaseUrl = null,
    string? RemoteRuntimeBaseUrl = null,
    string? GithubUsername = null,
    int? MaxToolRounds = null);

public interface IUserConfigService
{
    Task<UserConfig> GetAsync(CancellationToken ct = default);

    Task<UserConfigRuntimeView> GetRuntimeAsync(CancellationToken ct = default);

    Task<UserConfigSaveReceipt> SaveAsync(SaveUserConfigCommand command, CancellationToken ct = default);

    Task<UserConfigSaveReceipt> SaveAsync(
        string? bearerToken,
        SaveUserConfigCommand command,
        CancellationToken ct = default);

    Task<UserConfigSaveReceipt> SaveLlmPreferenceAsync(
        string? bearerToken,
        UserLlmPreferenceIntent intent,
        CancellationToken ct = default);
}

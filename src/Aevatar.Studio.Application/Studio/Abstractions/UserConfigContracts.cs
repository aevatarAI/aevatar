using System.Text.Json.Serialization;

namespace Aevatar.Studio.Application.Studio.Abstractions;

public static class UserConfigCommandAckStage
{
    public const string Accepted = "accepted";
}

public sealed record UserConfigSaveReceipt(
    [property: JsonPropertyName("accepted")] bool Accepted,
    [property: JsonPropertyName("commandId")] string CommandId,
    [property: JsonPropertyName("ackStage")] string AckStage,
    [property: JsonPropertyName("actorId")] string ActorId,
    [property: JsonPropertyName("correlationId")] string CorrelationId,
    [property: JsonPropertyName("ackedAtUtc")] DateTimeOffset AckedAtUtc);

public sealed record SaveUserConfigCommand(
    string? DefaultModel = null,
    string? PreferredLlmRoute = null,
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
        SaveUserLlmPreferenceCommand command,
        CancellationToken ct = default);
}

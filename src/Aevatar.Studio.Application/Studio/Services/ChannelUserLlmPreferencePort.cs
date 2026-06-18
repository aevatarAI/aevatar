using Aevatar.Studio.Application.Studio.Abstractions;

namespace Aevatar.Studio.Application.Studio.Services;

public sealed class ChannelUserLlmPreferencePort : IChannelUserLlmPreferencePort
{
    private readonly UserLlmPreferenceWriter _writer;

    public ChannelUserLlmPreferencePort(UserLlmPreferenceWriter writer)
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
    }

    public Task<UserConfigSaveReceipt> SaveAsync(
        string scopeId,
        string? bearerToken,
        SaveUserLlmPreferenceCommand command,
        CancellationToken ct) =>
        _writer.SaveAsync(scopeId, bearerToken, command, ct);

    public Task<UserConfigSaveReceipt> SaveSelectedOptionAsync(
        string scopeId,
        UserLlmOption option,
        string? model,
        bool preserveCurrentModelWhenMissing,
        CancellationToken ct) =>
        _writer.SaveSelectedOptionAsync(scopeId, option, model, preserveCurrentModelWhenMissing, ct);
}

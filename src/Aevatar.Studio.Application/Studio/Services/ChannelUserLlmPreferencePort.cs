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
        string bindingId,
        string? bearerToken,
        UserLlmPreferenceIntent intent,
        CancellationToken ct) =>
        _writer.SaveAsync(UserConfigResourceKey.ForChannelBinding(bindingId), bearerToken, intent, ct);
}

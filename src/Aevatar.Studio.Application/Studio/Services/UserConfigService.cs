using Aevatar.Studio.Application.Studio.Abstractions;

namespace Aevatar.Studio.Application.Studio.Services;

public sealed class UserConfigService : IUserConfigService
{
    private readonly IUserConfigQueryPort _queryPort;
    private readonly IUserConfigCommandService _commandService;
    private readonly UserLlmPreferenceWriter _llmPreferenceWriter;

    public UserConfigService(
        IUserConfigQueryPort queryPort,
        IUserConfigCommandService commandService,
        UserLlmPreferenceWriter llmPreferenceWriter)
    {
        _queryPort = queryPort ?? throw new ArgumentNullException(nameof(queryPort));
        _commandService = commandService ?? throw new ArgumentNullException(nameof(commandService));
        _llmPreferenceWriter = llmPreferenceWriter ?? throw new ArgumentNullException(nameof(llmPreferenceWriter));
    }

    public Task<UserConfig> GetAsync(CancellationToken ct = default) => _queryPort.GetAsync(ct);

    public async Task<UserConfigRuntimeView> GetRuntimeAsync(CancellationToken ct = default)
    {
        var config = await _queryPort.GetAsync(ct).ConfigureAwait(false);
        return UserConfigRuntime.BuildView(config);
    }

    public Task<UserConfigSaveReceipt> SaveAsync(SaveUserConfigCommand command, CancellationToken ct = default) =>
        SaveAsync(null, command, ct);

    public async Task<UserConfigSaveReceipt> SaveAsync(
        string? bearerToken,
        SaveUserConfigCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var current = await _queryPort.GetAsync(ct).ConfigureAwait(false);
        var next = current with
        {
            RuntimeMode = command.RuntimeMode is null
                ? current.RuntimeMode
                : UserConfigRuntime.NormalizeConfiguredMode(command.RuntimeMode),
            LocalRuntimeBaseUrl = command.LocalRuntimeBaseUrl is null
                ? current.LocalRuntimeBaseUrl
                : UserConfigRuntime.NormalizeConfiguredBaseUrl(
                    command.LocalRuntimeBaseUrl,
                    nameof(command.LocalRuntimeBaseUrl)),
            RemoteRuntimeBaseUrl = command.RemoteRuntimeBaseUrl is null
                ? current.RemoteRuntimeBaseUrl
                : UserConfigRuntime.NormalizeConfiguredBaseUrl(
                    command.RemoteRuntimeBaseUrl,
                    nameof(command.RemoteRuntimeBaseUrl)),
            GithubUsername = command.GithubUsername is null
                ? current.GithubUsername
                : NormalizeOptional(command.GithubUsername),
            MaxToolRounds = command.MaxToolRounds ?? current.MaxToolRounds,
        };

        if (command.DefaultModel is not null || command.PreferredLlmRoute is not null)
        {
            next = await _llmPreferenceWriter.MergeLegacyFieldsAsync(
                    bearerToken,
                    next,
                    command.DefaultModel,
                    command.PreferredLlmRoute,
                    ct)
                .ConfigureAwait(false);
        }

        return await _commandService.SaveAsync(next, ct).ConfigureAwait(false);
    }

    public Task<UserConfigSaveReceipt> SaveLlmPreferenceAsync(
        string? bearerToken,
        SaveUserLlmPreferenceCommand command,
        CancellationToken ct = default) =>
        _llmPreferenceWriter.SaveAsync(bearerToken, command, ct);

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
}

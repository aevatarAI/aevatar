using Aevatar.Studio.Application.Studio.Abstractions;

namespace Aevatar.Studio.Application.Studio.Services;

public sealed class UserConfigService : IUserConfigService
{
    private readonly IUserConfigQueryPort _queryPort;
    private readonly IUserConfigCommandService _commandService;
    private readonly UserLlmPreferenceWriter _llmPreferenceWriter;
    private readonly IAppScopeResolver _scopeResolver;

    public UserConfigService(
        IUserConfigQueryPort queryPort,
        IUserConfigCommandService commandService,
        UserLlmPreferenceWriter llmPreferenceWriter,
        IAppScopeResolver scopeResolver)
    {
        _queryPort = queryPort ?? throw new ArgumentNullException(nameof(queryPort));
        _commandService = commandService ?? throw new ArgumentNullException(nameof(commandService));
        _llmPreferenceWriter = llmPreferenceWriter ?? throw new ArgumentNullException(nameof(llmPreferenceWriter));
        _scopeResolver = scopeResolver ?? throw new ArgumentNullException(nameof(scopeResolver));
    }

    public Task<UserConfig> GetAsync(CancellationToken ct = default) =>
        _queryPort.GetAsync(ResolveOwnerResource(), ct);

    public async Task<UserConfigRuntimeView> GetRuntimeAsync(CancellationToken ct = default)
    {
        var config = await _queryPort.GetAsync(ResolveOwnerResource(), ct).ConfigureAwait(false);
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

        _ = bearerToken;
        var update = new UserConfigUpdate(
            RuntimeMode: command.RuntimeMode is null
                ? null
                : UserConfigRuntime.NormalizeConfiguredMode(command.RuntimeMode),
            LocalRuntimeBaseUrl: command.LocalRuntimeBaseUrl is null
                ? null
                : UserConfigRuntime.NormalizeConfiguredBaseUrl(
                    command.LocalRuntimeBaseUrl,
                    nameof(command.LocalRuntimeBaseUrl)),
            RemoteRuntimeBaseUrl: command.RemoteRuntimeBaseUrl is null
                ? null
                : UserConfigRuntime.NormalizeConfiguredBaseUrl(
                    command.RemoteRuntimeBaseUrl,
                    nameof(command.RemoteRuntimeBaseUrl)),
            GithubUsername: command.GithubUsername is null
                ? null
                : NormalizeOptional(command.GithubUsername) ?? string.Empty,
            MaxToolRounds: command.MaxToolRounds);

        return await _commandService.UpdateAsync(ResolveOwnerResource(), update, ct).ConfigureAwait(false);
    }

    public Task<UserConfigSaveReceipt> SaveLlmPreferenceAsync(
        string? bearerToken,
        UserLlmPreferenceIntent intent,
        CancellationToken ct = default) =>
        _llmPreferenceWriter.SaveAsync(ResolveOwnerResource(), bearerToken, intent, ct);

    private UserConfigResourceKey ResolveOwnerResource() =>
        UserConfigResourceKey.ForOwnerScope(_scopeResolver.ResolveScopeIdOrDefault());

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
}

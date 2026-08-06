using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Abstractions.Slash;
using Aevatar.GAgents.Channel.Identity;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.GAgents.NyxidChat.LlmSelection;
using Aevatar.Studio.Application.Studio.Abstractions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.NyxidChat.Slash;

/// <summary>
/// /model and /route — deterministic, no-LLM path for listing and selecting the
/// inbound sender's NyxID-backed LLM route/model preference.
/// </summary>
public sealed class ModelChannelSlashCommandHandler : IChannelSlashCommandHandler
{
    private static readonly char[] WhitespaceSeparators = [' ', '\t', '\r', '\n'];

    private readonly IUserLlmOptionsService? _optionsService;
    private readonly IUserLlmSelectionService? _selectionService;
    private readonly IUserLlmOptionsRenderer<MessageContent>? _renderer;
    private readonly IActorDispatchPort _actorDispatchPort;
    private readonly ILogger<ModelChannelSlashCommandHandler> _logger;

    public ModelChannelSlashCommandHandler(
        ILogger<ModelChannelSlashCommandHandler> logger,
        IActorDispatchPort actorDispatchPort,
        IUserLlmOptionsService? optionsService = null,
        IUserLlmSelectionService? selectionService = null,
        IUserLlmOptionsRenderer<MessageContent>? renderer = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _actorDispatchPort = actorDispatchPort ?? throw new ArgumentNullException(nameof(actorDispatchPort));
        _optionsService = optionsService;
        _selectionService = selectionService;
        _renderer = renderer;
    }

    public string Name => "model";

    public IReadOnlyList<string> Aliases => ["models", "llm", "route"];

    public bool RequiresBinding => true;

    public ChannelSlashCommandUsage Usage => new(
        Name,
        "[list [page]] | use <service-number|service-name> [model-name] | preset <preset-id> | reset",
        "查看和切换当前 NyxID 绑定用户的 LLM route/model 偏好");

    public async Task<MessageContent?> HandleAsync(ChannelSlashCommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var bindingId = context.BindingIdValue;
        if (string.IsNullOrWhiteSpace(bindingId))
            return new MessageContent { Text = "请先发送 /init 完成 NyxID 绑定。" };

        if (_optionsService is null || _selectionService is null || _renderer is null)
        {
            _logger.LogDebug("/model invoked but user LLM selection services are not registered");
            return new MessageContent { Text = "当前部署未启用模型偏好,/model 暂不可用。" };
        }

        var (sub, arg) = ParseSubcommand(context.ArgumentText);
        var mode = ResolveDisplayMode(context.CommandName);
        try
        {
            return sub switch
            {
                "" => _renderer.RenderCurrent(
                    await _optionsService.GetOptionsAsync(BuildQuery(context, bindingId), ct).ConfigureAwait(false),
                    mode),
                "list" => _renderer.RenderOptions(
                    await _optionsService.GetOptionsAsync(BuildQuery(context, bindingId), ct).ConfigureAwait(false),
                    mode,
                    ParsePage(arg)),
                "use" => await HandleUseAsync(context, bindingId, arg, ct).ConfigureAwait(false),
                "preset" => await HandlePresetAsync(context, bindingId, arg, ct).ConfigureAwait(false),
                "reset" => await HandleResetAsync(context, bindingId, ct).ConfigureAwait(false),
                _ => UsageHint(),
            };
        }
        catch (AevatarOAuthClientNotProvisionedException)
        {
            return new MessageContent { Text = "NyxID 客户端正在初始化,请稍后重试 /models。" };
        }
        catch (BindingNotFoundException)
        {
            return await SelfHealRevokedBindingAsync(
                context,
                reason: "auto_self_heal_remote_not_found",
                submittedMessage: "NyxID 端 binding 已不可用,本地清理已提交。请稍后发送 /init 完成新绑定。",
                degradedMessage: "NyxID 端 binding 已不可用,本地清理提交失败。请稍后重试 /models,或发送 /unbind 后再发送 /init 重新绑定。",
                ct).ConfigureAwait(false);
        }
        catch (BindingRevokedException)
        {
            return await SelfHealRevokedBindingAsync(
                context,
                reason: "auto_self_heal_remote_revoked",
                submittedMessage: "NyxID 端 binding 已失效,本地清理已提交。请稍后发送 /init 完成新绑定。",
                degradedMessage: "NyxID 端 binding 已失效,本地清理提交失败。请稍后重试 /models,或发送 /unbind 后再发送 /init 重新绑定。",
                ct).ConfigureAwait(false);
        }
        catch (BindingScopeMismatchException)
        {
            return new MessageContent
            {
                Text = "当前 NyxID 绑定缺少 LLM route 权限。请发送 /init 更新现有绑定的服务授权。",
            };
        }
        catch (BindingServiceAccessMismatchException)
        {
            return new MessageContent
            {
                Text = "当前 NyxID 绑定缺少 Aevatar、默认 LLM、Ornn service 或 Sandbox service 授权。请发送 /init 更新现有绑定的服务授权。",
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or HttpRequestException or NotSupportedException)
        {
            _logger.LogWarning(ex, "/model failed to read or update NyxID LLM selection");
            return new MessageContent { Text = BuildUserFacingFailureMessage(ex) };
        }
    }

    /// <summary>
    /// Submits a local binding revoke when NyxID reports the binding is gone
    /// (revoked / not_found / scope-mismatch). This is intentionally dispatch
    /// only: the slash request path must not activate projection scopes or
    /// wait for read-model materialization. The user is told cleanup has been
    /// submitted and can retry <c>/init</c> after the projection catches up.
    /// </summary>
    /// <remarks>
    /// Differs from explicit <c>/unbind</c> because the NyxID-side revoke is
    /// already known; we only need to flip the local actor, with one retry for
    /// transient dispatch failure.
    /// </remarks>
    private async Task<MessageContent> SelfHealRevokedBindingAsync(
        ChannelSlashCommandContext context,
        string reason,
        string submittedMessage,
        string degradedMessage,
        CancellationToken ct)
    {
        var submitted = await TryDispatchLocalBindingRevokeAsync(context, reason, ct).ConfigureAwait(false);
        return new MessageContent { Text = submitted ? submittedMessage : degradedMessage };
    }

    private async Task<bool> TryDispatchLocalBindingRevokeAsync(
        ChannelSlashCommandContext context,
        string reason,
        CancellationToken ct)
    {
        var actorId = context.Subject.ToActorId();

        // Single retry mirrors /unbind: a one-off dispatch hiccup should not
        // leave the user permanently stuck with a stale local binding.
        Exception? lastError = null;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                var envelope = new EventEnvelope
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                    Payload = Any.Pack(new RevokeBindingCommand
                    {
                        ExternalSubject = context.Subject.Clone(),
                        Reason = reason,
                    }),
                    Route = EnvelopeRouteSemantics.CreateDirect(
                        NyxIdChatServiceDefaults.ModelSelfHealPublisherActorId,
                        actorId),
                };
                await _actorDispatchPort
                    .DispatchAsync(actorId, envelope, ct)
                    .ConfigureAwait(false);
                _logger.LogWarning(
                    "/model submitted local binding self-heal actor={ActorId} after NyxID-side rejection: reason={Reason}, attempt={Attempt}/2, subject={Platform}:{Tenant}:{User}",
                    actorId,
                    reason,
                    attempt,
                    context.Subject.Platform, context.Subject.Tenant, context.Subject.ExternalUserId);
                return true;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                lastError = ex;
                _logger.LogWarning(ex,
                    "/model: local binding self-heal dispatch failed on attempt {Attempt}/2 for actor={ActorId}, reason={Reason}",
                    attempt,
                    actorId,
                    reason);
            }
        }

        _logger.LogError(lastError,
            "/model failed to self-heal local binding actor={ActorId} after 2 attempts; reason={Reason}. User has been told to /unbind manually.",
            actorId,
            reason);
        return false;
    }

    private async Task<MessageContent> HandleUseAsync(
        ChannelSlashCommandContext context,
        string bindingId,
        string argument,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(argument))
            return new MessageContent { Text = "用法:`/model use <service-number|service-name> [model-name]`" };

        var query = BuildQuery(context, bindingId);
        var selectionContext = BuildSelectionContext(context, bindingId);
        var view = await _optionsService!.GetOptionsAsync(query, ct).ConfigureAwait(false);
        var requested = argument.Trim();

        if (TryResolveServiceSelection(requested, view.Available, out var combinedOption, out var modelOverride, out var combinedError))
        {
            if (combinedError is not null)
                return new MessageContent { Text = combinedError };

            return await ApplyServiceAsync(selectionContext, combinedOption!, modelOverride, ct)
                .ConfigureAwait(false);
        }

        return UsageHint();
    }

    private async Task<MessageContent> ApplyServiceAsync(
        UserLlmSelectionContext context,
        UserLlmOption option,
        string? model,
        CancellationToken ct)
    {
        var modelSelection = new LLMModelSelection
        {
            Kind = string.IsNullOrWhiteSpace(model)
                ? LLMModelSelectionKind.ProviderDefault
                : LLMModelSelectionKind.ExplicitModel,
            ModelId = model?.Trim() ?? string.Empty,
        };
        try
        {
            var userServiceId = InventoryUserServiceId(option) ??
                                throw new InvalidOperationException(
                                    "The selected LLM option does not have an inventory identity.");
            await _selectionService!.SetByServiceAsync(context, userServiceId, modelSelection, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return new MessageContent { Text = ex.Message };
        }

        return _renderer!.RenderSelectionConfirm(option, modelSelection);
    }

    private async Task<MessageContent> HandlePresetAsync(
        ChannelSlashCommandContext context,
        string bindingId,
        string presetId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(presetId))
            return new MessageContent { Text = "用法:`/model preset <preset-id>`" };

        try
        {
            await _selectionService!
                .ApplyPresetAsync(BuildSelectionContext(context, bindingId), presetId.Trim(), ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException)
        {
            return new MessageContent { Text = ex.Message };
        }

        return new MessageContent
        {
            Text = $"LLM preset **{presetId.Trim()}** 更新已提交；观察到更新后的设置后生效。",
        };
    }

    private async Task<MessageContent> HandleResetAsync(
        ChannelSlashCommandContext context,
        string bindingId,
        CancellationToken ct)
    {
        try
        {
            await _selectionService!
                .ResetAsync(BuildSelectionContext(context, bindingId), ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return new MessageContent { Text = ex.Message };
        }

        return new MessageContent { Text = "LLM 选择重置已提交；观察到更新后的设置后使用系统默认。" };
    }

    private static bool TryResolveServiceSelection(
        string requested,
        IReadOnlyList<UserLlmOption> available,
        out UserLlmOption? option,
        out string? model,
        out string? error)
    {
        option = null;
        model = null;
        error = null;

        if (TryResolveNumberedOption(requested, available, out var directNumberedOption, out var directNumberError))
        {
            if (directNumberError is not null)
            {
                error = directNumberError;
                return true;
            }

            option = directNumberedOption;
            return true;
        }

        var exact = FindExactOption(requested, available);
        if (exact is not null)
        {
            option = exact;
            return true;
        }

        if (TryResolveExactOptionPrefix(requested, available, out var prefixOption, out var prefixModel))
        {
            option = prefixOption;
            model = prefixModel;
            return true;
        }

        var split = SplitFirstToken(requested);
        if (split is null)
            return false;
        var (serviceToken, modelToken) = split.Value;

        if (TryResolveNumberedOption(serviceToken, available, out var splitNumberedOption, out var splitNumberError))
        {
            if (splitNumberError is not null)
            {
                error = splitNumberError;
                return true;
            }

            option = splitNumberedOption;
            model = modelToken;
            return true;
        }

        var namedOption = FindExactOption(serviceToken, available);
        if (namedOption is null)
            return false;

        option = namedOption;
        model = modelToken;
        return true;
    }

    private static (string ServiceToken, string ModelToken)? SplitFirstToken(string requested)
    {
        var parts = requested.Split(
            WhitespaceSeparators,
            2,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
            return null;

        return string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1])
            ? null
            : (parts[0], parts[1]);
    }

    private static bool TryResolveNumberedOption(
        string requested,
        IReadOnlyList<UserLlmOption> available,
        out UserLlmOption? option,
        out string? error)
    {
        option = null;
        error = null;
        if (!int.TryParse(requested, out var number))
            return false;

        if (number < 1 || number > available.Count)
        {
            error = $"没有编号 {number} 的 LLM service。发送 /models 查看当前可用列表。";
            return true;
        }

        option = available[number - 1];
        return true;
    }

    private static UserLlmOption? FindExactOption(string requested, IReadOnlyList<UserLlmOption> available)
    {
        var matches = available
            .Where(option =>
                string.Equals(InventoryUserServiceId(option), requested, StringComparison.Ordinal) ||
                string.Equals(option.ServiceSlug, requested, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(option.DisplayName, requested, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        return UserLlmPreferenceWriteCore.ChoosePreferredOption(matches.Where(IsSelectable)) ??
               UserLlmPreferenceWriteCore.ChoosePreferredOption(matches);
    }

    private static bool IsSelectable(UserLlmOption option) =>
        option.Allowed && string.Equals(option.Status, "ready", StringComparison.OrdinalIgnoreCase);

    private static bool TryResolveExactOptionPrefix(
        string requested,
        IReadOnlyList<UserLlmOption> available,
        out UserLlmOption? option,
        out string? model)
    {
        option = null;
        model = null;

        var matches = available
            .SelectMany(service => ServiceTokens(service).Select(token => (Service: service, Token: token)))
            .Where(candidate =>
                candidate.Token.Length < requested.Length &&
                requested.StartsWith(candidate.Token, StringComparison.OrdinalIgnoreCase) &&
                char.IsWhiteSpace(requested[candidate.Token.Length]))
            .GroupBy(candidate => candidate.Service)
            .Select(group => group.OrderByDescending(candidate => candidate.Token.Length).First())
            .ToArray();

        var preferred = UserLlmPreferenceWriteCore.ChoosePreferredOption(matches
                            .Select(candidate => candidate.Service)
                            .Where(IsSelectable)) ??
                        (matches.Length == 1
                            ? matches[0].Service
                            : null);
        if (preferred is null)
            return false;

        var token = matches
            .Where(candidate => ReferenceEquals(candidate.Service, preferred) || candidate.Service == preferred)
            .Select(candidate => candidate.Token)
            .OrderByDescending(candidate => candidate.Length)
            .FirstOrDefault();
        if (token is null)
            return false;

        var modelToken = requested[token.Length..].Trim();
        if (string.IsNullOrWhiteSpace(modelToken))
        {
            return false;
        }

        option = preferred;
        model = modelToken;
        return true;
    }

    private static IEnumerable<string> ServiceTokens(UserLlmOption option)
    {
        if (InventoryUserServiceId(option) is { } userServiceId)
            yield return userServiceId;
        if (!string.IsNullOrWhiteSpace(option.ServiceSlug))
            yield return option.ServiceSlug.Trim();
        if (!string.IsNullOrWhiteSpace(option.DisplayName))
            yield return option.DisplayName.Trim();
    }

    private static string? InventoryUserServiceId(UserLlmOption option) =>
        option.Identity is
        {
            Authority: UserLlmIdentityAuthority.NyxIdUserServicesInventory,
        } identity
            ? UserLlmPreferenceWriteCore.NormalizeOptional(identity.NyxIdUserServiceId)
            : null;

    private static string BuildUserFacingFailureMessage(Exception ex) => ex switch
    {
        HttpRequestException => "读取或更新 NyxID LLM service 设置失败,请稍后重试。",
        NotSupportedException => "当前部署暂不支持这个 NyxID LLM 操作。",
        _ => "读取或更新 NyxID LLM service 设置失败,请稍后重试。",
    };

    private static UserLlmOptionsQuery BuildQuery(ChannelSlashCommandContext context, string bindingId) => new(
        new BindingId { Value = bindingId.Trim() },
        context.Subject.Clone(),
        context.RegistrationScopeId ?? string.Empty);

    private static UserLlmSelectionContext BuildSelectionContext(ChannelSlashCommandContext context, string bindingId) => new(
        new BindingId { Value = bindingId.Trim() },
        context.Subject.Clone(),
        context.RegistrationScopeId ?? string.Empty);

    private static (string Sub, string Arg) ParseSubcommand(string argumentText)
    {
        if (string.IsNullOrWhiteSpace(argumentText))
            return (string.Empty, string.Empty);

        var trimmed = argumentText.Trim();
        var firstSpace = trimmed.IndexOf(' ');
        if (firstSpace < 0)
            return (trimmed.ToLowerInvariant(), string.Empty);

        var sub = trimmed[..firstSpace].ToLowerInvariant();
        var arg = trimmed[(firstSpace + 1)..].Trim();
        return (sub, arg);
    }

    private static int ParsePage(string argument)
    {
        if (string.IsNullOrWhiteSpace(argument))
            return 1;

        var firstToken = argument.Split(
            WhitespaceSeparators,
            2,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0];
        return int.TryParse(firstToken, out var page) && page > 0 ? page : 1;
    }

    private static UserLlmSelectionDisplayMode ResolveDisplayMode(string? commandName) =>
        string.Equals(commandName?.Trim(), "route", StringComparison.OrdinalIgnoreCase)
            ? UserLlmSelectionDisplayMode.Route
            : UserLlmSelectionDisplayMode.Model;

    private static MessageContent UsageHint() => new()
    {
        Text = string.Join('\n',
            "未识别的子命令。可用:",
            "- `/route`:查看当前 route",
            "- `/model`:查看当前 model",
            "- `/route list` 或 `/model list`:查看所有可配置选项",
            "- `/model use <编号|service-name> [model-name]`:提交完整 service/model 选择",
            "- `/model preset <preset-id>`:使用 setup preset",
            "- `/model reset`:清空你的偏好,回退到 bot 默认"),
    };
}

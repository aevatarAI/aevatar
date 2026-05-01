using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Abstractions.Slash;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.GAgents.NyxidChat.LlmSelection;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.NyxidChat.Slash;

/// <summary>
/// /model — deterministic, no-LLM path for listing and selecting the
/// inbound sender's NyxID-backed LLM route/model preference.
/// </summary>
public sealed class ModelChannelSlashCommandHandler : IChannelSlashCommandHandler
{
    private readonly IUserLlmOptionsService? _optionsService;
    private readonly IUserLlmSelectionService? _selectionService;
    private readonly IUserLlmOptionsRenderer<MessageContent>? _renderer;
    private readonly ILogger<ModelChannelSlashCommandHandler> _logger;

    public ModelChannelSlashCommandHandler(
        ILogger<ModelChannelSlashCommandHandler> logger,
        IUserLlmOptionsService? optionsService = null,
        IUserLlmSelectionService? selectionService = null,
        IUserLlmOptionsRenderer<MessageContent>? renderer = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _optionsService = optionsService;
        _selectionService = selectionService;
        _renderer = renderer;
    }

    public string Name => "model";

    public IReadOnlyList<string> Aliases => ["models", "llm"];

    public bool RequiresBinding => true;

    public ChannelSlashCommandUsage Usage => new(
        Name,
        "list | use <service-number|service-name|model-name> | preset <preset-id> | reset",
        "查看和切换当前 NyxID 绑定用户的 LLM service/model 偏好");

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
        return sub switch
        {
            "" or "list" => _renderer.RenderOptions(
                await _optionsService.GetOptionsAsync(BuildQuery(context, bindingId), ct).ConfigureAwait(false)),
            "use" => await HandleUseAsync(context, bindingId, arg, ct).ConfigureAwait(false),
            "preset" => await HandlePresetAsync(context, bindingId, arg, ct).ConfigureAwait(false),
            "reset" => await HandleResetAsync(context, bindingId, ct).ConfigureAwait(false),
            _ => UsageHint(),
        };
    }

    private async Task<MessageContent> HandleUseAsync(
        ChannelSlashCommandContext context,
        string bindingId,
        string argument,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(argument))
            return new MessageContent { Text = "用法:`/model use <service-number|service-name|model-name>`" };

        var query = BuildQuery(context, bindingId);
        var selectionContext = BuildSelectionContext(context, bindingId);
        var view = await _optionsService!.GetOptionsAsync(query, ct).ConfigureAwait(false);
        var requested = argument.Trim();

        if (TryResolveNumberedOption(requested, view.Available, out var numberedOption, out var numberError))
        {
            if (numberError is not null)
                return new MessageContent { Text = numberError };

            return await ApplyServiceAsync(selectionContext, numberedOption!, modelOverride: null, ct)
                .ConfigureAwait(false);
        }

        var matched = FindOption(requested, view.Available);
        if (matched is not null)
            return await ApplyServiceAsync(selectionContext, matched, modelOverride: null, ct).ConfigureAwait(false);

        try
        {
            await _selectionService!.SetModelOverrideAsync(selectionContext, requested, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return new MessageContent { Text = ex.Message };
        }

        return new MessageContent
        {
            Text = $"已设置模型覆盖 **{requested}**。当前 LLM route 保持不变,下一条消息会尝试使用这个 model。",
        };
    }

    private async Task<MessageContent> ApplyServiceAsync(
        UserLlmSelectionContext context,
        UserLlmOption option,
        string? modelOverride,
        CancellationToken ct)
    {
        try
        {
            await _selectionService!.SetByServiceAsync(context, option.ServiceId, modelOverride, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return new MessageContent { Text = ex.Message };
        }

        return _renderer!.RenderSelectionConfirm(option, modelOverride ?? option.DefaultModel);
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
            Text = $"已应用 preset **{presetId.Trim()}**。下一条消息会用新的 LLM 设置回复。",
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

        return new MessageContent { Text = "已清空你的 service/model 偏好,后续消息使用 bot 默认设置。" };
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

    private static UserLlmOption? FindOption(string requested, IReadOnlyList<UserLlmOption> available)
    {
        var exact = available.FirstOrDefault(option =>
            string.Equals(option.ServiceId, requested, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(option.ServiceSlug, requested, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(option.DisplayName, requested, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
            return exact;

        var fuzzy = available
            .Where(option =>
                option.ServiceSlug.Contains(requested, StringComparison.OrdinalIgnoreCase) ||
                option.DisplayName.Contains(requested, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToArray();
        return fuzzy.Length == 1 ? fuzzy[0] : null;
    }

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

    private static MessageContent UsageHint() => new()
    {
        Text = string.Join('\n',
            "未识别的子命令。可用:",
            "- `/model` 或 `/models`:查看当前 LLM service 设置",
            "- `/model use <编号|service-name|model-name>`:切换 service 或只覆盖 model",
            "- `/model preset <preset-id>`:使用 setup preset",
            "- `/model reset`:清空你的偏好,回退到 bot 默认"),
    };
}

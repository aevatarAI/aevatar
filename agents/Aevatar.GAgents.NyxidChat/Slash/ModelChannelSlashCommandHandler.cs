using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Abstractions.Slash;
using Aevatar.Studio.Application.Studio.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.NyxidChat.Slash;

/// <summary>
/// /model — show or set the inbound sender's per-binding LLM model
/// preference (issue #513 phase 5). Subcommands:
///   - <c>/model</c> or <c>/model list</c>: show current sender model + the
///     bot owner default + a short usage hint.
///   - <c>/model use &lt;name&gt;</c>: persist <c>DefaultModel</c> on the
///     sender's <c>user-config-&lt;binding-id&gt;</c> actor.
///   - <c>/model reset</c>: clear the sender override so the next turn
///     falls back to the bot owner's prefs (or provider default).
/// Validation is intentionally minimal — the LLM provider rejects unknown
/// model names at request time. Phase 5 keeps the surface area small; a
/// follow-up PR will add a curated list once we wire NyxID's
/// /api/v1/llm/status into the broker capability.
/// </summary>
public sealed class ModelChannelSlashCommandHandler : IChannelSlashCommandHandler
{
    private readonly ILogger<ModelChannelSlashCommandHandler> _logger;

    public ModelChannelSlashCommandHandler(ILogger<ModelChannelSlashCommandHandler> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public string Name => "model";

    public bool RequiresBinding => true;

    public async Task<MessageContent?> HandleAsync(ChannelSlashCommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var bindingId = context.BindingIdValue;
        if (string.IsNullOrEmpty(bindingId))
        {
            // Defensive — the runner enforces RequiresBinding before invoking.
            return new MessageContent { Text = "请先发送 /init 完成 NyxID 绑定。" };
        }

        var queryPort = context.Services.GetService<IUserConfigQueryPort>();
        if (queryPort is null)
        {
            _logger.LogDebug("/model invoked but IUserConfigQueryPort is not registered; falling back to read-only hint");
            return new MessageContent { Text = "当前部署未启用模型偏好,/model 暂不可用。" };
        }

        var (sub, arg) = ParseSubcommand(context.ArgumentText);
        return sub switch
        {
            "" or "list" => await BuildListReplyAsync(queryPort, bindingId, ct).ConfigureAwait(false),
            "use" => await HandleUseAsync(context, queryPort, bindingId, arg, ct).ConfigureAwait(false),
            "reset" => await HandleResetAsync(context, queryPort, bindingId, ct).ConfigureAwait(false),
            _ => UsageHint(),
        };
    }

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

    private static async Task<MessageContent> BuildListReplyAsync(
        IUserConfigQueryPort queryPort,
        string bindingId,
        CancellationToken ct)
    {
        var senderConfig = await queryPort.GetAsync(bindingId, ct).ConfigureAwait(false);
        var ownerConfig = await queryPort.GetAsync(ct).ConfigureAwait(false);

        var senderModel = string.IsNullOrWhiteSpace(senderConfig.DefaultModel) ? "(未设置)" : senderConfig.DefaultModel;
        var ownerModel = string.IsNullOrWhiteSpace(ownerConfig.DefaultModel) ? "(未设置)" : ownerConfig.DefaultModel;

        var lines = new[]
        {
            "**模型设置**",
            $"- 当前你的模型:{senderModel}",
            $"- Bot 默认模型:{ownerModel}",
            "",
            "用法:",
            "- `/model use <model-name>` 设置你的偏好",
            "- `/model reset` 清空,回退到 bot 默认",
        };
        return new MessageContent { Text = string.Join('\n', lines) };
    }

    private async Task<MessageContent> HandleUseAsync(
        ChannelSlashCommandContext context,
        IUserConfigQueryPort queryPort,
        string bindingId,
        string requestedModel,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(requestedModel))
            return new MessageContent { Text = "用法:`/model use <model-name>`" };

        var commandService = context.Services.GetService<IUserConfigCommandService>();
        if (commandService is null)
        {
            _logger.LogDebug("/model use invoked but IUserConfigCommandService is not registered");
            return new MessageContent { Text = "当前部署未启用模型偏好写入,/model use 暂不可用。" };
        }

        Aevatar.Studio.Application.Studio.Abstractions.UserConfig current;
        try
        {
            current = await queryPort.GetAsync(bindingId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "/model use: failed to read current sender config for binding {BindingId}", bindingId);
            return new MessageContent { Text = "读取你的模型偏好时遇到内部错误,稍后重试。" };
        }

        var merged = new Aevatar.Studio.Application.Studio.Abstractions.UserConfig(
            DefaultModel: requestedModel.Trim(),
            PreferredLlmRoute: current.PreferredLlmRoute,
            RuntimeMode: current.RuntimeMode,
            LocalRuntimeBaseUrl: current.LocalRuntimeBaseUrl,
            RemoteRuntimeBaseUrl: current.RemoteRuntimeBaseUrl,
            GithubUsername: current.GithubUsername,
            MaxToolRounds: current.MaxToolRounds);

        try
        {
            await commandService.SaveAsync(bindingId, merged, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "/model use: failed to save sender config for binding {BindingId}", bindingId);
            return new MessageContent { Text = "保存你的模型偏好时遇到内部错误,稍后重试。" };
        }

        return new MessageContent
        {
            Text = $"已切换到模型 **{requestedModel.Trim()}**。下一条消息会用新模型回复。",
        };
    }

    private async Task<MessageContent> HandleResetAsync(
        ChannelSlashCommandContext context,
        IUserConfigQueryPort queryPort,
        string bindingId,
        CancellationToken ct)
    {
        var commandService = context.Services.GetService<IUserConfigCommandService>();
        if (commandService is null)
            return new MessageContent { Text = "当前部署未启用模型偏好写入,/model reset 暂不可用。" };

        Aevatar.Studio.Application.Studio.Abstractions.UserConfig current;
        try
        {
            current = await queryPort.GetAsync(bindingId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "/model reset: failed to read sender config for binding {BindingId}", bindingId);
            return new MessageContent { Text = "读取你的模型偏好时遇到内部错误,稍后重试。" };
        }

        var cleared = new Aevatar.Studio.Application.Studio.Abstractions.UserConfig(
            DefaultModel: string.Empty,
            PreferredLlmRoute: current.PreferredLlmRoute,
            RuntimeMode: current.RuntimeMode,
            LocalRuntimeBaseUrl: current.LocalRuntimeBaseUrl,
            RemoteRuntimeBaseUrl: current.RemoteRuntimeBaseUrl,
            GithubUsername: current.GithubUsername,
            MaxToolRounds: current.MaxToolRounds);

        try
        {
            await commandService.SaveAsync(bindingId, cleared, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "/model reset: failed to save cleared sender config for binding {BindingId}", bindingId);
            return new MessageContent { Text = "重置你的模型偏好时遇到内部错误,稍后重试。" };
        }

        return new MessageContent { Text = "已清空你的模型偏好,后续消息使用 bot 默认模型。" };
    }

    private static MessageContent UsageHint() => new()
    {
        Text = string.Join('\n',
            "未识别的子命令。可用:",
            "- `/model` 或 `/model list`:查看当前模型设置",
            "- `/model use <model-name>`:切换到指定模型",
            "- `/model reset`:清空你的模型偏好,回退到 bot 默认"),
    };
}

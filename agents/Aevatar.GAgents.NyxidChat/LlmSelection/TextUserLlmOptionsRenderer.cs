using Aevatar.GAgents.Channel.Abstractions;

namespace Aevatar.GAgents.NyxidChat.LlmSelection;

public sealed class TextUserLlmOptionsRenderer : IUserLlmOptionsRenderer<MessageContent>
{
    public MessageContent RenderOptions(UserLlmOptionsView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        if (view.Available.Count == 0 && view.SetupHint is not null)
            return RenderSetupGuide(view.SetupHint);

        var lines = new List<string>
        {
            "**模型设置**",
            RenderCurrent(view.Current),
            "",
            "可用 services:",
        };

        for (var i = 0; i < view.Available.Count; i++)
        {
            var option = view.Available[i];
            var marker = ReferenceEquals(option, view.Current) ? " ✓" : string.Empty;
            var status = option.Allowed
                ? option.Status
                : $"{option.Status}, not allowed";
            var model = string.IsNullOrWhiteSpace(option.DefaultModel)
                ? string.Empty
                : $" / {option.DefaultModel}";
            lines.Add($"{i + 1}. {option.DisplayName}{model} [{option.Source}, {status}]{marker}");
        }

        lines.Add("");
        lines.Add("用法:");
        lines.Add("- `/model use <编号|service-name|model-name>` 切换 service 或只覆盖 model");
        lines.Add("- `/model preset <preset-id>` 使用 setup preset");
        lines.Add("- `/model reset` 清空你的 service/model 偏好,回退到 bot 默认");

        return new MessageContent { Text = string.Join('\n', lines) };
    }

    public MessageContent RenderSelectionConfirm(UserLlmOption picked, string? model)
    {
        ArgumentNullException.ThrowIfNull(picked);

        var resolvedModel = string.IsNullOrWhiteSpace(model)
            ? picked.DefaultModel
            : model.Trim();
        var suffix = string.IsNullOrWhiteSpace(resolvedModel)
            ? string.Empty
            : $" / {resolvedModel}";
        return new MessageContent
        {
            Text = $"已切换到 **{picked.DisplayName}{suffix}**。下一条消息会用这个 service 回复。",
        };
    }

    public MessageContent RenderSetupGuide(UserLlmSetupHint hint)
    {
        ArgumentNullException.ThrowIfNull(hint);

        var lines = new List<string>
        {
            "**模型设置**",
            "你的 NyxID 账号还没接入任何 LLM service。",
            "",
        };

        if (hint.Presets.Count > 0)
        {
            lines.Add("一键开始:");
            foreach (var preset in hint.Presets)
                lines.Add($"- `{preset.Id}` {preset.Title}: {preset.Description}");
            lines.Add("");
            lines.Add("用法:`/model preset <preset-id>`");
            lines.Add("");
        }

        lines.Add($"去 NyxID 配置 service: {hint.SetupUrl}");
        return new MessageContent { Text = string.Join('\n', lines) };
    }

    public MessageContent RenderPresetProvisioning(UserLlmPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        return new MessageContent { Text = $"正在为你开通 {preset.Title}..." };
    }

    private static string RenderCurrent(UserLlmOption? current)
    {
        if (current is null)
            return "- 当前:未选择 service";

        var model = string.IsNullOrWhiteSpace(current.DefaultModel)
            ? string.Empty
            : $" / {current.DefaultModel}";
        return $"- 当前:{current.DisplayName}{model}";
    }
}

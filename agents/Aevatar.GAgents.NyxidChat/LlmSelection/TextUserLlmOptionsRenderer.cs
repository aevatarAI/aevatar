using Aevatar.AI.Abstractions;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.Studio.Application.Studio.Abstractions;

namespace Aevatar.GAgents.NyxidChat.LlmSelection;

public sealed class TextUserLlmOptionsRenderer : IUserLlmOptionsRenderer<MessageContent>
{
    public const string SelectServiceActionId = "ls";
    public const string ApplyPresetActionId = "lp";
    public const string ListPageActionId = "llp";
    public const string LegacySelectServiceActionId = "llm_select_service";
    public const string LegacySelectModelActionId = "llm_select_model";
    public const string LegacyApplyPresetActionId = "llm_apply_preset";
    public const string LlmActionArgument = "llm_action";
    public const string SelectServiceAction = "select_service";
    public const string LegacySelectModelAction = "select_model";
    public const string ApplyPresetAction = "apply_preset";
    public const string ListPageAction = "list_page";
    public const string ServiceIdArgument = "service_id";
    public const string PresetIdArgument = "preset_id";
    public const string ModelArgument = "model";
    public const string PageArgument = "page";
    public const int DefaultPageSize = 5;

    public MessageContent RenderCurrent(UserLlmOptionsView view, UserLlmSelectionDisplayMode mode)
    {
        ArgumentNullException.ThrowIfNull(view);

        if (view.Available.Count == 0 && view.SetupHint is not null)
            return RenderSetupGuide(view.SetupHint);

        var command = CommandFor(mode);
        var otherCommand = mode == UserLlmSelectionDisplayMode.Route ? "/model" : "/route";
        var lines = new List<string>
        {
            mode == UserLlmSelectionDisplayMode.Route ? "**当前 route**" : "**当前 model**",
            "",
        };

        if (mode == UserLlmSelectionDisplayMode.Route)
        {
            lines.Add($"- Route: {RenderCurrentRouteName(view)}");
            lines.Add($"- Route value: `{RenderCurrentRouteValue(view)}`");
            lines.Add($"- 当前 model: {RenderCurrentModel(view)}");
        }
        else
        {
            lines.Add($"- Model: {RenderCurrentModel(view)}");
            lines.Add($"- Route: {RenderCurrentRouteName(view)}");
            lines.Add($"- Route value: `{RenderCurrentRouteValue(view)}`");
        }

        lines.Add("");
        lines.Add($"查看可配置选项: `{command} list`");
        lines.Add($"切换 route: `/route use <编号|service-name> [model-name]`");
        lines.Add($"选择 service/model: `/model use <编号|service-name> [model-name]`");

        var reply = new MessageContent { Text = string.Join('\n', lines) };
        reply.Cards.Add(new CardBlock
        {
            Kind = CardBlockKind.Section,
            Title = mode == UserLlmSelectionDisplayMode.Route ? "当前 route" : "当前 model",
            Text = $"发送 `{command} list` 查看可配置选项；发送 `{otherCommand}` 查看另一项当前设置。",
            Fields =
            {
                new CardField { Title = "Route", Text = RenderCurrentRouteName(view), IsShort = true },
                new CardField { Title = "Model", Text = RenderCurrentModel(view), IsShort = true },
                new CardField { Title = "Route value", Text = RenderCurrentRouteValue(view) },
            },
        });

        return reply;
    }

    public MessageContent RenderOptions(UserLlmOptionsView view, UserLlmSelectionDisplayMode mode, int page = 1)
    {
        ArgumentNullException.ThrowIfNull(view);

        if (view.Available.Count == 0 && view.SetupHint is not null)
            return RenderSetupGuide(view.SetupHint);

        var pagination = ResolvePagination(view.Available.Count, page);
        var pageItems = view.Available
            .Skip((pagination.Page - 1) * pagination.PageSize)
            .Take(pagination.PageSize)
            .ToArray();
        var command = CommandFor(mode);
        var title = mode == UserLlmSelectionDisplayMode.Route ? "可选 route" : "可选 route / model";

        var lines = new List<string>
        {
            $"**{title}**",
            RenderCurrentLine(view),
            "",
        };

        if (pageItems.Length == 0)
        {
            lines.Add("当前没有可用 LLM route。");
        }
        else
        {
            lines.Add($"第 {pagination.Page}/{pagination.TotalPages} 页,共 {view.Available.Count} 个选项:");
            foreach (var (option, absoluteIndex) in pageItems.Select((option, index) => (option, (pagination.Page - 1) * pagination.PageSize + index + 1)))
            {
                lines.Add($"{absoluteIndex}. {option.DisplayName}{RenderCurrentMarker(option, view.Current)}");
                lines.Add($"   route: `{option.RouteValue}`");
                lines.Add($"   default model: {RenderDefaultModel(option)}");
                lines.Add($"   source/status: {option.Source} / {RenderStatus(option)}");
            }
        }

        lines.Add("");
        if (pagination.TotalPages > 1)
            lines.Add($"翻页: `{command} list {PreviousPage(pagination)}` / `{command} list {NextPage(pagination)}`");
        lines.Add("选择 route: `/route use <编号|service-name> [model-name]`");
        lines.Add("选择 service/model: `/model use <编号|service-name> [model-name]`");

        var reply = new MessageContent { Text = string.Join('\n', lines) };
        reply.Cards.Add(new CardBlock
        {
            Kind = CardBlockKind.Section,
            Title = title,
            Text = $"{RenderCurrentLine(view)}\n第 {pagination.Page}/{pagination.TotalPages} 页,共 {view.Available.Count} 个选项。",
        });

        if (pageItems.Length > 0)
        {
            reply.Actions.Add(BuildServiceSelectAction(pageItems, view.Current));
            reply.Actions.Add(BuildSubmitSelectedServiceAction());
        }

        if (pagination.TotalPages > 1)
        {
            reply.Actions.Add(BuildPageAction("上一页", PreviousPage(pagination), mode, isDisabled: pagination.Page <= 1));
            reply.Actions.Add(BuildPageAction("下一页", NextPage(pagination), mode, isDisabled: pagination.Page >= pagination.TotalPages));
        }

        return reply;
    }

    public MessageContent RenderSelectionConfirm(UserLlmOption picked, LLMModelSelection modelSelection)
    {
        ArgumentNullException.ThrowIfNull(picked);
        ArgumentNullException.ThrowIfNull(modelSelection);

        var modelLine = modelSelection.Kind == LLMModelSelectionKind.ExplicitModel
            ? modelSelection.ModelId
            : "Provider default";
        var reply = new MessageContent
        {
            Text = $"**{picked.DisplayName}** 的 LLM 选择更新已提交。\n- Route: `{picked.RouteValue}`\n- Model: {modelLine}\n观察到更新后的设置后生效。",
        };
        reply.Cards.Add(new CardBlock
        {
            Kind = CardBlockKind.Section,
            Title = "模型设置更新已提交",
            Text = $"等待观察 {picked.DisplayName} 的完整选择",
            Fields =
            {
                new CardField { Title = "Route", Text = picked.RouteValue },
                new CardField { Title = "Model", Text = modelLine, IsShort = true },
                new CardField { Title = "Status", Text = picked.Status, IsShort = true },
                new CardField { Title = "Source", Text = picked.Source, IsShort = true },
            },
        });
        return reply;
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
        var reply = new MessageContent { Text = string.Join('\n', lines) };
        reply.Cards.Add(new CardBlock
        {
            Kind = CardBlockKind.Section,
            Title = "模型设置",
            Text = "你的 NyxID 账号还没接入任何 LLM service。",
        });
        foreach (var preset in hint.Presets)
            reply.Actions.Add(BuildPresetAction(preset));
        if (!string.IsNullOrWhiteSpace(hint.SetupUrl))
        {
            reply.Actions.Add(new ActionElement
            {
                Kind = ActionElementKind.Link,
                ActionId = "llm_setup_open_nyxid",
                Label = "去 NyxID 配置",
                Value = hint.SetupUrl,
            });
        }

        return reply;
    }

    public MessageContent RenderPresetProvisioning(UserLlmPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        return new MessageContent { Text = $"正在为你开通 {preset.Title}..." };
    }

    private static string CommandFor(UserLlmSelectionDisplayMode mode) =>
        mode == UserLlmSelectionDisplayMode.Route ? "/route" : "/model";

    private static string RenderCurrentLine(UserLlmOptionsView view) =>
        $"当前: route {RenderCurrentRouteName(view)} / model {RenderCurrentModel(view)}";

    private static string RenderCurrentRouteName(UserLlmOptionsView view) =>
        view.Current?.DisplayName ??
        (string.IsNullOrWhiteSpace(view.CurrentRouteValue) ? "bot 默认 route" : "自定义 route");

    private static string RenderCurrentRouteValue(UserLlmOptionsView view)
    {
        if (!string.IsNullOrWhiteSpace(view.CurrentRouteValue))
            return view.CurrentRouteValue.Trim();
        return UserConfigLlmRouteDefaults.Gateway;
    }

    private static string RenderCurrentModel(UserLlmOptionsView view)
    {
        if (!string.IsNullOrWhiteSpace(view.CurrentModel))
            return view.CurrentModel.Trim();

        if (!string.IsNullOrWhiteSpace(view.Current?.ModelCatalog.DefaultModelId))
            return $"{view.Current.ModelCatalog.DefaultModelId} (route 默认)";

        return "未覆盖,使用 route 默认";
    }

    private static string RenderDefaultModel(UserLlmOption option) =>
        string.IsNullOrWhiteSpace(option.ModelCatalog.DefaultModelId)
            ? "service default"
            : option.ModelCatalog.DefaultModelId.Trim();

    private static string RenderStatus(UserLlmOption option) =>
        option.Allowed ? option.Status : $"{option.Status}, not allowed";

    private static string RenderCurrentMarker(UserLlmOption option, UserLlmOption? current) =>
        IsCurrent(option, current) ? " (当前)" : string.Empty;

    private static bool IsCurrent(UserLlmOption option, UserLlmOption? current) =>
        current is not null &&
        string.Equals(
            InventoryUserServiceId(option),
            InventoryUserServiceId(current),
            StringComparison.Ordinal) &&
        string.Equals(option.RouteValue, current.RouteValue, StringComparison.OrdinalIgnoreCase);

    private static PageWindow ResolvePagination(int totalCount, int requestedPage)
    {
        var totalPages = Math.Max(1, (int)Math.Ceiling(totalCount / (double)DefaultPageSize));
        var page = Math.Clamp(requestedPage <= 0 ? 1 : requestedPage, 1, totalPages);
        return new PageWindow(page, totalPages, DefaultPageSize);
    }

    private static int PreviousPage(PageWindow pagination) =>
        Math.Max(1, pagination.Page - 1);

    private static int NextPage(PageWindow pagination) =>
        Math.Min(pagination.TotalPages, pagination.Page + 1);

    private static ActionElement BuildServiceSelectAction(
        IReadOnlyList<UserLlmOption> options,
        UserLlmOption? current)
    {
        var action = new ActionElement
        {
            Kind = ActionElementKind.Select,
            ActionId = ServiceIdArgument,
            Label = "选择本页 route",
            Placeholder = "选择一个 route",
            Value = options
                .Where(option => IsCurrent(option, current))
                .Select(InventoryUserServiceId)
                .FirstOrDefault() ?? string.Empty,
        };

        foreach (var option in options)
        {
            var userServiceId = InventoryUserServiceId(option);
            if (userServiceId is null)
                continue;

            action.Options.Add(new ActionOption
            {
                Label = $"{option.DisplayName}{RenderCurrentMarker(option, current)}",
                Value = userServiceId,
            });
        }

        return action;
    }

    private static string? InventoryUserServiceId(UserLlmOption option) =>
        option.Identity is
        {
            Authority: UserLlmIdentityAuthority.NyxIdUserServicesInventory,
        } identity
            ? UserLlmPreferenceWriteCore.NormalizeOptional(identity.NyxIdUserServiceId)
            : null;

    private static ActionElement BuildSubmitSelectedServiceAction() => new()
    {
        Kind = ActionElementKind.FormSubmit,
        ActionId = SelectServiceActionId,
        Label = "应用所选 route",
        IsPrimary = true,
        LlmSelection = new LlmSelectionActionPayload
        {
            Action = SelectServiceAction,
        },
    };

    private static ActionElement BuildPageAction(
        string label,
        int page,
        UserLlmSelectionDisplayMode mode,
        bool isDisabled) => new()
    {
        Kind = ActionElementKind.Button,
        ActionId = ListPageActionId,
        Label = label,
        Value = page.ToString(System.Globalization.CultureInfo.InvariantCulture),
        IsDisabled = isDisabled,
        LlmSelection = new LlmSelectionActionPayload
        {
            Action = ListPageAction,
            Page = page,
            DisplayMode = mode == UserLlmSelectionDisplayMode.Route ? "route" : "model",
        },
    };

    private static ActionElement BuildPresetAction(UserLlmPreset preset) => new()
    {
        Kind = ActionElementKind.Button,
        ActionId = ApplyPresetActionId,
        Label = preset.Title,
        Value = preset.Id,
        IsPrimary = true,
        LlmSelection = new LlmSelectionActionPayload
        {
            Action = ApplyPresetAction,
            PresetId = preset.Id,
        },
    };

    private readonly record struct PageWindow(int Page, int TotalPages, int PageSize);
}

using System.Globalization;
using System.Text.Json;
using Aevatar.GAgents.Channel.Abstractions;

namespace Aevatar.GAgents.Platform.Lark;

public sealed class LarkMessageComposer : IMessageComposer<LarkOutboundMessage>
{
    public const int DefaultMaxMessageLength = 30_000;
    private const string TruncationMarker = "\n\n...[truncated]";

    public static readonly ChannelCapabilities DefaultCapabilities = new()
    {
        SupportsEphemeral = false,
        SupportsEdit = true,
        SupportsDelete = true,
        SupportsThread = true,
        Streaming = StreamingSupport.Native,
        SupportsFiles = false,
        MaxMessageLength = DefaultMaxMessageLength,
        SupportsActionButtons = true,
        SupportsConfirmDialog = false,
        SupportsModal = false,
        SupportsMention = true,
        SupportsTyping = false,
        SupportsReactions = false,
        RecommendedStreamDebounceMs = 300,
        Transport = TransportMode.Webhook,
    };

    public ChannelId Channel { get; } = ChannelId.From("lark");

    public LarkOutboundMessage Compose(MessageContent intent, ComposeContext context)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(context);

        var effectiveText = Truncate(intent.Text, context.Capabilities?.MaxMessageLength ?? DefaultCapabilities.MaxMessageLength);
        if (intent.Actions.Count == 0 && intent.Cards.Count == 0)
        {
            return new LarkOutboundMessage(
                MessageType: "text",
                ContentJson: JsonSerializer.Serialize(new { text = effectiveText }),
                PlainText: effectiveText,
                IsInteractive: false);
        }

        var headerTitle = ResolveHeaderTitle(intent, effectiveText);
        var template = ResolveHeaderTemplate(intent);
        var formMode = RequiresFormWrapping(intent);

        if (formMode)
        {
            var formElements = new List<object>();
            var leading = BuildLeadingMarkdown(effectiveText, intent);
            if (leading is not null)
                formElements.Add(leading);

            var actionElements = EnumerateActions(intent).SelectMany(BuildFormChildElements).ToArray();
            formElements.Add(new
            {
                tag = "form",
                name = DefaultFormName,
                direction = "vertical",
                elements = actionElements,
            });

            var formCardJson = JsonSerializer.Serialize(new
            {
                schema = "2.0",
                config = new
                {
                    wide_screen_mode = true,
                },
                header = new
                {
                    title = new
                    {
                        tag = "plain_text",
                        content = headerTitle,
                    },
                    template,
                },
                body = new
                {
                    direction = "vertical",
                    elements = formElements,
                },
            });

            return new LarkOutboundMessage(
                MessageType: "interactive",
                ContentJson: formCardJson,
                PlainText: effectiveText,
                IsInteractive: true);
        }

        var elements = new List<object>();
        if (!string.IsNullOrWhiteSpace(effectiveText))
        {
            elements.Add(new
            {
                tag = "markdown",
                content = effectiveText,
            });
        }

        for (var i = 0; i < intent.Cards.Count; i++)
        {
            var card = intent.Cards[i];
            // First card's Title is consumed by ResolveHeaderTitle as the card header (Title
            // takes precedence over intent.Text there), so render its body markdown without the
            // title to avoid header/body duplication. Form mode already does this; non-form mode
            // used to leak the title twice and made every single-card response (e.g. /agents,
            // /agent-status) show a redundant bold title row right under the header. When the
            // first card has no Title, ResolveHeaderTitle falls back to intent.Text and this
            // skip is a no-op (no title to elide).
            var skipTitle = i == 0;
            var markdown = BuildCardMarkdown(card, skipTitle);
            if (string.IsNullOrWhiteSpace(markdown))
                continue;

            elements.Add(new
            {
                tag = "markdown",
                content = markdown,
            });
        }

        if (EnumerateActions(intent).Any())
        {
            elements.AddRange(EnumerateActions(intent)
                .Where(action => action.Kind is not ActionElementKind.TextInput and not ActionElementKind.Select)
                .Select(BuildAction));
        }

        var cardJson = JsonSerializer.Serialize(new
        {
            schema = "2.0",
            config = new
            {
                wide_screen_mode = true,
            },
            header = new
            {
                title = new
                {
                    tag = "plain_text",
                    content = headerTitle,
                },
                template,
            },
            body = new
            {
                direction = "vertical",
                elements,
            },
        });

        return new LarkOutboundMessage(
            MessageType: "interactive",
            ContentJson: cardJson,
            PlainText: effectiveText,
            IsInteractive: true);
    }

    object IMessageComposer.Compose(MessageContent intent, ComposeContext context) => Compose(intent, context);

    public ComposeCapability Evaluate(MessageContent intent, ComposeContext context)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(context);

        if (intent.Disposition == MessageDisposition.Ephemeral)
            return ComposeCapability.Degraded;

        if (intent.Attachments.Count > 0 && !(context.Capabilities?.SupportsFiles ?? DefaultCapabilities.SupportsFiles))
            return ComposeCapability.Unsupported;

        if (EnumerateActions(intent).Any() && !(context.Capabilities?.SupportsActionButtons ?? DefaultCapabilities.SupportsActionButtons))
            return ComposeCapability.Degraded;

        return ComposeCapability.Exact;
    }

    private const string DefaultFormName = "card_form";

    private static bool RequiresFormWrapping(MessageContent intent) =>
        EnumerateActions(intent).Any(a => a.Kind is ActionElementKind.TextInput or ActionElementKind.Select or ActionElementKind.FormSubmit);

    private static string ResolveHeaderTitle(MessageContent intent, string effectiveText)
    {
        if (intent.Cards.Count > 0)
        {
            var first = intent.Cards[0];
            if (!string.IsNullOrWhiteSpace(first.Title))
                return first.Title;
        }

        return string.IsNullOrWhiteSpace(effectiveText) ? "Aevatar" : effectiveText;
    }

    private static string ResolveHeaderTemplate(MessageContent intent) =>
        EnumerateActions(intent).Any(a => a.IsDanger) ? "orange" : "blue";

    private static IEnumerable<ActionElement> EnumerateActions(MessageContent intent)
    {
        foreach (var action in intent.Actions)
            yield return action;
        foreach (var card in intent.Cards)
        {
            foreach (var action in card.Actions)
                yield return action;
        }
    }

    private static object? BuildLeadingMarkdown(string effectiveText, MessageContent intent)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(effectiveText))
            parts.Add(effectiveText);

        for (var i = 0; i < intent.Cards.Count; i++)
        {
            var card = intent.Cards[i];
            // In form mode the first card's title is consumed as the card header title,
            // so skip the title when rendering its body markdown to avoid duplication.
            var skipTitle = i == 0;
            var markdown = BuildCardMarkdown(card, skipTitle);
            if (!string.IsNullOrWhiteSpace(markdown))
                parts.Add(markdown);
        }

        if (parts.Count == 0)
            return null;

        return new
        {
            tag = "markdown",
            content = string.Join("\n\n", parts),
        };
    }

    private static IEnumerable<object> BuildFormChildElements(ActionElement action)
    {
        if (action.Kind is ActionElementKind.TextInput or ActionElementKind.Select)
        {
            var label = string.IsNullOrWhiteSpace(action.Label) ? action.ActionId : action.Label;
            if (!string.IsNullOrWhiteSpace(label))
            {
                yield return new
                {
                    tag = "markdown",
                    content = $"**{label}**",
                };
            }
        }

        yield return action.Kind switch
        {
            ActionElementKind.TextInput => BuildFormInput(action),
            ActionElementKind.Select => BuildFormSelect(action),
            _ => BuildFormButton(action),
        };
    }

    private static object BuildFormInput(ActionElement action)
    {
        // Lark schema 2.0 input honors `default_value` as the pre-filled textbox content; if we emit
        // it unconditionally even as empty string, the rendered input still shows placeholder ghost
        // text, which defeats the point. So only add it when the caller put something in Value.
        var input = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["tag"] = "input",
            ["name"] = action.ActionId,
            ["input_type"] = "text",
            ["width"] = "fill",
            ["placeholder"] = new
            {
                tag = "plain_text",
                content = action.Placeholder ?? string.Empty,
            },
        };
        if (!string.IsNullOrEmpty(action.Value))
            input["default_value"] = action.Value;
        return input;
    }

    private static object BuildFormSelect(ActionElement action)
    {
        var select = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["tag"] = "select_static",
            ["name"] = action.ActionId,
            ["type"] = "default",
            ["width"] = "default",
            ["placeholder"] = new
            {
                tag = "plain_text",
                content = ResolvePlaceholder(action),
            },
            ["options"] = action.Options.Select(static option => new
            {
                text = new
                {
                    tag = "plain_text",
                    content = string.IsNullOrWhiteSpace(option.Label) ? option.Value : option.Label,
                },
                value = option.Value,
            }).ToArray(),
            ["value"] = BuildActionValueObject(action),
        };
        if (!string.IsNullOrWhiteSpace(action.Value))
            select["initial_option"] = action.Value;
        if (action.IsDisabled)
            select["disabled"] = true;
        return select;
    }

    private static object BuildFormButton(ActionElement action)
    {
        var button = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["tag"] = "button",
            ["type"] = ResolveButtonType(action),
            ["text"] = new
            {
                tag = "plain_text",
                content = string.IsNullOrWhiteSpace(action.Label) ? action.ActionId : action.Label,
            },
            ["behaviors"] = BuildButtonBehaviors(action),
        };
        if (action.Kind != ActionElementKind.Link)
        {
            button["name"] = action.ActionId;
            button["form_action_type"] = "submit";
        }
        if (action.IsDisabled)
            button["disabled"] = true;
        return button;
    }

    private static object BuildAction(ActionElement action) => new
    {
        tag = "button",
        text = new
        {
            tag = "plain_text",
            content = string.IsNullOrWhiteSpace(action.Label) ? action.ActionId : action.Label,
        },
        type = ResolveButtonType(action),
        behaviors = BuildButtonBehaviors(action),
    };

    private static string ResolvePlaceholder(ActionElement action)
    {
        if (!string.IsNullOrWhiteSpace(action.Placeholder))
            return action.Placeholder;
        if (!string.IsNullOrWhiteSpace(action.Label))
            return action.Label;
        return action.ActionId;
    }

    private static string ResolveButtonType(ActionElement action)
    {
        if (action.IsDanger)
            return "danger";
        return action.IsPrimary ? "primary" : "default";
    }

    private static object[] BuildButtonBehaviors(ActionElement action)
    {
        if (action.Kind == ActionElementKind.Link && !string.IsNullOrWhiteSpace(action.Value))
        {
            return new object[]
            {
                new
                {
                    type = "open_url",
                    default_url = action.Value,
                },
            };
        }

        return new object[]
        {
            new
            {
                type = "callback",
                value = BuildActionValueObject(action),
            },
        };
    }

    private static IDictionary<string, object?> BuildActionValueObject(ActionElement action)
    {
        var map = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["action_id"] = action.ActionId,
            ["value"] = action.Value,
            ["action_kind"] = ToBoundaryActionKind(action.Kind),
        };
        CopyWorkflowResumePayload(action.WorkflowResume, map);
        CopyLlmSelectionPayload(action.LlmSelection, map);

        foreach (var argument in action.Arguments)
        {
            if (string.Equals(argument.Key, "action_id", StringComparison.Ordinal) ||
                string.Equals(argument.Key, "value", StringComparison.Ordinal) ||
                string.Equals(argument.Key, "action_kind", StringComparison.Ordinal))
                continue;

            map[argument.Key] = CoerceArgumentValue(argument.Value);
        }

        return map;
    }

    private static string ToBoundaryActionKind(ActionElementKind kind) =>
        kind switch
        {
            ActionElementKind.Select => "select",
            ActionElementKind.TextInput => "text_input",
            ActionElementKind.FormSubmit => "form_submit",
            ActionElementKind.Link => "link",
            ActionElementKind.Button => "button",
            _ => "unspecified",
        };

    private static void CopyWorkflowResumePayload(
        WorkflowResumeActionPayload? payload,
        IDictionary<string, object?> map)
    {
        // Refactor (iter93/cluster-093):
        // Old: workflow resume + LLM selection control semantics lived in the open `arguments` map.
        // New: repository-owned semantics use typed payloads; `arguments` is only for adapter/third-party
        // extension data plus legacy callback JSON inbound compatibility.
        if (payload is null)
            return;

        if (!string.IsNullOrWhiteSpace(payload.ActorId))
            map["actor_id"] = payload.ActorId;
        if (!string.IsNullOrWhiteSpace(payload.RunId))
            map["run_id"] = payload.RunId;
        if (!string.IsNullOrWhiteSpace(payload.StepId))
            map["step_id"] = payload.StepId;
        if (payload.HasApproved)
            map["approved"] = payload.Approved;
        if (!string.IsNullOrWhiteSpace(payload.UserInput))
            map["user_input"] = payload.UserInput;
        if (!string.IsNullOrWhiteSpace(payload.EditedContent))
            map["edited_content"] = payload.EditedContent;
        if (!string.IsNullOrWhiteSpace(payload.Feedback))
            map["feedback"] = payload.Feedback;
    }

    private static void CopyLlmSelectionPayload(
        LlmSelectionActionPayload? payload,
        IDictionary<string, object?> map)
    {
        // Refactor (iter93/cluster-093):
        // Old: workflow resume + LLM selection control semantics lived in the open `arguments` map.
        // New: repository-owned semantics use typed payloads; `arguments` is only for adapter/third-party
        // extension data plus legacy callback JSON inbound compatibility.
        if (payload is null)
            return;

        if (!string.IsNullOrWhiteSpace(payload.Action))
            map["llm_action"] = payload.Action;
        if (!string.IsNullOrWhiteSpace(payload.ServiceId))
            map["service_id"] = payload.ServiceId;
        if (!string.IsNullOrWhiteSpace(payload.PresetId))
            map["preset_id"] = payload.PresetId;
        if (!string.IsNullOrWhiteSpace(payload.Model))
            map["model"] = payload.Model;
        if (payload.Page > 0)
            map["page"] = payload.Page;
        if (!string.IsNullOrWhiteSpace(payload.DisplayMode))
            map["display_mode"] = payload.DisplayMode;
    }

    private static object? CoerceArgumentValue(string raw)
    {
        if (bool.TryParse(raw, out var boolean))
            return boolean;
        if (long.TryParse(raw, out var integer))
            return integer;
        return raw;
    }

    private static string BuildCardMarkdown(CardBlock card, bool skipTitle = false)
    {
        var parts = new List<string>();
        if (!skipTitle && !string.IsNullOrWhiteSpace(card.Title))
            parts.Add($"**{card.Title}**");
        if (!string.IsNullOrWhiteSpace(card.Text))
            parts.Add(card.Text);

        foreach (var field in card.Fields)
        {
            if (string.IsNullOrWhiteSpace(field.Title) && string.IsNullOrWhiteSpace(field.Text))
                continue;

            parts.Add($"- {field.Title}: {field.Text}".Trim());
        }

        return string.Join("\n", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private static string Truncate(string? value, int maxLength)
    {
        var text = value ?? string.Empty;
        if (maxLength <= 0)
            return text;

        var textInfo = new StringInfo(text);
        if (textInfo.LengthInTextElements <= maxLength)
            return text;

        var markerInfo = new StringInfo(TruncationMarker);
        var markerLength = markerInfo.LengthInTextElements;
        if (maxLength <= markerLength)
            return textInfo.SubstringByTextElements(0, maxLength);

        return textInfo.SubstringByTextElements(0, maxLength - markerLength) + TruncationMarker;
    }
}

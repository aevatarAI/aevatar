using Aevatar.Foundation.Abstractions.Interactions;

namespace Aevatar.GAgents.Channel.Abstractions;

/// <summary>
/// Maps channel-neutral interaction contracts onto outbound channel message content.
/// </summary>
public static class InteractionSpecMapper
{
    /// <summary>
    /// Converts a typed interaction contract into composer-ready message content.
    /// </summary>
    public static MessageContent ToMessageContent(InteractionSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        var content = new MessageContent
        {
            Text = BuildTopLevelText(spec.Title, spec.Body),
            Disposition = MapDisposition(spec.Disposition),
        };

        foreach (var action in spec.Actions)
        {
            var mapped = MapAction(action);
            if (mapped is not null)
                content.Actions.Add(mapped);
        }

        if (ShouldBuildTopLevelCard(spec))
        {
            var card = new CardBlock
            {
                Kind = CardBlockKind.Section,
                Title = spec.Title ?? string.Empty,
                Text = spec.Body ?? string.Empty,
            };
            AppendFields(card, spec.Fields);
            content.Cards.Add(card);
        }

        foreach (var card in spec.Cards)
        {
            var mapped = MapCard(card);
            if (mapped is not null)
                content.Cards.Add(mapped);
        }

        return content;
    }

    private static bool ShouldBuildTopLevelCard(InteractionSpec spec) =>
        !string.IsNullOrWhiteSpace(spec.Title) &&
        spec.Cards.Count == 0 &&
        spec.Fields.Count > 0;

    private static string BuildTopLevelText(string? title, string? body)
    {
        if (string.IsNullOrWhiteSpace(title))
            return body ?? string.Empty;
        if (string.IsNullOrWhiteSpace(body))
            return title;

        return $"{title}\n{body}";
    }

    private static MessageDisposition MapDisposition(InteractionDisposition disposition) =>
        disposition switch
        {
            InteractionDisposition.Normal => MessageDisposition.Normal,
            InteractionDisposition.Ephemeral => MessageDisposition.Ephemeral,
            InteractionDisposition.Silent => MessageDisposition.Silent,
            InteractionDisposition.Pinned => MessageDisposition.Pinned,
            _ => MessageDisposition.Normal,
        };

    private static ActionElement? MapAction(InteractionAction? action)
    {
        if (action is null)
            return null;

        if (string.IsNullOrWhiteSpace(action.ActionId) &&
            string.IsNullOrWhiteSpace(action.Label) &&
            string.IsNullOrWhiteSpace(action.Value) &&
            action.Options.Count == 0)
        {
            return null;
        }

        var element = new ActionElement
        {
            Kind = MapActionKind(action.Kind, action.Options.Count),
            ActionId = action.ActionId ?? string.Empty,
            Label = string.IsNullOrWhiteSpace(action.Label)
                ? action.ActionId ?? string.Empty
                : action.Label,
            Value = action.Value ?? string.Empty,
            Placeholder = action.Placeholder ?? string.Empty,
            IsPrimary = action.Style == InteractionActionStyle.Primary,
            IsDanger = action.Style == InteractionActionStyle.Danger,
            IsDisabled = action.Disabled,
        };
        ApplyApprovalDecision(action, element);

        foreach (var option in action.Options)
        {
            if (string.IsNullOrWhiteSpace(option.Label) && string.IsNullOrWhiteSpace(option.Value))
                continue;

            element.Options.Add(new ActionOption
            {
                Label = option.Label ?? string.Empty,
                Value = option.Value ?? string.Empty,
            });
        }

        return element;
    }

    private static void ApplyApprovalDecision(InteractionAction action, ActionElement element)
    {
        var approved = action.ApprovalDecision switch
        {
            InteractionApprovalDecision.Approve => true,
            InteractionApprovalDecision.Reject => false,
            _ => (bool?)null,
        };
        if (!approved.HasValue)
            return;

        element.WorkflowResume = new WorkflowResumeActionPayload
        {
            Approved = approved.Value,
        };
    }

    private static ActionElementKind MapActionKind(InteractionActionKind kind, int optionCount) =>
        kind switch
        {
            InteractionActionKind.Button => ActionElementKind.Button,
            InteractionActionKind.Select => ActionElementKind.Select,
            InteractionActionKind.FormSubmit => ActionElementKind.FormSubmit,
            InteractionActionKind.Link => ActionElementKind.Link,
            InteractionActionKind.TextInput => ActionElementKind.TextInput,
            _ => optionCount > 0 ? ActionElementKind.Select : ActionElementKind.Button,
        };

    private static CardBlock? MapCard(InteractionCard? card)
    {
        if (card is null)
            return null;

        var block = new CardBlock
        {
            Kind = MapCardKind(card.Kind),
            BlockId = card.BlockId ?? string.Empty,
            Title = card.Title ?? string.Empty,
            Text = card.Text ?? string.Empty,
            ImageUrl = card.ImageUrl ?? string.Empty,
        };

        AppendFields(block, card.Fields);
        foreach (var action in card.Actions)
        {
            var mapped = MapAction(action);
            if (mapped is not null)
                block.Actions.Add(mapped);
        }

        return string.IsNullOrWhiteSpace(block.BlockId) &&
               string.IsNullOrWhiteSpace(block.Title) &&
               string.IsNullOrWhiteSpace(block.Text) &&
               string.IsNullOrWhiteSpace(block.ImageUrl) &&
               block.Fields.Count == 0 &&
               block.Actions.Count == 0
            ? null
            : block;
    }

    private static CardBlockKind MapCardKind(InteractionCardKind kind) =>
        kind switch
        {
            InteractionCardKind.Actions => CardBlockKind.Actions,
            InteractionCardKind.Image => CardBlockKind.Image,
            InteractionCardKind.Context => CardBlockKind.Context,
            InteractionCardKind.Divider => CardBlockKind.Divider,
            _ => CardBlockKind.Section,
        };

    private static void AppendFields(CardBlock block, IEnumerable<InteractionField> fields)
    {
        foreach (var field in fields)
        {
            if (string.IsNullOrWhiteSpace(field.Title) && string.IsNullOrWhiteSpace(field.Text))
                continue;

            block.Fields.Add(new CardField
            {
                Title = field.Title ?? string.Empty,
                Text = field.Text ?? string.Empty,
                IsShort = field.IsShort,
            });
        }
    }
}

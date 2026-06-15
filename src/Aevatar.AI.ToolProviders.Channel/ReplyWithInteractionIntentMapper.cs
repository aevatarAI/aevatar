using Aevatar.GAgents.Channel.Abstractions;

namespace Aevatar.AI.ToolProviders.Channel;

/// <summary>
/// Maps the channel-neutral <see cref="ReplyWithInteractionArguments"/> shape onto a
/// <see cref="MessageContent"/> intent consumable by composers.
/// </summary>
public static class ReplyWithInteractionIntentMapper
{
    /// <summary>Projects the supplied arguments into a <see cref="MessageContent"/> intent.</summary>
    public static MessageContent ToMessageContent(ReplyWithInteractionArguments arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var intent = new MessageContent
        {
            Text = BuildTopLevelText(arguments.Title, arguments.Body),
            Disposition = MessageDisposition.Normal,
        };

        if (arguments.Actions is { Count: > 0 })
        {
            foreach (var action in arguments.Actions)
            {
                var mapped = MapAction(action);
                if (mapped is not null)
                    intent.Actions.Add(mapped);
            }
        }

        if (ShouldBuildTopLevelCard(arguments))
        {
            var card = new CardBlock
            {
                Kind = CardBlockKind.Section,
                Title = arguments.Title ?? string.Empty,
                Text = arguments.Body ?? string.Empty,
            };
            AppendFields(card, arguments.Fields);
            intent.Cards.Add(card);
        }

        if (arguments.Cards is { Count: > 0 })
        {
            foreach (var card in arguments.Cards)
            {
                var mapped = MapCard(card);
                if (mapped is not null)
                    intent.Cards.Add(mapped);
            }
        }

        return intent;
    }

    private static bool ShouldBuildTopLevelCard(ReplyWithInteractionArguments arguments) =>
        !string.IsNullOrWhiteSpace(arguments.Title) &&
        (arguments.Cards is null || arguments.Cards.Count == 0) &&
        arguments.Fields is { Count: > 0 };

    private static string BuildTopLevelText(string? title, string? body)
    {
        if (string.IsNullOrWhiteSpace(title))
            return body ?? string.Empty;
        if (string.IsNullOrWhiteSpace(body))
            return title;

        return $"{title}\n{body}";
    }

    private static ActionElement? MapAction(ReplyActionArgument? action)
    {
        if (action is null)
            return null;
        if (string.IsNullOrWhiteSpace(action.ActionId))
            return null;

        return new ActionElement
        {
            Kind = ActionElementKind.Button,
            ActionId = action.ActionId!,
            Label = action.Label ?? action.ActionId!,
            Value = action.Value ?? string.Empty,
            IsPrimary = string.Equals(action.Style, "primary", StringComparison.OrdinalIgnoreCase),
            IsDanger = string.Equals(action.Style, "danger", StringComparison.OrdinalIgnoreCase),
        };
    }

    private static CardBlock? MapCard(ReplyCardArgument? card)
    {
        if (card is null)
            return null;

        var block = new CardBlock
        {
            Kind = CardBlockKind.Section,
            Title = card.Title ?? string.Empty,
            Text = card.Text ?? string.Empty,
        };

        AppendFields(block, card.Fields);
        if (card.Actions is { Count: > 0 })
        {
            foreach (var action in card.Actions)
            {
                var mapped = MapAction(action);
                if (mapped is not null)
                    block.Actions.Add(mapped);
            }
        }

        return block;
    }

    private static void AppendFields(CardBlock block, List<ReplyFieldArgument>? fields)
    {
        if (fields is null)
            return;

        foreach (var field in fields)
        {
            if (field is null)
                continue;
            if (string.IsNullOrWhiteSpace(field.Title) && string.IsNullOrWhiteSpace(field.Text))
                continue;

            block.Fields.Add(new CardField
            {
                Title = field.Title ?? string.Empty,
                Text = field.Text ?? string.Empty,
            });
        }
    }
}

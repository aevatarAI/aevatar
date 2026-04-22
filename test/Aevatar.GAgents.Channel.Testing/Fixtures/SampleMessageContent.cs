using Aevatar.GAgents.Channel.Abstractions;

namespace Aevatar.GAgents.Channel.Testing;

/// <summary>
/// Minimal <see cref="MessageContent"/> builders shared by the conformance and fault suites.
/// </summary>
public static class SampleMessageContent
{
    /// <summary>
    /// Returns one short plain-text message.
    /// </summary>
    public static MessageContent SimpleText(string text = "hello") => new()
    {
        Text = text,
        Disposition = MessageDisposition.Normal,
    };

    /// <summary>
    /// Returns one message carrying a primary and secondary button action.
    /// </summary>
    public static MessageContent TextWithActions(string text = "choose")
    {
        var content = new MessageContent
        {
            Text = text,
            Disposition = MessageDisposition.Normal,
        };
        content.Actions.Add(new ActionElement
        {
            Kind = ActionElementKind.Button,
            ActionId = "confirm",
            Label = "Confirm",
            Value = "confirm",
            IsPrimary = true,
        });
        content.Actions.Add(new ActionElement
        {
            Kind = ActionElementKind.Button,
            ActionId = "cancel",
            Label = "Cancel",
            Value = "cancel",
        });
        return content;
    }

    /// <summary>
    /// Returns one message carrying a single-section card block and one attachment.
    /// </summary>
    public static MessageContent TextWithCard(string text = "card")
    {
        var content = new MessageContent
        {
            Text = text,
            Disposition = MessageDisposition.Normal,
        };
        var card = new CardBlock
        {
            Kind = CardBlockKind.Section,
            BlockId = "hero",
            Title = "Hero",
            Text = "Hero body",
        };
        card.Fields.Add(new CardField { Title = "Field", Text = "Value", IsShort = true });
        content.Cards.Add(card);
        return content;
    }

    /// <summary>
    /// Returns one message that requests ephemeral delivery.
    /// </summary>
    public static MessageContent Ephemeral(string text = "ephemeral") => new()
    {
        Text = text,
        Disposition = MessageDisposition.Ephemeral,
    };

    /// <summary>
    /// Returns one message with text length strictly greater than the supplied maximum, used for truncation tests.
    /// </summary>
    public static MessageContent Overflowing(int maxLength) => new()
    {
        Text = new string('x', Math.Max(maxLength, 1) + 16),
        Disposition = MessageDisposition.Normal,
    };

    /// <summary>
    /// Returns one message that references one generic attachment.
    /// </summary>
    public static MessageContent TextWithAttachment(string text = "attachment")
    {
        var content = new MessageContent
        {
            Text = text,
            Disposition = MessageDisposition.Normal,
        };
        content.Attachments.Add(new AttachmentRef
        {
            AttachmentId = "att-1",
            Kind = AttachmentKind.Image,
            Name = "screenshot.png",
            ContentType = "image/png",
            BlobRef = "blob://screenshot",
            SizeBytes = 128,
        });
        return content;
    }
}

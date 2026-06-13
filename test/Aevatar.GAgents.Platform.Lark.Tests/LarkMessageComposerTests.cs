using System.Text.Json;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Platform.Lark;
using Aevatar.GAgents.Channel.Testing;
using Shouldly;

namespace Aevatar.GAgents.Platform.Lark.Tests;

public sealed class LarkMessageComposerTests : MessageComposerUnitTests<LarkMessageComposer>
{
    protected override LarkMessageComposer CreateComposer() => new();

    protected override ChannelCapabilities CreateCapabilities() => LarkMessageComposer.DefaultCapabilities.Clone();

    protected override void AssertSimpleTextPayload(object payload, MessageContent intent, ComposeContext context)
    {
        var native = payload.ShouldBeOfType<LarkOutboundMessage>();
        native.MessageType.ShouldBe("text");
        using var document = JsonDocument.Parse(native.ContentJson);
        document.RootElement.GetProperty("text").GetString().ShouldBe(intent.Text);
    }

    protected override void AssertActionsPayload(object payload, MessageContent intent, ComposeContext context, ComposeCapability capability)
    {
        var native = payload.ShouldBeOfType<LarkOutboundMessage>();
        native.MessageType.ShouldBe("interactive");
        native.ContentJson.ShouldContain("Confirm");
        native.ContentJson.ShouldContain("Cancel");
    }

    protected override void AssertCardPayload(object payload, MessageContent intent, ComposeContext context, ComposeCapability capability)
    {
        var native = payload.ShouldBeOfType<LarkOutboundMessage>();
        native.MessageType.ShouldBe("interactive");
        native.ContentJson.ShouldContain("Hero");
        native.ContentJson.ShouldContain("Hero body");
    }

    protected override void AssertOverflowTruncation(object payload, int maxLength)
    {
        var native = payload.ShouldBeOfType<LarkOutboundMessage>();
        native.PlainText.Length.ShouldBeLessThanOrEqualTo(maxLength);
    }

    [Fact]
    public void Compose_WhenTextContainsSurrogatePair_DoesNotSplitTextElement()
    {
        var payload = CreateComposer().Compose(
            new MessageContent
            {
                Text = "A🙂B",
            },
            new ComposeContext
            {
                Conversation = ConversationReference.Create(
                    ChannelId.From("lark"),
                    BotInstanceId.From("bot-1"),
                    ConversationScope.DirectMessage,
                    partition: null,
                    "user-1"),
                Capabilities = new ChannelCapabilities
                {
                    MaxMessageLength = 2,
                },
            });

        payload.PlainText.ShouldBe("A🙂");
    }

    [Fact]
    public void Compose_WhenPlainTextExceedsLegacyTwoThousandChars_DoesNotSilentlyTruncate()
    {
        var text = new string('a', 2_500);

        var payload = CreateComposer().Compose(
            new MessageContent
            {
                Text = text,
            },
            new ComposeContext
            {
                Conversation = ConversationReference.Create(
                    ChannelId.From("lark"),
                    BotInstanceId.From("bot-1"),
                    ConversationScope.DirectMessage,
                    partition: null,
                    "user-1"),
                Capabilities = LarkMessageComposer.DefaultCapabilities.Clone(),
            });

        payload.PlainText.ShouldBe(text);
        using var document = JsonDocument.Parse(payload.ContentJson);
        document.RootElement.GetProperty("text").GetString().ShouldBe(text);
    }

    [Fact]
    public void BuildCloseStreamingSettingsJson_ShouldUseNestedCardKitSettingsShape()
    {
        var json = LarkStreamingCardShell.BuildCloseStreamingSettingsJson();

        json.ShouldBe("""{"config":{"streaming_mode":false}}""");
        using var document = JsonDocument.Parse(json);
        document.RootElement.TryGetProperty("streaming_mode", out _).ShouldBeFalse();
        document.RootElement.GetProperty("config").GetProperty("streaming_mode").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public void Compose_WhenTextExceedsConfiguredLimit_AppendsTruncationMarker()
    {
        var payload = CreateComposer().Compose(
            new MessageContent
            {
                Text = "0123456789ABCDEFGHIJ",
            },
            new ComposeContext
            {
                Conversation = ConversationReference.Create(
                    ChannelId.From("lark"),
                    BotInstanceId.From("bot-1"),
                    ConversationScope.DirectMessage,
                    partition: null,
                    "user-1"),
                Capabilities = new ChannelCapabilities
                {
                    MaxMessageLength = 18,
                },
            });

        payload.PlainText.Length.ShouldBeLessThanOrEqualTo(18);
        payload.PlainText.ShouldEndWith("...[truncated]");
    }

    [Fact]
    public void Compose_WhenRenderingInteractiveCard_UsesLarkV2BodyElements()
    {
        var intent = new MessageContent
        {
            Text = "Choose an agent",
        };
        intent.Cards.Add(new CardBlock
        {
            Title = "Agents",
            Text = "skill-runner",
        });
        intent.Actions.Add(new ActionElement
        {
            Kind = ActionElementKind.Button,
            ActionId = "status",
            Label = "Status",
        });

        var payload = CreateComposer().Compose(
            intent,
            new ComposeContext
            {
                Conversation = ConversationReference.Create(
                    ChannelId.From("lark"),
                    BotInstanceId.From("bot-1"),
                    ConversationScope.DirectMessage,
                    partition: null,
                    "user-1"),
                Capabilities = LarkMessageComposer.DefaultCapabilities.Clone(),
            });

        payload.MessageType.ShouldBe("interactive");
        using var document = JsonDocument.Parse(payload.ContentJson);
        document.RootElement.GetProperty("schema").GetString().ShouldBe("2.0");
        document.RootElement.TryGetProperty("elements", out _).ShouldBeFalse();
        var bodyElements = document.RootElement.GetProperty("body").GetProperty("elements");
        bodyElements.GetArrayLength().ShouldBe(3);
        bodyElements[0].GetProperty("content").GetString().ShouldBe("Choose an agent");
        var cardMarkdown = bodyElements[1].GetProperty("content").GetString();
        cardMarkdown.ShouldNotBeNull();
        cardMarkdown.ShouldContain("skill-runner");
        var button = bodyElements[2];
        button.GetProperty("tag").GetString().ShouldBe("button");
        button.TryGetProperty("value", out _).ShouldBeFalse();
        var behavior = button.GetProperty("behaviors")[0];
        behavior.GetProperty("type").GetString().ShouldBe("callback");
        behavior.GetProperty("value").GetProperty("action_id").GetString().ShouldBe("status");
        behavior.GetProperty("value").GetProperty("action_kind").GetString().ShouldBe("button");
    }

    [Fact]
    public void Compose_WhenActionCarriesLlmPaginationPayload_ProjectsTypedFields()
    {
        var intent = new MessageContent { Text = "Routes" };
        intent.Actions.Add(new ActionElement
        {
            Kind = ActionElementKind.Button,
            ActionId = "llp",
            Label = "Next",
            LlmSelection = new LlmSelectionActionPayload
            {
                Action = "list_page",
                Page = 2,
                DisplayMode = "route",
            },
        });

        var payload = CreateComposer().Compose(
            intent,
            new ComposeContext
            {
                Conversation = ConversationReference.Create(
                    ChannelId.From("lark"),
                    BotInstanceId.From("bot-1"),
                    ConversationScope.DirectMessage,
                    partition: null,
                    "user-1"),
                Capabilities = LarkMessageComposer.DefaultCapabilities.Clone(),
            });

        using var document = JsonDocument.Parse(payload.ContentJson);
        var value = document.RootElement
            .GetProperty("body")
            .GetProperty("elements")[1]
            .GetProperty("behaviors")[0]
            .GetProperty("value");

        value.GetProperty("llm_action").GetString().ShouldBe("list_page");
        value.GetProperty("page").GetInt32().ShouldBe(2);
        value.GetProperty("display_mode").GetString().ShouldBe("route");
    }

    [Fact]
    public void Compose_WhenActionCarriesNyxIdApprovalPayload_ProjectsTypedFields()
    {
        var intent = new MessageContent { Text = "Approval required" };
        intent.Actions.Add(new ActionElement
        {
            Kind = ActionElementKind.Button,
            ActionId = "nyxid-approval-approve",
            Label = "Approve",
            NyxIdApproval = new NyxIdApprovalActionPayload
            {
                RequestId = "nyx-approval-1",
                Approved = true,
            },
            Arguments =
            {
                ["nyxid_approval_request_id"] = "forged-request",
                ["nyxid_approval_approved"] = "false",
            },
        });

        var payload = CreateComposer().Compose(
            intent,
            new ComposeContext
            {
                Conversation = ConversationReference.Create(
                    ChannelId.From("lark"),
                    BotInstanceId.From("bot-1"),
                    ConversationScope.DirectMessage,
                    partition: null,
                    "user-1"),
                Capabilities = LarkMessageComposer.DefaultCapabilities.Clone(),
            });

        using var document = JsonDocument.Parse(payload.ContentJson);
        var value = document.RootElement
            .GetProperty("body")
            .GetProperty("elements")[1]
            .GetProperty("behaviors")[0]
            .GetProperty("value");

        value.GetProperty("action_id").GetString().ShouldBe("nyxid-approval-approve");
        value.GetProperty("nyxid_approval_request_id").GetString().ShouldBe("nyx-approval-1");
        value.GetProperty("nyxid_approval_approved").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public void Compose_WhenSingleCardSuppliesTitle_DoesNotDuplicateInBody()
    {
        // The first card's Title is consumed by the Lark card header (see ResolveHeaderTitle).
        // Form mode already skipped the title in the body markdown, but non-form mode used to
        // re-emit it as `**Title**` right under the header — every single-card response (e.g.
        // /agent-status, /agents in its post-fix unified shape) ended up with a redundant bold
        // title row. Pin the no-duplicate contract here so a refactor cannot regress it.
        var intent = new MessageContent();
        intent.Cards.Add(new CardBlock
        {
            BlockId = "agents_list",
            Title = "Your Agents (1)",
            Text = "1. `summary` · running",
        });
        intent.Actions.Add(new ActionElement
        {
            Kind = ActionElementKind.Button,
            ActionId = "list_agents",
            Label = "Refresh",
        });

        var payload = CreateComposer().Compose(
            intent,
            new ComposeContext
            {
                Conversation = ConversationReference.Create(
                    ChannelId.From("lark"),
                    BotInstanceId.From("bot-1"),
                    ConversationScope.DirectMessage,
                    partition: null,
                    "user-1"),
                Capabilities = LarkMessageComposer.DefaultCapabilities.Clone(),
            });

        using var document = JsonDocument.Parse(payload.ContentJson);
        // Header title appears exactly once (in the header element).
        document.RootElement.GetProperty("header").GetProperty("title").GetProperty("content").GetString()
            .ShouldBe("Your Agents (1)");
        var bodyElements = document.RootElement.GetProperty("body").GetProperty("elements");
        // Two body elements: the card body markdown (without the duplicated title) and the button.
        bodyElements.GetArrayLength().ShouldBe(2);
        var cardMarkdown = bodyElements[0].GetProperty("content").GetString();
        cardMarkdown.ShouldNotBeNull();
        cardMarkdown.ShouldNotContain("**Your Agents (1)**");
        cardMarkdown.ShouldContain("summary");
    }

    [Fact]
    public void Compose_WhenFormInputCarriesValue_RendersLarkDefaultValue()
    {
        var intent = new MessageContent();
        intent.Actions.Add(new ActionElement
        {
            Kind = ActionElementKind.TextInput,
            ActionId = "github_username",
            Label = "GitHub Username",
            Placeholder = "octocat",
            Value = "eanzhao",
        });
        intent.Actions.Add(new ActionElement
        {
            Kind = ActionElementKind.FormSubmit,
            ActionId = "submit",
            Label = "Submit",
            IsPrimary = true,
        });

        var payload = CreateComposer().Compose(
            intent,
            new ComposeContext
            {
                Conversation = ConversationReference.Create(
                    ChannelId.From("lark"),
                    BotInstanceId.From("bot-1"),
                    ConversationScope.DirectMessage,
                    partition: null,
                    "user-1"),
                Capabilities = LarkMessageComposer.DefaultCapabilities.Clone(),
            });

        payload.MessageType.ShouldBe("interactive");
        using var document = JsonDocument.Parse(payload.ContentJson);
        var formElement = document.RootElement
            .GetProperty("body")
            .GetProperty("elements")
            .EnumerateArray()
            .First(e => e.TryGetProperty("tag", out var tag) && tag.GetString() == "form");
        var inputElement = formElement
            .GetProperty("elements")
            .EnumerateArray()
            .First(e => e.TryGetProperty("tag", out var tag) && tag.GetString() == "input");
        inputElement.TryGetProperty("label", out _).ShouldBeFalse();
        inputElement.GetProperty("default_value").GetString().ShouldBe("eanzhao");
    }

    [Fact]
    public void Compose_WhenFormInputHasNoValue_OmitsLarkDefaultValue()
    {
        var intent = new MessageContent();
        intent.Actions.Add(new ActionElement
        {
            Kind = ActionElementKind.TextInput,
            ActionId = "github_username",
            Label = "GitHub Username",
            Placeholder = "octocat",
        });
        intent.Actions.Add(new ActionElement
        {
            Kind = ActionElementKind.FormSubmit,
            ActionId = "submit",
            Label = "Submit",
            IsPrimary = true,
        });

        var payload = CreateComposer().Compose(
            intent,
            new ComposeContext
            {
                Conversation = ConversationReference.Create(
                    ChannelId.From("lark"),
                    BotInstanceId.From("bot-1"),
                    ConversationScope.DirectMessage,
                    partition: null,
                    "user-1"),
                Capabilities = LarkMessageComposer.DefaultCapabilities.Clone(),
            });

        using var document = JsonDocument.Parse(payload.ContentJson);
        var formElement = document.RootElement
            .GetProperty("body")
            .GetProperty("elements")
            .EnumerateArray()
            .First(e => e.TryGetProperty("tag", out var tag) && tag.GetString() == "form");
        var inputElement = formElement
            .GetProperty("elements")
            .EnumerateArray()
            .First(e => e.TryGetProperty("tag", out var tag) && tag.GetString() == "input");
        inputElement.TryGetProperty("default_value", out _).ShouldBeFalse();
    }

    [Fact]
    public void Compose_WhenRenderingFormSubmit_UsesLarkV2CallbackBehavior()
    {
        var intent = new MessageContent();
        intent.Actions.Add(new ActionElement
        {
            Kind = ActionElementKind.TextInput,
            ActionId = "github_username",
            Label = "GitHub Username",
            Placeholder = "octocat",
        });
        var submit = new ActionElement
        {
            Kind = ActionElementKind.FormSubmit,
            ActionId = "submit_daily",
            Label = "Create",
            IsPrimary = true,
        };
        submit.Arguments["agent_builder_action"] = "create_daily";
        intent.Actions.Add(submit);

        var payload = CreateComposer().Compose(
            intent,
            new ComposeContext
            {
                Conversation = ConversationReference.Create(
                    ChannelId.From("lark"),
                    BotInstanceId.From("bot-1"),
                    ConversationScope.DirectMessage,
                    partition: null,
                    "user-1"),
                Capabilities = LarkMessageComposer.DefaultCapabilities.Clone(),
            });

        using var document = JsonDocument.Parse(payload.ContentJson);
        var formElement = document.RootElement
            .GetProperty("body")
            .GetProperty("elements")
            .EnumerateArray()
            .First(e => e.TryGetProperty("tag", out var tag) && tag.GetString() == "form");
        var submitButton = formElement
            .GetProperty("elements")
            .EnumerateArray()
            .First(e => e.TryGetProperty("tag", out var tag) && tag.GetString() == "button");

        submitButton.GetProperty("name").GetString().ShouldBe("submit_daily");
        submitButton.GetProperty("form_action_type").GetString().ShouldBe("submit");
        submitButton.TryGetProperty("value", out _).ShouldBeFalse();
        var behavior = submitButton.GetProperty("behaviors")[0];
        behavior.GetProperty("type").GetString().ShouldBe("callback");
        var value = behavior.GetProperty("value");
        value.GetProperty("action_id").GetString().ShouldBe("submit_daily");
        value.GetProperty("action_kind").GetString().ShouldBe("form_submit");
        value.GetProperty("agent_builder_action").GetString().ShouldBe("create_daily");
    }

    [Fact]
    public void Compose_WhenRenderingSelectAndFormSubmit_UsesLarkFormCardPrimitives()
    {
        var intent = new MessageContent { Text = "Configure deployment" };
        var select = new ActionElement
        {
            Kind = ActionElementKind.Select,
            ActionId = "environment",
            Label = "Environment",
            Placeholder = "Choose environment",
            Value = "prod",
        };
        select.Options.Add(new ActionOption { Label = "Production", Value = "prod" });
        select.Options.Add(new ActionOption { Label = "Staging", Value = "stage" });
        intent.Actions.Add(select);
        intent.Actions.Add(new ActionElement
        {
            Kind = ActionElementKind.TextInput,
            ActionId = "reason",
            Label = "Reason",
            Placeholder = "Why now?",
        });
        intent.Actions.Add(new ActionElement
        {
            Kind = ActionElementKind.FormSubmit,
            ActionId = "submit_deploy",
            Label = "Submit",
            IsPrimary = true,
        });

        var payload = CreateComposer().Compose(
            intent,
            new ComposeContext
            {
                Conversation = ConversationReference.Create(
                    ChannelId.From("lark"),
                    BotInstanceId.From("bot-1"),
                    ConversationScope.DirectMessage,
                    partition: null,
                    "user-1"),
                Capabilities = LarkMessageComposer.DefaultCapabilities.Clone(),
            });

        payload.MessageType.ShouldBe("interactive");
        using var document = JsonDocument.Parse(payload.ContentJson);
        var formElement = document.RootElement
            .GetProperty("body")
            .GetProperty("elements")
            .EnumerateArray()
            .First(e => e.TryGetProperty("tag", out var tag) && tag.GetString() == "form");
        var formChildren = formElement.GetProperty("elements");
        var selectElement = formChildren
            .EnumerateArray()
            .First(e => e.TryGetProperty("tag", out var tag) && tag.GetString() == "select_static");
        selectElement.GetProperty("name").GetString().ShouldBe("environment");
        selectElement.GetProperty("placeholder").GetProperty("content").GetString().ShouldBe("Choose environment");
        selectElement.GetProperty("initial_option").GetString().ShouldBe("prod");
        selectElement.GetProperty("options").GetArrayLength().ShouldBe(2);
        selectElement.GetProperty("value").GetProperty("action_kind").GetString().ShouldBe("select");

        var submitButton = formChildren
            .EnumerateArray()
            .First(e => e.TryGetProperty("tag", out var tag) &&
                        tag.GetString() == "button" &&
                        e.GetProperty("name").GetString() == "submit_deploy");
        submitButton.GetProperty("form_action_type").GetString().ShouldBe("submit");
        submitButton.GetProperty("behaviors")[0]
            .GetProperty("value")
            .GetProperty("action_kind")
            .GetString()
            .ShouldBe("form_submit");
    }

    [Fact]
    public void Compose_WhenRenderingDisabledSelectAndFormSubmit_EmitsLarkDisabledFlags()
    {
        var intent = new MessageContent { Text = "Configure deployment" };
        var select = new ActionElement
        {
            Kind = ActionElementKind.Select,
            ActionId = "environment",
            Label = "Environment",
            Placeholder = "Choose environment",
            IsDisabled = true,
        };
        select.Options.Add(new ActionOption { Label = "Production", Value = "prod" });
        intent.Actions.Add(select);
        intent.Actions.Add(new ActionElement
        {
            Kind = ActionElementKind.FormSubmit,
            ActionId = "submit_deploy",
            Label = "Submit",
            IsDisabled = true,
        });

        var payload = CreateComposer().Compose(
            intent,
            new ComposeContext
            {
                Conversation = ConversationReference.Create(
                    ChannelId.From("lark"),
                    BotInstanceId.From("bot-1"),
                    ConversationScope.DirectMessage,
                    partition: null,
                    "user-1"),
                Capabilities = LarkMessageComposer.DefaultCapabilities.Clone(),
            });

        using var document = JsonDocument.Parse(payload.ContentJson);
        var formChildren = document.RootElement
            .GetProperty("body")
            .GetProperty("elements")
            .EnumerateArray()
            .First(e => e.TryGetProperty("tag", out var tag) && tag.GetString() == "form")
            .GetProperty("elements");
        var selectElement = formChildren
            .EnumerateArray()
            .First(e => e.TryGetProperty("tag", out var tag) && tag.GetString() == "select_static");
        selectElement.GetProperty("name").GetString().ShouldBe("environment");
        selectElement.GetProperty("disabled").GetBoolean().ShouldBeTrue();

        var submitButton = formChildren
            .EnumerateArray()
            .First(e => e.TryGetProperty("tag", out var tag) &&
                        tag.GetString() == "button" &&
                        e.GetProperty("name").GetString() == "submit_deploy");
        submitButton.GetProperty("disabled").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public void Compose_WhenCardContainsActions_RendersThoseActions()
    {
        var intent = new MessageContent();
        var card = new CardBlock
        {
            Title = "Report",
            Text = "Ready",
        };
        card.Actions.Add(new ActionElement
        {
            Kind = ActionElementKind.Button,
            ActionId = "open_report",
            Label = "Open",
        });
        intent.Cards.Add(card);

        var payload = CreateComposer().Compose(
            intent,
            new ComposeContext
            {
                Conversation = ConversationReference.Create(
                    ChannelId.From("lark"),
                    BotInstanceId.From("bot-1"),
                    ConversationScope.DirectMessage,
                    partition: null,
                    "user-1"),
                Capabilities = LarkMessageComposer.DefaultCapabilities.Clone(),
            });

        using var document = JsonDocument.Parse(payload.ContentJson);
        var button = document.RootElement
            .GetProperty("body")
            .GetProperty("elements")
            .EnumerateArray()
            .First(e => e.TryGetProperty("tag", out var tag) && tag.GetString() == "button");
        button.GetProperty("behaviors")[0]
            .GetProperty("value")
            .GetProperty("action_id")
            .GetString()
            .ShouldBe("open_report");
    }
}

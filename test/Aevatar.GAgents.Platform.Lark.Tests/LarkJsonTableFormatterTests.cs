using System.Text.Json;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Platform.Lark;
using Shouldly;

namespace Aevatar.GAgents.Platform.Lark.Tests;

public sealed class LarkJsonTableFormatterTests
{
    [Fact]
    public void Compose_WhenTextIsJsonObject_ShouldRenderNativeTable()
    {
        var payload = Compose("""{"name":"Ada","active":true,"score":42}""");

        payload.MessageType.ShouldBe("interactive");
        payload.IsInteractive.ShouldBeTrue();
        payload.PlainText.ShouldContain("| name | active | score |");
        payload.PlainText.ShouldNotContain("{\"name\"");

        using var document = JsonDocument.Parse(payload.ContentJson);
        var elements = document.RootElement.GetProperty("body").GetProperty("elements");
        elements.GetArrayLength().ShouldBe(1);
        var table = elements[0];
        table.GetProperty("tag").GetString().ShouldBe("table");
        table.GetProperty("page_size").GetInt32().ShouldBe(1);
        table.GetProperty("columns")[0].GetProperty("display_name").GetString().ShouldBe("name");
        table.GetProperty("columns")[1].GetProperty("display_name").GetString().ShouldBe("active");
        table.GetProperty("rows")[0].GetProperty("c0").GetString().ShouldBe("Ada");
        table.GetProperty("rows")[0].GetProperty("c1").GetString().ShouldBe("true");
        table.GetProperty("rows")[0].GetProperty("c2").GetString().ShouldBe("42");
    }

    [Fact]
    public void Compose_WhenTextIsJsonObjectArray_ShouldRenderObjectsAsRows()
    {
        var payload = Compose("""[{"name":"Ada","role":"admin"},{"name":"Lin","role":"viewer"}]""");

        using var document = JsonDocument.Parse(payload.ContentJson);
        var table = document.RootElement.GetProperty("body").GetProperty("elements")[0];
        table.GetProperty("tag").GetString().ShouldBe("table");
        table.GetProperty("rows").GetArrayLength().ShouldBe(2);
        table.GetProperty("rows")[0].GetProperty("c0").GetString().ShouldBe("Ada");
        table.GetProperty("rows")[1].GetProperty("c0").GetString().ShouldBe("Lin");
        table.GetProperty("rows")[1].GetProperty("c1").GetString().ShouldBe("viewer");
    }

    [Fact]
    public void Compose_WhenJsonContainsNestedValues_ShouldFlattenWithoutJsonCells()
    {
        var payload = Compose("""{"profile":{"name":"Ada"},"tags":["alpha","beta"]}""");

        using var document = JsonDocument.Parse(payload.ContentJson);
        var table = document.RootElement.GetProperty("body").GetProperty("elements")[0];
        table.GetProperty("columns")[0].GetProperty("display_name").GetString().ShouldBe("profile.name");
        table.GetProperty("columns")[1].GetProperty("display_name").GetString().ShouldBe("tags");
        table.GetProperty("rows")[0].GetProperty("c0").GetString().ShouldBe("Ada");
        table.GetProperty("rows")[0].GetProperty("c1").GetString().ShouldBe("1. alpha\n2. beta");
        payload.PlainText.ShouldNotContain("{\"profile\"");
        payload.PlainText.ShouldNotContain("[\"alpha\"");
    }

    [Fact]
    public void Compose_WhenTextContainsFencedJson_ShouldKeepProseAroundNativeTable()
    {
        var payload = Compose(
            """
            Summary:

            ```json
            [{"name":"Ada"},{"name":"Lin"}]
            ```

            Complete.
            """);

        using var document = JsonDocument.Parse(payload.ContentJson);
        var elements = document.RootElement.GetProperty("body").GetProperty("elements");
        elements.GetArrayLength().ShouldBe(3);
        elements[0].GetProperty("tag").GetString().ShouldBe("markdown");
        elements[0].GetProperty("content").GetString().ShouldBe("Summary:");
        elements[1].GetProperty("tag").GetString().ShouldBe("table");
        elements[2].GetProperty("tag").GetString().ShouldBe("markdown");
        elements[2].GetProperty("content").GetString().ShouldBe("Complete.");
    }

    [Fact]
    public void Compose_WhenJsonArrayExceedsRowLimit_ShouldKeepTableWithinOneHundredRows()
    {
        var json = "[" + string.Join(",", Enumerable.Range(1, 105).Select(index => $"{{\"id\":{index}}}")) + "]";

        var payload = Compose(json);

        using var document = JsonDocument.Parse(payload.ContentJson);
        var rows = document.RootElement
            .GetProperty("body")
            .GetProperty("elements")[0]
            .GetProperty("rows");
        rows.GetArrayLength().ShouldBe(100);
        rows[99].GetProperty("c0").GetString().ShouldBe("Additional values were not shown.");
    }

    [Fact]
    public void FormatAsMarkdownTable_WhenJsonIsIncomplete_ShouldLeaveTextUntouched()
    {
        const string incomplete = "result: {\"name\":\"Ada\"";

        LarkJsonTableFormatter.ContainsConvertibleJson(incomplete).ShouldBeFalse();
        LarkJsonTableFormatter.FormatAsMarkdownTable(incomplete).ShouldBe(incomplete);
    }

    [Fact]
    public void FormatAsMarkdownTable_WhenNonJsonCodeFenceContainsJsonText_ShouldLeaveFenceUntouched()
    {
        const string code = """
                            ```javascript
                            const value = {"name":"Ada"};
                            ```
                            """;

        LarkJsonTableFormatter.ContainsConvertibleJson(code).ShouldBeFalse();
        LarkJsonTableFormatter.FormatAsMarkdownTable(code).ShouldBe(code);
    }

    private static LarkOutboundMessage Compose(string text) =>
        new LarkMessageComposer().Compose(
            new MessageContent { Text = text },
            new ComposeContext
            {
                Conversation = ConversationReference.Create(
                    ChannelId.From("lark"),
                    BotInstanceId.From("bot-json-table"),
                    ConversationScope.DirectMessage,
                    partition: null,
                    "user-json-table"),
                Capabilities = LarkMessageComposer.DefaultCapabilities.Clone(),
            });
}

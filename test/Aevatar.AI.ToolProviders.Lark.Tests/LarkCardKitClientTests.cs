using System.Net;
using System.Text;
using System.Text.Json;
using Aevatar.AI.ToolProviders.Lark;
using Aevatar.AI.ToolProviders.NyxId;
using FluentAssertions;
using Xunit;

namespace Aevatar.AI.ToolProviders.Lark.Tests;

public sealed class LarkCardKitClientTests
{
    [Fact]
    public async Task CreateCardAsync_PostsToCardsEndpoint_WithInlineDataObject()
    {
        var (client, handler) = BuildClient("""{"code":0,"data":{"card_id":"card_x"}}""");
        var dataJson = """{"schema":"2.0","config":{"streaming_mode":true},"body":{"elements":[]}}""";

        await client.CreateCardAsync(
            "tok-1",
            new LarkCardKitCreateRequest("card_json", dataJson),
            CancellationToken.None);

        handler.LastRequest!.Method.Should().Be(HttpMethod.Post);
        handler.LastRequest!.RequestUri!.ToString().Should().Be(
            "https://nyx.example.com/api/v1/proxy/s/api-lark-bot/open-apis/cardkit/v1/cards");
        // The DataJson string must be embedded as a nested JSON object, not a JSON-encoded
        // string. Lark CardKit rejects double-encoded payloads with a parse error, so the
        // serializer-level inline embedding is load-bearing.
        using var body = JsonDocument.Parse(handler.LastBody!);
        body.RootElement.GetProperty("type").GetString().Should().Be("card_json");
        body.RootElement.GetProperty("data").ValueKind.Should().Be(JsonValueKind.Object);
        body.RootElement.GetProperty("data").GetProperty("schema").GetString().Should().Be("2.0");
    }

    [Fact]
    public async Task StreamElementContentAsync_PutsToElementContentPath_AndIncludesSequence()
    {
        var (client, handler) = BuildClient("""{"code":0,"data":{}}""");

        await client.StreamElementContentAsync(
            "tok-1",
            new LarkCardKitStreamElementContentRequest(
                CardId: "card_x",
                ElementId: "streaming_main",
                Content: "hello world",
                Sequence: 7,
                IdempotencyKey: "uuid-7"),
            CancellationToken.None);

        handler.LastRequest!.Method.Should().Be(HttpMethod.Put);
        handler.LastRequest!.RequestUri!.ToString().Should().Be(
            "https://nyx.example.com/api/v1/proxy/s/api-lark-bot/open-apis/cardkit/v1/cards/card_x/elements/streaming_main/content");
        using var body = JsonDocument.Parse(handler.LastBody!);
        body.RootElement.GetProperty("content").GetString().Should().Be("hello world");
        body.RootElement.GetProperty("sequence").GetInt64().Should().Be(7L);
        body.RootElement.GetProperty("uuid").GetString().Should().Be("uuid-7");
    }

    [Fact]
    public async Task StreamElementContentAsync_OmitsUuid_WhenIdempotencyKeyIsBlank()
    {
        var (client, handler) = BuildClient("""{"code":0,"data":{}}""");

        await client.StreamElementContentAsync(
            "tok-1",
            new LarkCardKitStreamElementContentRequest(
                CardId: "card_x",
                ElementId: "streaming_main",
                Content: "content",
                Sequence: 1,
                IdempotencyKey: "   "),
            CancellationToken.None);

        // The DTO's IdempotencyKey is whitespace; the client must not emit a `uuid` field
        // (Lark rejects empty uuids on some endpoints).
        using var body = JsonDocument.Parse(handler.LastBody!);
        body.RootElement.TryGetProperty("uuid", out _).Should().BeFalse();
    }

    [Fact]
    public async Task StreamElementContentAsync_UrlEncodesIds_ThatContainReservedCharacters()
    {
        var (client, handler) = BuildClient("""{"code":0,"data":{}}""");

        // Lark CardKit returns card_ids/element_ids as opaque strings; the client must run
        // them through Uri.EscapeDataString or a malformed id would land in the path
        // unescaped. We test space encoding (System.Uri preserves %20 in absolute URI
        // paths); slash encoding (%2F) is also called but .NET's Uri canonicalization
        // unescapes path-segment %2F back to '/' by default, so we only assert what is
        // observable on the wire.
        await client.StreamElementContentAsync(
            "tok-1",
            new LarkCardKitStreamElementContentRequest(
                CardId: "card with space",
                ElementId: "streaming_main",
                Content: "x",
                Sequence: 1),
            CancellationToken.None);

        // Uri.ToString() returns the unescaped form; use AbsoluteUri to inspect the
        // percent-encoded path actually placed on the wire.
        handler.LastRequest!.RequestUri!.AbsoluteUri.Should().Contain("/cards/card%20with%20space/elements/");
    }

    [Fact]
    public async Task SetCardSettingsAsync_PatchesSettingsEndpoint_WithInlineSettingsObject()
    {
        var (client, handler) = BuildClient("""{"code":0,"data":{}}""");

        await client.SetCardSettingsAsync(
            "tok-1",
            new LarkCardKitSettingsRequest(
                CardId: "card_x",
                SettingsJson: """{"streaming_mode":false}""",
                Sequence: 99,
                IdempotencyKey: "uuid-end"),
            CancellationToken.None);

        handler.LastRequest!.Method.Should().Be(new HttpMethod("PATCH"));
        handler.LastRequest!.RequestUri!.ToString().Should().Be(
            "https://nyx.example.com/api/v1/proxy/s/api-lark-bot/open-apis/cardkit/v1/cards/card_x/settings");
        using var body = JsonDocument.Parse(handler.LastBody!);
        body.RootElement.GetProperty("settings").ValueKind.Should().Be(JsonValueKind.Object);
        body.RootElement.GetProperty("settings").GetProperty("streaming_mode").GetBoolean().Should().BeFalse();
        body.RootElement.GetProperty("sequence").GetInt64().Should().Be(99L);
        body.RootElement.GetProperty("uuid").GetString().Should().Be("uuid-end");
    }

    [Fact]
    public async Task UpdateCardAsync_PutsCardJsonInline_AndCarriesSequence()
    {
        var (client, handler) = BuildClient("""{"code":0,"data":{}}""");
        var cardJson = """{"schema":"2.0","body":{"elements":[{"tag":"markdown","content":"final"}]}}""";

        await client.UpdateCardAsync(
            "tok-1",
            new LarkCardKitUpdateRequest(
                CardId: "card_x",
                CardJson: cardJson,
                Sequence: 42),
            CancellationToken.None);

        handler.LastRequest!.Method.Should().Be(HttpMethod.Put);
        handler.LastRequest!.RequestUri!.ToString().Should().Be(
            "https://nyx.example.com/api/v1/proxy/s/api-lark-bot/open-apis/cardkit/v1/cards/card_x");
        using var body = JsonDocument.Parse(handler.LastBody!);
        body.RootElement.GetProperty("card").ValueKind.Should().Be(JsonValueKind.Object);
        body.RootElement.GetProperty("card").GetProperty("body").GetProperty("elements")[0]
            .GetProperty("content").GetString().Should().Be("final");
        body.RootElement.GetProperty("sequence").GetInt64().Should().Be(42L);
        body.RootElement.TryGetProperty("uuid", out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CreateCardAsync_RejectsBlankDataJson(string dataJson)
    {
        var (client, _) = BuildClient("");

        var act = async () => await client.CreateCardAsync(
            "tok-1",
            new LarkCardKitCreateRequest("card_json", dataJson),
            CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>()
            .Where(ex => ex.ParamName == "DataJson");
    }

    [Fact]
    public async Task UpdateCardAsync_RejectsMalformedCardJson()
    {
        var (client, _) = BuildClient("");

        var act = async () => await client.UpdateCardAsync(
            "tok-1",
            new LarkCardKitUpdateRequest(CardId: "card_x", CardJson: "{not json", Sequence: 1),
            CancellationToken.None);

        // ParseJsonObject surfaces the underlying System.Text.Json error rather than letting
        // a malformed payload reach Lark with a 400.
        await act.Should().ThrowAsync<JsonException>();
    }

    [Fact]
    public async Task LarkCardKitClient_IsRegisteredAsSingleton_AfterAddLarkTools()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddLarkTools(opts => opts.ProviderSlug = "api-lark-bot");

        services.Should().ContainSingle(d => d.ServiceType == typeof(ILarkCardKitClient)
            && d.ImplementationType == typeof(LarkCardKitClient));
    }

    private static (LarkCardKitClient client, RecordingHandler handler) BuildClient(string responseJson)
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseJson, Encoding.UTF8, "application/json"),
        });
        var client = new LarkCardKitClient(
            new LarkToolOptions { ProviderSlug = "api-lark-bot" },
            new NyxIdApiClient(
                new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
                new HttpClient(handler)));
        return (client, handler);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return _responder(request);
        }
    }
}

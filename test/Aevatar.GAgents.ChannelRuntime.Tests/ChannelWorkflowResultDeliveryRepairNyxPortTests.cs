using System.Net;
using System.Text;
using System.Text.Json;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.GAgents.Channel.NyxIdRelay;
using FluentAssertions;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class ChannelWorkflowResultDeliveryRepairNyxPortTests
{
    [Fact]
    public async Task RotateListAndRebind_UseExistingNyxResourcesAndTypedSafeResults()
    {
        const string fullKey = "nyxid_ag_secret_alpha";
        var handler = new RecordingHandler(
            """{"id":"key-old-alpha","scopes":"read write"}""",
            """{"id":"key-old-alpha","scopes":"read write proxy"}""",
            """{"id":"key-new-alpha","full_key":"nyxid_ag_secret_alpha","created_at":"2026-07-21T02:00:00Z"}""",
            """[{"id":"key-new-alpha","name":"aevatar-lark-relay-reg-alpha","is_active":true,"created_at":"2026-07-21T02:00:00Z","scopes":"read write proxy"}]""",
            """{"id":"route-alpha","agent_api_key_id":"key-new-alpha","default_agent":true}""");
        var logger = new RecordingLogger<ChannelWorkflowResultDeliveryRepairNyxPort>();
        var port = CreatePort(handler, logger);

        var rotated = await port.RotateAgentKeyAsync(
            "user-bearer-alpha",
            "key-old-alpha",
            CancellationToken.None);
        var keys = await port.ListAgentKeysAsync("user-bearer-alpha", CancellationToken.None);
        await port.RebindConversationRouteAsync(
            "user-bearer-alpha",
            "route-alpha",
            "key-new-alpha",
            CancellationToken.None);

        rotated.ApiKeyId.Should().Be("key-new-alpha");
        rotated.FullKey.Should().Be(fullKey);
        rotated.CreatedAtUtc.Should().Be(
            new DateTimeOffset(2026, 7, 21, 2, 0, 0, TimeSpan.Zero));
        rotated.ToString().Should().NotContain(fullKey);
        keys.Should().ContainSingle().Which.Should().Be(new ChannelNyxAgentKeySummary(
            "key-new-alpha",
            "aevatar-lark-relay-reg-alpha",
            true,
            new DateTimeOffset(2026, 7, 21, 2, 0, 0, TimeSpan.Zero)));
        handler.Requests.Select(static request => (request.Method, request.Path)).Should().Equal(
            (HttpMethod.Get, "/api/v1/api-keys/key-old-alpha"),
            (HttpMethod.Put, "/api/v1/api-keys/key-old-alpha"),
            (HttpMethod.Post, "/api/v1/api-keys/key-old-alpha/rotate"),
            (HttpMethod.Get, "/api/v1/api-keys"),
            (HttpMethod.Put, "/api/v1/channel-conversations/route-alpha"));
        handler.Requests.Should().OnlyContain(request =>
            request.Authorization == "Bearer user-bearer-alpha");
        using var scopeBody = JsonDocument.Parse(handler.Requests[1].Body);
        scopeBody.RootElement.GetProperty("scopes").GetString().Should().Be("read write proxy");
        using var routeBody = JsonDocument.Parse(handler.Requests[4].Body);
        routeBody.RootElement.GetProperty("agent_api_key_id").GetString()
            .Should().Be("key-new-alpha");
        routeBody.RootElement.GetProperty("default_agent").GetBoolean().Should().BeTrue();
        logger.Messages.Should().NotContain(message =>
            message.Contains(fullKey, StringComparison.Ordinal) ||
            message.Contains("user-bearer-alpha", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("rotation", "{\"error\":true,\"status\":403,\"body\":\"nyxid_ag_secret_alpha\"}")]
    [InlineData("rotation", "{\"id\":\"key-new-alpha\"}")]
    [InlineData("rotation", "not-json")]
    [InlineData("list", "[{\"id\":\"key-new-alpha\",\"name\":\"relay\",\"is_active\":true,\"created_at\":\"not-a-date\"}]")]
    [InlineData("route", "{\"error\":true,\"status\":500,\"body\":\"nyxid_ag_secret_alpha\"}")]
    public async Task InvalidNyxResponses_ThrowControlledErrorsWithoutResponseOrSecret(
        string operation,
        string response)
    {
        var handler = operation == "rotation"
            ? new RecordingHandler(
                """{"id":"key-old-alpha","scopes":"read write proxy"}""",
                response)
            : new RecordingHandler(response);
        var logger = new RecordingLogger<ChannelWorkflowResultDeliveryRepairNyxPort>();
        var port = CreatePort(handler, logger);

        Func<Task> act = operation switch
        {
            "rotation" => async () => _ = await port.RotateAgentKeyAsync(
                "user-bearer-alpha",
                "key-old-alpha",
                CancellationToken.None),
            "list" => async () => _ = await port.ListAgentKeysAsync(
                "user-bearer-alpha",
                CancellationToken.None),
            _ => () => port.RebindConversationRouteAsync(
                "user-bearer-alpha",
                "route-alpha",
                "key-new-alpha",
                CancellationToken.None),
        };

        var exception = await act.Should().ThrowAsync<InvalidOperationException>();

        exception.Which.Message.Should().StartWith("channel_workflow_delivery_repair_nyx_");
        exception.Which.ToString().Should().NotContain("nyxid_ag_secret_alpha");
        exception.Which.ToString().Should().NotContain(response);
        logger.Messages.Should().NotContain(message =>
            message.Contains("nyxid_ag_secret_alpha", StringComparison.Ordinal) ||
            message.Contains("user-bearer-alpha", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("reg-alpha", "aevatar-lark-relay-reg-alpha")]
    [InlineData(" 1234567890123456 ", "aevatar-lark-relay-123456789012")]
    public void RelayKeyName_UsesProvisioningConvention(string registrationId, string expected)
    {
        ChannelWorkflowResultDeliveryRepairNyxPort.RelayKeyName(registrationId)
            .Should().Be(expected);
    }

    private static ChannelWorkflowResultDeliveryRepairNyxPort CreatePort(
        RecordingHandler handler,
        ILogger<ChannelWorkflowResultDeliveryRepairNyxPort> logger)
    {
        var client = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler));
        return new ChannelWorkflowResultDeliveryRepairNyxPort(client, logger);
    }

    private sealed class RecordingHandler(params string[] responses) : HttpMessageHandler
    {
        private readonly Queue<string> _responses = new(responses);

        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri!.AbsolutePath,
                request.Headers.Authorization?.ToString() ?? string.Empty,
                body));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    _responses.Count > 0 ? _responses.Dequeue() : "{}",
                    Encoding.UTF8,
                    "application/json"),
            };
        }
    }

    private sealed record RecordedRequest(
        HttpMethod Method,
        string Path,
        string Authorization,
        string Body);

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}

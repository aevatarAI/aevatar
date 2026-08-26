using System.Net;
using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions.Credentials.Testing;
using Aevatar.GAgents.Channel.NyxIdRelay;
using FluentAssertions;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class ChannelNyxIdAgentKeyReadinessPortTests
{
    [Fact]
    public async Task EnsureReadyAsync_LegacyReadWriteKey_AppendsProxyWithSameAgentKey()
    {
        const string rawAgentKey = "nyxid_ag_secret_alpha";
        var vault = new InMemorySecretVault();
        var durable = await StoreAsync(vault, rawAgentKey);
        var handler = new RecordingHandler(
            GeneralKey("read write"),
            """{"id":"key-alpha","scopes":"read write proxy"}""");
        var logger = new RecordingLogger<ChannelNyxIdAgentKeyReadinessPort>();
        var port = CreatePort(vault, handler, logger);

        var result = await port.EnsureReadyAsync(durable);

        result.Should().Be(ChannelNyxIdAgentKeyReadinessResult.Succeeded);
        handler.Requests.Select(static request => (request.Method, request.Path)).Should().Equal(
            (HttpMethod.Get, "/api/v1/api-keys/key-alpha"),
            (HttpMethod.Put, "/api/v1/api-keys/key-alpha"));
        handler.Requests.Should().OnlyContain(request => request.Authorization == $"Bearer {rawAgentKey}");
        using var update = JsonDocument.Parse(handler.Requests[1].Body);
        update.RootElement.GetProperty("scopes").GetString().Should().Be("read write proxy");
        logger.Messages.Should().NotContain(message => message.Contains(rawAgentKey, StringComparison.Ordinal));
    }

    [Fact]
    public async Task EnsureReadyAsync_AlreadyProxyScoped_DoesNotMutateKey()
    {
        var vault = new InMemorySecretVault();
        var durable = await StoreAsync(vault, "nyxid_ag_secret_alpha");
        var handler = new RecordingHandler(GeneralKey("read write proxy"));
        var port = CreatePort(
            vault,
            handler,
            new RecordingLogger<ChannelNyxIdAgentKeyReadinessPort>());

        var result = await port.EnsureReadyAsync(durable);

        result.Ready.Should().BeTrue();
        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Method.Should().Be(HttpMethod.Get);
    }

    [Fact]
    public async Task EnsureReadyAsync_ScheduledInvocationKey_FailsBeforeScopeMutation()
    {
        var vault = new InMemorySecretVault();
        var durable = await StoreAsync(vault, "nyxid_ag_secret_alpha");
        var handler = new RecordingHandler(
            """{"id":"key-alpha","scopes":"read write","purpose":"scheduled_invocation","scheduled_write_enabled":true}""");
        var port = CreatePort(
            vault,
            handler,
            new RecordingLogger<ChannelNyxIdAgentKeyReadinessPort>());

        var result = await port.EnsureReadyAsync(durable);

        result.Ready.Should().BeFalse();
        result.FailureCode.Should().Be("channel_agent_key_rebind_required");
        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Method.Should().Be(HttpMethod.Get);
    }

    [Fact]
    public async Task EnsureReadyAsync_ScheduledInvocationKeySelfReadIsForbidden_RequiresRebind()
    {
        var vault = new InMemorySecretVault();
        var durable = await StoreAsync(vault, "nyxid_ag_secret_alpha");
        var handler = new RecordingHandler(
            """{"error":"durable_grant_mismatch","error_code":9009,"message":"scheduled_invocation API keys are restricted to durable proxy execution routes"}""")
        {
            StatusCode = HttpStatusCode.Forbidden,
        };
        var port = CreatePort(
            vault,
            handler,
            new RecordingLogger<ChannelNyxIdAgentKeyReadinessPort>());

        var result = await port.EnsureReadyAsync(durable);

        result.Ready.Should().BeFalse();
        result.FailureCode.Should().Be("channel_agent_key_rebind_required");
        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Method.Should().Be(HttpMethod.Get);
    }

    [Fact]
    public async Task EnsureReadyAsync_MismatchedVaultDescriptor_FailsClosedBeforeNyxCall()
    {
        var vault = new InMemorySecretVault();
        var durable = await StoreAsync(vault, "nyxid_ag_secret_alpha");
        durable.SecretReference.Fingerprint = "mismatched-fingerprint";
        var handler = new RecordingHandler();
        var port = CreatePort(
            vault,
            handler,
            new RecordingLogger<ChannelNyxIdAgentKeyReadinessPort>());

        var result = await port.EnsureReadyAsync(durable);

        result.Ready.Should().BeFalse();
        result.FailureCode.Should().Be("channel_agent_key_unavailable");
        handler.Requests.Should().BeEmpty();
    }

    private static async Task<DurableCallerCredentialRef> StoreAsync(
        ISecretVault vault,
        string rawAgentKey)
    {
        var stored = await vault.PutAsync(new StoreSecretRequest(
            CredentialSecretPurposes.ChannelWorkflowResultDeliveryAgentKey,
            "scope-alpha",
            "key-alpha",
            rawAgentKey,
            "test-store"));
        return new DurableCallerCredentialRef
        {
            Ref = stored.Reference.Ref,
            Purpose = stored.Reference.Purpose,
            OwnerScopeKey = stored.Reference.OwnerScopeKey,
            SubjectId = "key-alpha",
            SourceKind = DurableCallerCredentialSourceKind.ChannelRegistration,
            SecretReference = stored.Reference.Clone(),
        };
    }

    private static ChannelNyxIdAgentKeyReadinessPort CreatePort(
        ISecretVault vault,
        RecordingHandler handler,
        ILogger<ChannelNyxIdAgentKeyReadinessPort> logger)
    {
        var client = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler));
        return new ChannelNyxIdAgentKeyReadinessPort(vault, client, logger);
    }

    private static string GeneralKey(string scopes) =>
        $$"""{"id":"key-alpha","scopes":"{{scopes}}","purpose":"general","scheduled_write_enabled":false}""";

    private sealed class RecordingHandler(params string[] responses) : HttpMessageHandler
    {
        private readonly Queue<string> _responses = new(responses);

        public List<RecordedRequest> Requests { get; } = [];
        public HttpStatusCode StatusCode { get; init; } = HttpStatusCode.OK;

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
            return new HttpResponseMessage(StatusCode)
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

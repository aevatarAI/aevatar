using System.Net;
using System.Text.Json;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.Tools;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Audit;
using Aevatar.Audit.Abstractions.Identity;
using Aevatar.Audit.Abstractions.Models;
using Aevatar.Audit.Abstractions.Ports;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.GAgents.NyxidChat;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class ChannelNyxIdConnectedServiceInventoryToolSourceTests
{
    [Fact]
    public async Task ExecuteAsync_WithExistingSenderToken_ReadsInventoryWithoutIssuingCapability()
    {
        var handler = new InventoryHandler();
        var broker = Substitute.For<INyxIdCapabilityBroker>();
        var source = CreateSource(handler, broker);
        var context = SenderContext("strict-sender-token");
        var tool = await DiscoverSingleAsync(source, context);

        var outcome = await ExecuteAsync(tool, context, "{}");

        outcome.Kind.Should().Be(AgentToolExecutionOutcomeKind.Executed);
        outcome.ResultJson.Should().Contain("GitHub");
        handler.Authorization.Should().Be("Bearer strict-sender-token");
        handler.Requests.Should().Be(1);
        await broker.DidNotReceiveWithAnyArgs()
            .IssueShortLivedByBindingIdAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task ExecuteAsync_WithoutSenderToken_IssuesBoundSenderCapabilityBeforeInventoryRead()
    {
        var handler = new InventoryHandler();
        var broker = Substitute.For<INyxIdCapabilityBroker>();
        broker.IssueShortLivedByBindingIdAsync(
                Arg.Any<ExternalSubjectRef>(),
                "bnd-sender-1",
                Arg.Is<CapabilityScope>(scope => scope.Value == "proxy"),
                Arg.Any<CancellationToken>())
            .Returns(new CapabilityHandle { AccessToken = "minted-sender-token", Scope = "proxy" });
        var source = CreateSource(handler, broker);
        var context = SenderContext(senderToken: null);
        var tool = await DiscoverSingleAsync(source, context);

        var outcome = await ExecuteAsync(tool, context, "{}");

        outcome.Kind.Should().Be(AgentToolExecutionOutcomeKind.Executed);
        handler.Authorization.Should().Be("Bearer minted-sender-token");
        await broker.Received(1).IssueShortLivedByBindingIdAsync(
            Arg.Is<ExternalSubjectRef>(subject =>
                subject.Platform == "lark" &&
                subject.Tenant == "tenant-1" &&
                subject.ExternalUserId == "sender-1"),
            "bnd-sender-1",
            Arg.Is<CapabilityScope>(scope => scope.Value == "proxy"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_WithoutTypedSenderSubject_FailsClosedWithoutOwnerCredentialFallback()
    {
        var handler = new InventoryHandler();
        var broker = Substitute.For<INyxIdCapabilityBroker>();
        var source = CreateSource(handler, broker);
        var context = SenderContext(senderToken: null) with
        {
            Credentials = new AgentToolCredentials("owner-token", "owner-org-token", null),
            Channel = AgentToolChannelContext.Empty,
        };
        var tool = await DiscoverSingleAsync(source, context);

        var outcome = await ExecuteAsync(tool, context, "{}");

        outcome.Kind.Should().Be(AgentToolExecutionOutcomeKind.Executed);
        ErrorCode(outcome.ResultJson).Should().Be("inventory_capability_unavailable");
        handler.Requests.Should().Be(0);
        await broker.DidNotReceiveWithAnyArgs()
            .IssueShortLivedByBindingIdAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task ExecuteAsync_WithCallerSelectedInstance_RejectsBeforeAnyDownstreamCall()
    {
        var handler = new InventoryHandler();
        var broker = Substitute.For<INyxIdCapabilityBroker>();
        var source = CreateSource(handler, broker);
        var context = SenderContext("strict-sender-token");
        var tool = await DiscoverSingleAsync(source, context);

        var outcome = await ExecuteAsync(tool, context, "{\"user_service_id\":\"forged\"}");

        outcome.Kind.Should().Be(AgentToolExecutionOutcomeKind.Executed);
        ErrorCode(outcome.ResultJson).Should().Be("invalid_arguments");
        handler.Requests.Should().Be(0);
        await broker.DidNotReceiveWithAnyArgs()
            .IssueShortLivedByBindingIdAsync(default!, default!, default!, default);
    }

    private static ChannelNyxIdConnectedServiceInventoryToolSource CreateSource(
        InventoryHandler handler,
        INyxIdCapabilityBroker broker)
    {
        var options = new NyxIdToolOptions { BaseUrl = "https://nyx.test" };
        var client = new NyxIdApiClient(options, new HttpClient(handler));
        return new ChannelNyxIdConnectedServiceInventoryToolSource(
            options,
            new TestNyxIdApiClientFactory(client),
            broker,
            NullLogger<ChannelNyxIdConnectedServiceInventoryToolSource>.Instance);
    }

    private static async Task<IAgentTool> DiscoverSingleAsync(
        IAgentToolSource source,
        AgentToolExecutionContext context)
    {
        using var scope = AgentToolContextScope.Push(context);
        return (await source.DiscoverToolsAsync()).Should().ContainSingle().Subject;
    }

    private static Task<AgentToolExecutionOutcome> ExecuteAsync(
        IAgentTool tool,
        AgentToolExecutionContext context,
        string argumentsJson) =>
        new AdmittedAgentToolExecutor(new AppendedAuditTrail(), new StableIdentityHasher())
            .ExecuteAsync(new AgentToolExecutionRequest(
                tool,
                argumentsJson,
                context,
                AgentToolApprovalContinuationMode.None,
                null));

    private static AgentToolExecutionContext SenderContext(string? senderToken) =>
        AgentToolExecutionContext.Empty with
        {
            Request = new AgentToolRequestIdentity("request-inventory", "call-inventory"),
            Credentials = new AgentToolCredentials(null, null, senderToken),
            Channel = new AgentToolChannelContext(
                "LARK",
                "sender-1",
                "scope-1",
                "message-1",
                "platform-message-1"),
            SenderBinding = new AgentToolSenderBindingContext(
                "bnd-sender-1",
                NyxUserId: null,
                SenderTenant: "tenant-1"),
        };

    private static string? ErrorCode(string resultJson)
    {
        using var document = JsonDocument.Parse(resultJson);
        return document.RootElement.GetProperty("error").GetString();
    }

    private sealed class InventoryHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }
        public string? Authorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests++;
            Authorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {
                      "keys": [
                        {
                          "id": "user-service-1",
                          "slug": "github",
                          "service_id": "catalog-github",
                          "label": "GitHub",
                          "is_active": true,
                          "credential_source": { "type": "personal" }
                        }
                      ]
                    }
                    """),
            });
        }
    }

    private sealed class TestNyxIdApiClientFactory(NyxIdApiClient client) : INyxIdApiClientFactory
    {
        public NyxIdApiClient CreateClient() => client;
    }

    private sealed class AppendedAuditTrail : IAuditTrailAppender
    {
        public Task<AuditTrailAppendResult> AppendAsync(
            AuditRecord record,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(AuditTrailAppendResult.Appended(record.AuditId));
    }

    private sealed class StableIdentityHasher : IAuditActorIdentityHasher
    {
        public AuditActorIdentity Hash(string canonicalActorKey) => new("actor-hash", "key-1");

        public bool Verify(string canonicalActorKey, string auditActorId, string identityKeyId) => true;
    }
}

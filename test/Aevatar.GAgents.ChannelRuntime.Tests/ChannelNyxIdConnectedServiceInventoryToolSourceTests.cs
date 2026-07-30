<<<<<<< HEAD
using System.Net;
using System.Text.Json;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.Tools;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Audit;
using Aevatar.Audit.Abstractions.Identity;
using Aevatar.Audit.Abstractions.Models;
using Aevatar.Audit.Abstractions.Ports;
=======
using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.NyxId.ConnectedServices;
>>>>>>> origin/feat/2026-07-10_scheduled-agent-key-credential
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.GAgents.NyxidChat;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
<<<<<<< HEAD
=======
using Xunit;
>>>>>>> origin/feat/2026-07-10_scheduled-agent-key-credential

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class ChannelNyxIdConnectedServiceInventoryToolSourceTests
{
    [Fact]
<<<<<<< HEAD
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
=======
    public async Task DiscoverToolsAsync_ExposesListOnlySchemaWithoutUnverifiedInstanceIdentity()
    {
        var source = new ChannelNyxIdConnectedServiceInventoryToolSource();
        using var context = AgentToolContextScope.Push(AgentToolExecutionContext.Empty with
        {
>>>>>>> origin/feat/2026-07-10_scheduled-agent-key-credential
            SenderBinding = new AgentToolSenderBindingContext(
                "bnd-sender-1",
                NyxUserId: null,
                SenderTenant: "tenant-1"),
<<<<<<< HEAD
        };

    private static string? ErrorCode(string resultJson)
    {
        using var document = JsonDocument.Parse(resultJson);
        return document.RootElement.GetProperty("error").GetString();
=======
        });

        var tool = (await source.DiscoverToolsAsync()).Should().ContainSingle().Subject;

        using var schema = JsonDocument.Parse(tool.ParametersSchema);
        schema.RootElement.GetProperty("properties").EnumerateObject().Count().Should().Be(0);
        schema.RootElement.GetProperty("additionalProperties").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_WhenCallerSuppliesUnverifiedInstanceIdentity_RejectsBeforeInventoryRead()
    {
        var handler = new InventoryHandler();
        var options = new NyxIdToolOptions { BaseUrl = "https://nyx.test" };
        var issuer = Substitute.For<INyxIdConnectedServiceInventoryCapabilityIssuer>();
        var source = new ChannelNyxIdConnectedServiceInventoryToolSource(
            options,
            new TestNyxIdApiClientFactory(new NyxIdApiClient(
                options,
                new HttpClient(handler))),
            issuer,
            NullLogger<ChannelNyxIdConnectedServiceInventoryToolSource>.Instance);
        using var context = AgentToolContextScope.Push(AgentToolExecutionContext.Empty with
        {
            Credentials = new AgentToolCredentials(
                "bot-owner-access-token",
                "bot-owner-org-token",
                "strict-sender-token"),
            SenderBinding = new AgentToolSenderBindingContext(
                "bnd-sender-1",
                NyxUserId: null,
                SenderTenant: "tenant-1"),
        });
        var tool = (await source.DiscoverToolsAsync()).Should().ContainSingle().Subject;

        var result = await tool.ExecuteAsync("""{"user_service_id":"unverified-service"}""");

        using var document = JsonDocument.Parse(result);
        document.RootElement.GetProperty("error").GetString().Should().Be("invalid_arguments");
        handler.RequestPath.Should().BeNull();
        await issuer.DidNotReceiveWithAnyArgs()
            .IssueByBindingIdAsync(default!, default!, default);
    }

    [Fact]
    public async Task ExecuteAsync_WhenStrictSenderRouteTokenIsUnavailable_UsesBoundSenderInventoryCapability()
    {
        var handler = new InventoryHandler();
        var options = new NyxIdToolOptions { BaseUrl = "https://nyx.test" };
        var clientFactory = new TestNyxIdApiClientFactory(new NyxIdApiClient(
            options,
            new HttpClient(handler)));
        var issuer = Substitute.For<INyxIdConnectedServiceInventoryCapabilityIssuer>();
        issuer
            .IssueByBindingIdAsync(
                Arg.Any<ExternalSubjectRef>(),
                "bnd-sender-1",
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CapabilityHandle
            {
                AccessToken = "inventory-access-token",
                Scope = "proxy",
            }));
        var source = new ChannelNyxIdConnectedServiceInventoryToolSource(
            options,
            clientFactory,
            issuer,
            NullLogger<ChannelNyxIdConnectedServiceInventoryToolSource>.Instance);
        using var context = AgentToolContextScope.Push(AgentToolExecutionContext.Empty with
        {
            Credentials = new AgentToolCredentials(
                "bot-owner-access-token",
                "bot-owner-org-token",
                SenderNyxIdAccessToken: null),
            Channel = new AgentToolChannelContext(
                "legacy-channel-platform",
                "legacy-channel-user",
                "scope-1",
                "message-1",
                null),
            SenderBinding = new AgentToolSenderBindingContext(
                "bnd-sender-1",
                NyxUserId: null,
                SenderTenant: "legacy-channel-tenant"),
            NyxIdAuthority = new AgentToolNyxIdAuthorityContext(
                "lark",
                "tenant-1",
                "ou_sender_1"),
        });

        var tool = (await source.DiscoverToolsAsync()).Should().ContainSingle().Subject;

        tool.Name.Should().Be("nyxid_service_inventory");
        handler.Authorization.Should().BeNull("tool discovery must not query the sender's live inventory");
        handler.RequestPath.Should().BeNull();
        await issuer.DidNotReceiveWithAnyArgs()
            .IssueByBindingIdAsync(default!, default!, default);

        var result = await tool.ExecuteAsync("{}");

        result.Should().Contain("GitHub");
        handler.Authorization.Should().Be("Bearer inventory-access-token");
        handler.RequestPath.Should().Be("/api/v1/keys");
        await issuer.Received(1).IssueByBindingIdAsync(
            Arg.Is<ExternalSubjectRef>(subject =>
                subject.Platform == "lark" &&
                subject.Tenant == "tenant-1" &&
                subject.ExternalUserId == "ou_sender_1"),
            "bnd-sender-1",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_WhenSenderRouteTokenExists_ReusesItWithoutIssuingAnotherCapability()
    {
        var handler = new InventoryHandler();
        var options = new NyxIdToolOptions { BaseUrl = "https://nyx.test" };
        var clientFactory = new TestNyxIdApiClientFactory(new NyxIdApiClient(
            options,
            new HttpClient(handler)));
        var issuer = Substitute.For<INyxIdConnectedServiceInventoryCapabilityIssuer>();
        var source = new ChannelNyxIdConnectedServiceInventoryToolSource(
            options,
            clientFactory,
            issuer,
            NullLogger<ChannelNyxIdConnectedServiceInventoryToolSource>.Instance);
        using var context = AgentToolContextScope.Push(AgentToolExecutionContext.Empty with
        {
            Credentials = new AgentToolCredentials(
                "bot-owner-access-token",
                "bot-owner-org-token",
                "strict-sender-token"),
            Channel = new AgentToolChannelContext(
                "lark",
                "ou_sender_1",
                "scope-1",
                "message-1",
                null),
            SenderBinding = new AgentToolSenderBindingContext(
                "bnd-sender-1",
                NyxUserId: null,
                SenderTenant: "tenant-1"),
        });

        var tool = (await source.DiscoverToolsAsync()).Should().ContainSingle().Subject;

        tool.Name.Should().Be("nyxid_service_inventory");
        handler.Authorization.Should().BeNull("tool discovery must not query the sender's live inventory");
        handler.RequestPath.Should().BeNull();
        await issuer.DidNotReceiveWithAnyArgs()
            .IssueByBindingIdAsync(default!, default!, default);

        var result = await tool.ExecuteAsync("{}");

        result.Should().Contain("GitHub");
        handler.Authorization.Should().Be("Bearer strict-sender-token");
        handler.RequestPath.Should().Be("/api/v1/keys");
        await issuer.DidNotReceiveWithAnyArgs()
            .IssueByBindingIdAsync(default!, default!, default);
    }

    [Fact]
    public async Task ExecuteAsync_WhenInventoryCapabilityCannotBeIssued_ReturnsSanitizedFailure()
    {
        var handler = new InventoryHandler();
        var options = new NyxIdToolOptions { BaseUrl = "https://nyx.test" };
        var clientFactory = new TestNyxIdApiClientFactory(new NyxIdApiClient(
            options,
            new HttpClient(handler)));
        var issuer = Substitute.For<INyxIdConnectedServiceInventoryCapabilityIssuer>();
        issuer
            .IssueByBindingIdAsync(
                Arg.Any<ExternalSubjectRef>(),
                "bnd-sender-1",
                Arg.Any<CancellationToken>())
            .Returns<Task<CapabilityHandle>>(_ => throw new HttpRequestException("NyxID unavailable"));
        var source = new ChannelNyxIdConnectedServiceInventoryToolSource(
            options,
            clientFactory,
            issuer,
            NullLogger<ChannelNyxIdConnectedServiceInventoryToolSource>.Instance);
        using var context = AgentToolContextScope.Push(AgentToolExecutionContext.Empty with
        {
            Credentials = new AgentToolCredentials(
                "bot-owner-access-token",
                "bot-owner-org-token",
                SenderNyxIdAccessToken: null),
            Channel = new AgentToolChannelContext(
                "lark",
                "ou_sender_1",
                "scope-1",
                "message-1",
                null),
            SenderBinding = new AgentToolSenderBindingContext(
                "bnd-sender-1",
                NyxUserId: null,
                SenderTenant: "tenant-1"),
            NyxIdAuthority = new AgentToolNyxIdAuthorityContext(
                "lark",
                "tenant-1",
                "ou_sender_1"),
        });

        var tool = (await source.DiscoverToolsAsync()).Should().ContainSingle().Subject;

        handler.Authorization.Should().BeNull("tool discovery must not query the sender's live inventory");
        await issuer.DidNotReceiveWithAnyArgs()
            .IssueByBindingIdAsync(default!, default!, default);

        var result = await tool.ExecuteAsync("{}");

        tool.Name.Should().Be("nyxid_service_inventory");
        tool.IsReadOnly.Should().BeTrue();
        handler.Authorization.Should().BeNull("the bot owner's credential must never be used for sender inventory");
        using var document = JsonDocument.Parse(result);
        document.RootElement.GetProperty("error").GetString().Should().Be("inventory_capability_unavailable");
        var receipt = tool.CreateResultReceipt("call-1", tool.Name, "{}", result);
        receipt.Should().NotBeNull();
        receipt!.Status.Should().Be(AgentToolReceiptStatus.Error);
        receipt.ErrorCode.Should().Be("NYXID_SERVICE_INVENTORY_FAILED");
        result.Should().NotContain("/init");
    }

    [Fact]
    public async Task ExecuteAsync_WhenTypedNyxIdAuthorityIsMissing_FailsClosedWithoutGuessingChannelSubject()
    {
        var handler = new InventoryHandler();
        var options = new NyxIdToolOptions { BaseUrl = "https://nyx.test" };
        var clientFactory = new TestNyxIdApiClientFactory(new NyxIdApiClient(
            options,
            new HttpClient(handler)));
        var issuer = Substitute.For<INyxIdConnectedServiceInventoryCapabilityIssuer>();
        issuer
            .IssueByBindingIdAsync(
                Arg.Any<ExternalSubjectRef>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CapabilityHandle
            {
                AccessToken = "must-not-be-used",
                Scope = "proxy",
            }));
        var source = new ChannelNyxIdConnectedServiceInventoryToolSource(
            options,
            clientFactory,
            issuer,
            NullLogger<ChannelNyxIdConnectedServiceInventoryToolSource>.Instance);
        using var context = AgentToolContextScope.Push(AgentToolExecutionContext.Empty with
        {
            Credentials = new AgentToolCredentials(
                "bot-owner-access-token",
                "bot-owner-org-token",
                SenderNyxIdAccessToken: null),
            Channel = new AgentToolChannelContext(
                "lark",
                "ou_sender_1",
                "scope-1",
                "message-1",
                null),
            SenderBinding = new AgentToolSenderBindingContext(
                "bnd-sender-1",
                NyxUserId: null,
                SenderTenant: "tenant-1"),
        });

        var tool = (await source.DiscoverToolsAsync()).Should().ContainSingle().Subject;
        var result = await tool.ExecuteAsync("{}");

        using var document = JsonDocument.Parse(result);
        document.RootElement.GetProperty("error").GetString().Should().Be("inventory_capability_unavailable");
        handler.Authorization.Should().BeNull();
        await issuer.DidNotReceiveWithAnyArgs()
            .IssueByBindingIdAsync(default!, default!, default);
    }

    [Fact]
    public async Task ExecuteAsync_WhenCapabilityIssueIsCanceled_PropagatesCancellation()
    {
        using var cts = new CancellationTokenSource();
        var handler = new InventoryHandler();
        var options = new NyxIdToolOptions { BaseUrl = "https://nyx.test" };
        var clientFactory = new TestNyxIdApiClientFactory(new NyxIdApiClient(
            options,
            new HttpClient(handler)));
        var issuer = new CancelingInventoryCapabilityIssuer(cts);
        var source = new ChannelNyxIdConnectedServiceInventoryToolSource(
            options,
            clientFactory,
            issuer,
            NullLogger<ChannelNyxIdConnectedServiceInventoryToolSource>.Instance);
        using var context = AgentToolContextScope.Push(AgentToolExecutionContext.Empty with
        {
            SenderBinding = new AgentToolSenderBindingContext(
                "bnd-sender-1",
                NyxUserId: null,
                SenderTenant: "tenant-1"),
            NyxIdAuthority = new AgentToolNyxIdAuthorityContext(
                "lark",
                "tenant-1",
                "ou_sender_1"),
        });

        var tool = (await source.DiscoverToolsAsync(cts.Token)).Should().ContainSingle().Subject;

        cts.IsCancellationRequested.Should().BeFalse("discovery must not issue a capability");
        Func<Task> act = () => tool.ExecuteAsync("{}", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        handler.Authorization.Should().BeNull();
>>>>>>> origin/feat/2026-07-10_scheduled-agent-key-credential
    }

    private sealed class InventoryHandler : HttpMessageHandler
    {
<<<<<<< HEAD
        public int Requests { get; private set; }
        public string? Authorization { get; private set; }
=======
        public string? Authorization { get; private set; }
        public string? RequestPath { get; private set; }
>>>>>>> origin/feat/2026-07-10_scheduled-agent-key-credential

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
<<<<<<< HEAD
            Requests++;
            Authorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
=======
            Authorization = request.Headers.Authorization?.ToString();
            RequestPath = request.RequestUri?.AbsolutePath;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
>>>>>>> origin/feat/2026-07-10_scheduled-agent-key-credential
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

<<<<<<< HEAD
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
=======
    private sealed class CancelingInventoryCapabilityIssuer(CancellationTokenSource callerCancellation)
        : INyxIdConnectedServiceInventoryCapabilityIssuer
    {
        public Task<CapabilityHandle> IssueByBindingIdAsync(
            ExternalSubjectRef externalSubject,
            string bindingId,
            CancellationToken ct = default)
        {
            callerCancellation.Cancel();
            return Task.FromCanceled<CapabilityHandle>(callerCancellation.Token);
        }
>>>>>>> origin/feat/2026-07-10_scheduled-agent-key-credential
    }
}

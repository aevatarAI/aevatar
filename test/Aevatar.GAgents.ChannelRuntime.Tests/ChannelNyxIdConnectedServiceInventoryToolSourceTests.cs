using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.AgentProfiles;
using Aevatar.AI.Core.Tools;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.NyxId.ConnectedServices;
using Aevatar.Audit;
using Aevatar.Audit.Abstractions.Identity;
using Aevatar.Audit.Abstractions.Models;
using Aevatar.Audit.Abstractions.Ports;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.GAgents.NyxidChat;
using FluentAssertions;
using Google.Protobuf;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

[Collection(ChannelRuntimeTestCollections.NyxIdInventoryRequestContext)]
public sealed class ChannelNyxIdConnectedServiceInventoryToolSourceTests
{
    [Fact]
    public async Task DiscoverToolsAsync_ExposesListOnlySchemaWithoutUnverifiedInstanceIdentity()
    {
        var source = new ChannelNyxIdConnectedServiceInventoryToolSource(new RecordingExecutionPort());
        using var context = AgentToolContextScope.Push(AgentToolExecutionContext.Empty with
        {
            SenderBinding = new AgentToolSenderBindingContext(
                "bnd-sender-1",
                NyxUserId: null,
                SenderTenant: "tenant-1"),
        });

        var tool = InventoryTool(await source.DiscoverToolsAsync());

        using var schema = JsonDocument.Parse(tool.ParametersSchema);
        schema.RootElement.GetProperty("properties").EnumerateObject().Count().Should().Be(0);
        schema.RootElement.GetProperty("additionalProperties").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_WhenCallerSuppliesUnverifiedInstanceIdentity_RejectsBeforeInventoryRead()
    {
        var handler = new InventoryHandler();
        var options = new NyxIdToolOptions { BaseUrl = "https://nyx.test" };
        var issuer = Substitute.For<INyxIdConnectedServiceCapabilityIssuer>();
        var executionPort = new RecordingExecutionPort();
        var source = new ChannelNyxIdConnectedServiceInventoryToolSource(
            executionPort,
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
        var tool = InventoryTool(await source.DiscoverToolsAsync());

        var result = await tool.ExecuteAsync("""{"user_service_id":"unverified-service"}""");

        using var document = JsonDocument.Parse(result);
        document.RootElement.GetProperty("error").GetString().Should().Be("invalid_arguments");
        handler.RequestPath.Should().BeNull();
        executionPort.Requests.Should().BeEmpty();
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
        var issuer = Substitute.For<INyxIdConnectedServiceCapabilityIssuer>();
        var executionPort = new RecordingExecutionPort();
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
            executionPort,
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
            Request = new AgentToolRequestIdentity("request-inventory-1", "call-inventory-1"),
        });

        var tool = InventoryTool(await source.DiscoverToolsAsync());

        tool.Name.Should().Be("nyxid_service_inventory");
        handler.Authorization.Should().BeNull("tool discovery must not query the sender's live inventory");
        handler.RequestPath.Should().BeNull();
        await issuer.DidNotReceiveWithAnyArgs()
            .IssueByBindingIdAsync(default!, default!, default);

        var result = await tool.ExecuteAsync("{}");

        result.Should().Contain("GitHub");
        handler.Authorization.Should().Be("Bearer inventory-access-token");
        handler.RequestPath.Should().Be("/api/v1/keys");
        executionPort.Requests.Should().ContainSingle();
        executionPort.Requests[0].ArgumentsJson.Should().Be("{}");
        executionPort.Requests[0].ExecutionContext.Request.RequestId.Should().Be("request-inventory-1");
        executionPort.Requests[0].ExecutionContext.Request.CallId.Should().Be("call-inventory-1:inventory-read");
        executionPort.Requests[0].ExecutionContext.Credentials.NyxIdAccessToken
            .Should().Be("inventory-access-token");
        executionPort.Requests[0].ApprovalContinuationMode.Should()
            .Be(AgentToolApprovalContinuationMode.None);
        await issuer.Received(1).IssueByBindingIdAsync(
            Arg.Is<ExternalSubjectRef>(subject =>
                subject.Platform == "lark" &&
                subject.Tenant == "tenant-1" &&
                subject.ExternalUserId == "ou_sender_1"),
            "bnd-sender-1",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_WhenSenderRouteTokenExists_RevalidatesBindingBeforeUsingSenderAuthority()
    {
        var handler = new InventoryHandler();
        var options = new NyxIdToolOptions { BaseUrl = "https://nyx.test" };
        var clientFactory = new TestNyxIdApiClientFactory(new NyxIdApiClient(
            options,
            new HttpClient(handler)));
        var issuer = Substitute.For<INyxIdConnectedServiceCapabilityIssuer>();
        issuer
            .IssueByBindingIdAsync(
                Arg.Any<ExternalSubjectRef>(),
                "bnd-sender-1",
                Arg.Any<CancellationToken>())
            .Returns(new CapabilityHandle { AccessToken = "strict-sender-token", Scope = "proxy" });
        var executionPort = new RecordingExecutionPort();
        var source = new ChannelNyxIdConnectedServiceInventoryToolSource(
            executionPort,
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
            NyxIdAuthority = new AgentToolNyxIdAuthorityContext(
                "lark",
                "tenant-1",
                "ou_sender_1"),
        });

        var tool = InventoryTool(await source.DiscoverToolsAsync());

        tool.Name.Should().Be("nyxid_service_inventory");
        handler.Authorization.Should().BeNull("tool discovery must not query the sender's live inventory");
        handler.RequestPath.Should().BeNull();
        await issuer.DidNotReceiveWithAnyArgs()
            .IssueByBindingIdAsync(default!, default!, default);

        var result = await tool.ExecuteAsync("{}");

        result.Should().Contain("GitHub");
        handler.Authorization.Should().Be("Bearer strict-sender-token");
        handler.RequestPath.Should().Be("/api/v1/keys");
        await issuer.Received(1).IssueByBindingIdAsync(
            Arg.Any<ExternalSubjectRef>(),
            "bnd-sender-1",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_WhenBindingChanged_DoesNotReuseStaleSenderToken()
    {
        var handler = new InventoryHandler();
        var options = new NyxIdToolOptions { BaseUrl = "https://nyx.test" };
        var clientFactory = new TestNyxIdApiClientFactory(new NyxIdApiClient(
            options,
            new HttpClient(handler)));
        var issuer = Substitute.For<INyxIdConnectedServiceCapabilityIssuer>();
        issuer
            .IssueByBindingIdAsync(
                Arg.Any<ExternalSubjectRef>(),
                "bnd-sender-1",
                Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<CapabilityHandle>(new BindingChangedException(
                new ExternalSubjectRef
                {
                    Platform = "lark",
                    Tenant = "tenant-1",
                    ExternalUserId = "ou_sender_1",
                })));
        var source = new ChannelNyxIdConnectedServiceInventoryToolSource(
            new RecordingExecutionPort(),
            options,
            clientFactory,
            issuer,
            NullLogger<ChannelNyxIdConnectedServiceInventoryToolSource>.Instance);
        using var context = AgentToolContextScope.Push(AgentToolExecutionContext.Empty with
        {
            Credentials = new AgentToolCredentials(
                "bot-owner-access-token",
                "bot-owner-org-token",
                "stale-sender-token"),
            SenderBinding = new AgentToolSenderBindingContext(
                "bnd-sender-1",
                NyxUserId: null,
                SenderTenant: "tenant-1"),
            NyxIdAuthority = new AgentToolNyxIdAuthorityContext(
                "lark",
                "tenant-1",
                "ou_sender_1"),
        });

        var tool = InventoryTool(await source.DiscoverToolsAsync());

        var result = await tool.ExecuteAsync("{}");

        using var document = JsonDocument.Parse(result);
        document.RootElement.GetProperty("error").GetString()
            .Should().Be("inventory_capability_unavailable");
        handler.Authorization.Should().BeNull();
        handler.RequestPath.Should().BeNull();
        await issuer.Received(1).IssueByBindingIdAsync(
            Arg.Any<ExternalSubjectRef>(),
            "bnd-sender-1",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_WhenInventoryCapabilityCannotBeIssued_ReturnsSanitizedFailure()
    {
        var handler = new InventoryHandler();
        var options = new NyxIdToolOptions { BaseUrl = "https://nyx.test" };
        var clientFactory = new TestNyxIdApiClientFactory(new NyxIdApiClient(
            options,
            new HttpClient(handler)));
        var issuer = Substitute.For<INyxIdConnectedServiceCapabilityIssuer>();
        var executionPort = new RecordingExecutionPort();
        issuer
            .IssueByBindingIdAsync(
                Arg.Any<ExternalSubjectRef>(),
                "bnd-sender-1",
                Arg.Any<CancellationToken>())
            .Returns<Task<CapabilityHandle>>(_ => throw new HttpRequestException("NyxID unavailable"));
        var source = new ChannelNyxIdConnectedServiceInventoryToolSource(
            executionPort,
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

        var tool = InventoryTool(await source.DiscoverToolsAsync());

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
        var issuer = Substitute.For<INyxIdConnectedServiceCapabilityIssuer>();
        var executionPort = new RecordingExecutionPort();
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
            executionPort,
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

        var tool = InventoryTool(await source.DiscoverToolsAsync());
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
        var executionPort = new RecordingExecutionPort();
        var source = new ChannelNyxIdConnectedServiceInventoryToolSource(
            executionPort,
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

        var tool = InventoryTool(await source.DiscoverToolsAsync(cts.Token));

        cts.IsCancellationRequested.Should().BeFalse("discovery must not issue a capability");
        Func<Task> act = () => tool.ExecuteAsync("{}", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        handler.Authorization.Should().BeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExecuteAsync_ThroughRealAdmission_UsesSenderInventoryWithSeparateCredentialAuthority(
        bool registrationAgentKeyMode)
    {
        var handler = new InventoryHandler();
        var options = new NyxIdToolOptions { BaseUrl = "https://nyx.test" };
        var issuer = Substitute.For<INyxIdConnectedServiceCapabilityIssuer>();
        issuer.IssueByBindingIdAsync(
                Arg.Any<ExternalSubjectRef>(),
                "bnd-sender-1",
                Arg.Any<CancellationToken>())
            .Returns(new CapabilityHandle
            {
                AccessToken = registrationAgentKeyMode ? "inventory-access-token" : "strict-sender-token",
                Scope = "proxy",
            });
        var auditRecords = new List<AuditRecord>();
        var executionPort = CreateAdmittedExecutionPort(auditRecords);
        var source = new ChannelNyxIdConnectedServiceInventoryToolSource(
            executionPort,
            options,
            new TestNyxIdApiClientFactory(new NyxIdApiClient(options, new HttpClient(handler))),
            issuer);
        var outerContext = CreateRegistrationContext();
        if (!registrationAgentKeyMode)
        {
            outerContext = outerContext with
            {
                Credentials = new AgentToolCredentials(
                    "bot-owner-token", "bot-owner-org-token", "strict-sender-token"),
            };
        }
        using var scope = AgentToolContextScope.Push(outerContext);
        var tool = InventoryTool(await source.DiscoverToolsAsync());

        var outcome = await executionPort.ExecuteAsync(new AgentToolExecutionRequest(
            tool, "{}", outerContext, AgentToolApprovalContinuationMode.None, ApprovalGrant: null));

        outcome.Receipt.Status.Should().Be(AgentToolReceiptStatus.Success, outcome.ResultJson);
        outcome.ResultJson.Should().Contain("GitHub");
        outcome.AuditCompleted.Should().BeTrue();
        handler.RequestPath.Should().Be("/api/v1/keys");
        handler.Authorization.Should().Be(registrationAgentKeyMode
            ? "Bearer inventory-access-token"
            : "Bearer strict-sender-token");
        handler.ExecutionContext.Should().NotBeNull();
        var readContext = handler.ExecutionContext!;
        readContext.Credentials.NyxIdCredentialKind.Should().Be(AgentToolNyxIdCredentialKind.SourceReadableUserBearer);
        readContext.Credentials.SenderNyxIdAccessToken.Should().Be(readContext.Credentials.NyxIdAccessToken);
        readContext.CredentialSource.Should().Be(AgentToolCredentialSource.BearerToken);
        readContext.DurableNyxIdCredential.Should().BeNull();
        readContext.ExecutionOwner.Should().BeEquivalentTo(outerContext.ExecutionOwner);
        readContext.SenderBinding.Should().Be(outerContext.SenderBinding);
        readContext.NyxIdAuthority.Should().Be(outerContext.NyxIdAuthority);
        AgentToolRequestContext.Current.Should().BeSameAs(outerContext);

        var terminalRecords = auditRecords.Where(record => record.LifecyclePhase == AuditLifecyclePhase.Terminal).ToArray();
        terminalRecords.Should().HaveCount(2);
        terminalRecords.Should().OnlyContain(record => record.Outcome == AuditOutcome.Success);
        var inventoryAudit = terminalRecords.Single(record => record.OperationName == "nyxid_service_inventory_reader");
        inventoryAudit.CredentialSource.Should().Be(AuditCredentialSource.BearerToken);
        inventoryAudit.Correlation.RequestId.Should().Be("request-inventory-1");
        inventoryAudit.Correlation.CallId.Should().Be("call-inventory-1:inventory-read");
        terminalRecords.Single(record => record.OperationName == "nyxid_service_inventory")
            .Correlation.CallId.Should().Be("call-inventory-1");
        if (registrationAgentKeyMode)
            await issuer.Received(1).IssueByBindingIdAsync(
                Arg.Is<ExternalSubjectRef>(subject => subject.ExternalUserId == "sender-1"),
                "bnd-sender-1", Arg.Any<CancellationToken>());
        else
            await issuer.Received(1).IssueByBindingIdAsync(
                Arg.Is<ExternalSubjectRef>(subject => subject.ExternalUserId == "sender-1"),
                "bnd-sender-1", Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("{unsafe-provider-secret")]
    [InlineData("{}")]
    [InlineData("{\"services\":[]}")]
    [InlineData("{\"keys\":[{\"id\":\"us-incomplete\",\"is_active\":true}]}")]
    public async Task ExecuteAsync_MalformedInventory_RecordsContractFailureInsteadOfEmptySuccess(string response)
    {
        var handler = new InventoryHandler { KeysResponse = response };
        var options = new NyxIdToolOptions { BaseUrl = "https://nyx.test" };
        var issuer = Substitute.For<INyxIdConnectedServiceCapabilityIssuer>();
        issuer.IssueByBindingIdAsync(
                Arg.Any<ExternalSubjectRef>(),
                "bnd-sender-1",
                Arg.Any<CancellationToken>())
            .Returns(new CapabilityHandle { AccessToken = "strict-sender-token", Scope = "proxy" });
        var auditRecords = new List<AuditRecord>();
        var executionPort = CreateAdmittedExecutionPort(auditRecords);
        var source = new ChannelNyxIdConnectedServiceInventoryToolSource(
            executionPort,
            options,
            new TestNyxIdApiClientFactory(new NyxIdApiClient(options, new HttpClient(handler))),
            issuer);
        var context = CreateRegistrationContext() with
        {
            Credentials = new AgentToolCredentials(
                "bot-owner-token", "bot-owner-org-token", "strict-sender-token"),
        };
        using var scope = AgentToolContextScope.Push(context);
        var tool = InventoryTool(await source.DiscoverToolsAsync());

        var outcome = await executionPort.ExecuteAsync(new AgentToolExecutionRequest(
            tool, "{}", context, AgentToolApprovalContinuationMode.None, ApprovalGrant: null));

        outcome.Receipt.Status.Should().Be(AgentToolReceiptStatus.Error);
        outcome.Receipt.ErrorCode.Should().Be("NYXID_SERVICE_INVENTORY_CONTRACT_INVALID");
        outcome.ResultJson.Should().NotContain("instances").And.NotContain("unsafe-provider-secret");
        outcome.Receipt.ResultJson.Should().NotContain("unsafe-provider-secret");
        handler.RequestPath.Should().Be("/api/v1/keys");
        var terminalRecords = auditRecords.Where(record => record.LifecyclePhase == AuditLifecyclePhase.Terminal).ToArray();
        terminalRecords.Should().HaveCount(2);
        terminalRecords.Should().OnlyContain(record => record.Outcome != AuditOutcome.Success);
    }

    [Fact]
    public async Task ExecuteAsync_GenuineEmptyKeys_ReturnsSuccessfulEmptyInventory()
    {
        var handler = new InventoryHandler { KeysResponse = "{\"keys\":[]}" };
        var options = new NyxIdToolOptions { BaseUrl = "https://nyx.test" };
        var issuer = Substitute.For<INyxIdConnectedServiceCapabilityIssuer>();
        issuer.IssueByBindingIdAsync(
                Arg.Any<ExternalSubjectRef>(),
                "bnd-sender-1",
                Arg.Any<CancellationToken>())
            .Returns(new CapabilityHandle { AccessToken = "strict-sender-token", Scope = "proxy" });
        var source = new ChannelNyxIdConnectedServiceInventoryToolSource(
            new RecordingExecutionPort(),
            options,
            new TestNyxIdApiClientFactory(new NyxIdApiClient(options, new HttpClient(handler))),
            issuer);
        using var scope = AgentToolContextScope.Push(CreateRegistrationContext() with
        {
            Credentials = new AgentToolCredentials(null, null, "strict-sender-token"),
        });
        var tool = InventoryTool(await source.DiscoverToolsAsync());

        var result = await tool.ExecuteAsync("{}");

        using var document = JsonDocument.Parse(result);
        document.RootElement.GetProperty("instances").EnumerateArray().Should().BeEmpty();
        tool.CreateResultReceipt("call-empty", tool.Name, "{}", result)!.Status
            .Should().Be(AgentToolReceiptStatus.Success);
        handler.RequestPath.Should().Be("/api/v1/keys");
    }

    [Fact]
    public async Task DiscoverToolsAsync_WithSenderBinding_ExposesInventoryAndRecommendedSkillLoader()
    {
        var source = new ChannelNyxIdConnectedServiceInventoryToolSource(new RecordingExecutionPort());
        using var context = AgentToolContextScope.Push(AgentToolExecutionContext.Empty with
        {
            SenderBinding = new AgentToolSenderBindingContext(
                "bnd-sender-1",
                NyxUserId: null,
                SenderTenant: "tenant-1"),
        });

        var tools = await source.DiscoverToolsAsync();

        tools.Select(static tool => tool.Name).Should().BeEquivalentTo(
            "nyxid_service_inventory",
            "nyxid_load_recommended_skill");
        RecommendedSkillTool(tools).ParametersSchema.Should().Contain("manifest_digest");
    }

    [Fact]
    public async Task LoadRecommendedSkillAsync_WhenRefMatchesCurrentInventory_LoadsExactOrnnMainDocument()
    {
        var manifestDigest = "sha256:" + new string('0', 64);
        var handler = new InventoryHandler { KeysResponse = KeysWithRecommendedSkill(manifestDigest) };
        var options = new NyxIdToolOptions { BaseUrl = "https://nyx.test" };
        var issuer = Substitute.For<INyxIdConnectedServiceCapabilityIssuer>();
        issuer.IssueByBindingIdAsync(
                Arg.Any<ExternalSubjectRef>(),
                "bnd-sender-1",
                Arg.Any<CancellationToken>())
            .Returns(new CapabilityHandle { AccessToken = "strict-sender-token", Scope = "proxy" });
        var fetcher = new RecordingExactFetcher(ExactRemoteSkillFetchResult.Success(
            "11111111-1111-1111-1111-111111111111",
            "1.2",
            "calendar-reader",
            "publisher-alpha",
            ByteString.CopyFrom(new byte[32]),
            "# Calendar Reader\n\nUse list events."));
        var auditRecords = new List<AuditRecord>();
        var executionPort = CreateAdmittedExecutionPort(auditRecords);
        var source = new ChannelNyxIdConnectedServiceInventoryToolSource(
            executionPort,
            options,
            new TestNyxIdApiClientFactory(new NyxIdApiClient(options, new HttpClient(handler))),
            issuer,
            exactSkillFetcher: fetcher);
        var context = CreateRegistrationContext() with
        {
            Credentials = new AgentToolCredentials(null, null, "strict-sender-token"),
        };
        using var scope = AgentToolContextScope.Push(context);
        var tool = RecommendedSkillTool(await source.DiscoverToolsAsync());
        var arguments = $$"""
            {
              "user_service_id":"user-service-1",
              "source":"ornn",
              "skill_id":"11111111-1111-1111-1111-111111111111",
              "literal_version":"1.2",
              "manifest_digest":"{{manifestDigest}}"
            }
            """;

        var outcome = await executionPort.ExecuteAsync(new AgentToolExecutionRequest(
            tool, arguments, context, AgentToolApprovalContinuationMode.None, ApprovalGrant: null));

        using var document = JsonDocument.Parse(outcome.ResultJson);
        document.RootElement.GetProperty("status").GetString().Should().Be("success");
        document.RootElement.GetProperty("main_document").GetString().Should().Contain("Calendar Reader");
        outcome.Receipt.Status.Should().Be(AgentToolReceiptStatus.Success);
        outcome.Receipt.ResultJson.Should().Contain("Calendar Reader");
        fetcher.ObservedToken.Should().Be("strict-sender-token");
        fetcher.ObservedRef.Should().BeEquivalentTo(new ExactRemoteSkillRef
        {
            Guid = "11111111-1111-1111-1111-111111111111",
            LiteralVersion = "1.2",
        });
        var terminalRecords = auditRecords.Where(record => record.LifecyclePhase == AuditLifecyclePhase.Terminal).ToArray();
        terminalRecords.Should().HaveCount(2);
        terminalRecords.Should().OnlyContain(record => record.Outcome == AuditOutcome.Success);
        terminalRecords.Select(record => record.OperationName).Should().BeEquivalentTo(
            "nyxid_load_recommended_skill",
            "nyxid_recommended_skill_reader");
    }

    [Fact]
    public async Task LoadRecommendedSkillAsync_WhenRefIsNotVisible_DoesNotFetchExactSkill()
    {
        var handler = new InventoryHandler { KeysResponse = KeysWithRecommendedSkill("sha256:" + new string('0', 64)) };
        var options = new NyxIdToolOptions { BaseUrl = "https://nyx.test" };
        var issuer = Substitute.For<INyxIdConnectedServiceCapabilityIssuer>();
        issuer.IssueByBindingIdAsync(
                Arg.Any<ExternalSubjectRef>(),
                "bnd-sender-1",
                Arg.Any<CancellationToken>())
            .Returns(new CapabilityHandle { AccessToken = "strict-sender-token", Scope = "proxy" });
        var fetcher = new RecordingExactFetcher(ExactRemoteSkillFetchResult.Failed(
            ExactRemoteSkillFetchFailureCode.Failed));
        var source = new ChannelNyxIdConnectedServiceInventoryToolSource(
            new RecordingExecutionPort(),
            options,
            new TestNyxIdApiClientFactory(new NyxIdApiClient(options, new HttpClient(handler))),
            issuer,
            exactSkillFetcher: fetcher);
        using var scope = AgentToolContextScope.Push(CreateRegistrationContext() with
        {
            Credentials = new AgentToolCredentials(null, null, "strict-sender-token"),
        });
        var tool = RecommendedSkillTool(await source.DiscoverToolsAsync());

        var result = await tool.ExecuteAsync("""
            {
              "user_service_id":"user-service-1",
              "source":"ornn",
              "skill_id":"22222222-2222-2222-2222-222222222222",
              "literal_version":"1.2",
              "manifest_digest":"sha256:0000000000000000000000000000000000000000000000000000000000000000"
            }
            """);

        using var document = JsonDocument.Parse(result);
        document.RootElement.GetProperty("error").GetString().Should().Be("recommended_skill_ref_not_visible");
        fetcher.CallCount.Should().Be(0);
    }

    private static IAgentTool InventoryTool(IReadOnlyList<IAgentTool> tools) =>
        tools.Should().ContainSingle(tool => tool.Name == "nyxid_service_inventory").Subject;

    private static IAgentTool RecommendedSkillTool(IReadOnlyList<IAgentTool> tools) =>
        tools.Should().ContainSingle(tool => tool.Name == "nyxid_load_recommended_skill").Subject;

    private static AgentToolExecutionContext CreateRegistrationContext()
    {
        var reference = new SecretReference
        {
            Ref = "vault://channel/key-1",
            Purpose = CredentialSecretPurposes.ChannelNyxIdAgentKey,
            Fingerprint = "fingerprint-1",
            Version = 1,
            OwnerScopeKey = "scope-1",
            CreatedAtUnixMs = 1,
        };
        return AgentToolExecutionContext.Empty with
        {
            Request = new AgentToolRequestIdentity("request-inventory-1", "call-inventory-1"),
            Caller = new AgentToolCallerContext("scope-1", "owner-1", "response-1"),
            Channel = new AgentToolChannelContext("telegram", "sender-1", "scope-1", "message-1", null),
            SenderBinding = new AgentToolSenderBindingContext("bnd-sender-1", "nyx-sender-1", "tenant-1"),
            NyxIdAuthority = new AgentToolNyxIdAuthorityContext("telegram", "tenant-1", "sender-1"),
            CredentialSource = AgentToolCredentialSource.ChannelRegistration,
            Credentials = new AgentToolCredentials(
                "registration-agent-key", null, null, AgentToolNyxIdCredentialKind.AgentKey),
            DurableNyxIdCredential = new DurableCallerCredentialRef
            {
                Ref = reference.Ref,
                Purpose = reference.Purpose,
                OwnerScopeKey = reference.OwnerScopeKey,
                SubjectId = "key-1",
                SourceKind = DurableCallerCredentialSourceKind.ChannelRegistration,
                SecretReference = reference,
            },
            ExecutionOwner = AgentToolExecutionOwners.ChannelRegistration("registration-1"),
        };
    }

    private static string KeysWithRecommendedSkill(string manifestDigest) => $$"""
        {
          "keys": [
            {
              "id": "user-service-1",
              "slug": "calendar",
              "catalog_service_id": "catalog-calendar",
              "label": "Calendar",
              "is_active": true,
              "connected": true,
              "status": "active",
              "credential_source": { "type": "personal" },
              "recommended_skill_refs": [
                {
                  "source": "ornn",
                  "skill_id": "11111111-1111-1111-1111-111111111111",
                  "literal_version": "1.2",
                  "manifest_digest": "{{manifestDigest}}",
                  "display_name": "Calendar Reader",
                  "recommendation_name": "read-calendar-events",
                  "revision": "rev-1"
                }
              ]
            }
          ]
        }
        """;

    private static AdmittedAgentToolExecutor CreateAdmittedExecutionPort(List<AuditRecord> auditRecords)
    {
        var ledger = Substitute.For<IAgentToolAdmissionLedger>();
        ledger.TryStartAsync(Arg.Any<AgentToolAdmissionFact>(), Arg.Any<CancellationToken>())
            .Returns(new AgentToolAdmissionResult(AgentToolAdmissionStatus.Started));
        var appender = Substitute.For<IAuditTrailAppender>();
        appender.AppendAsync(Arg.Any<AuditRecord>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var record = call.Arg<AuditRecord>();
            auditRecords.Add(record);
            return AuditTrailAppendResult.Appended(record.AuditId);
        });
        var identityHasher = Substitute.For<IAuditActorIdentityHasher>();
        identityHasher.Hash(Arg.Any<string>()).Returns(new AuditActorIdentity("actor-hash", "identity-key-1"));
        return new AdmittedAgentToolExecutor(
            ledger, appender, identityHasher,
            channelRegistrationAuthorityAdmissionPort: Substitute.For<IChannelRegistrationAuthorityAdmissionPort>());
    }

    private sealed class InventoryHandler : HttpMessageHandler
    {
        public string? Authorization { get; private set; }
        public string? RequestPath { get; private set; }
        public AgentToolExecutionContext? ExecutionContext { get; private set; }
        public string? KeysResponse { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Authorization = request.Headers.Authorization?.ToString();
            RequestPath = request.RequestUri?.AbsolutePath;
            ExecutionContext = AgentToolRequestContext.Current;
            var response = RequestPath switch
            {
                "/api/v1/user-services" => """
                    {"services":[{"id":"user-service-1","slug":"github","label":"GitHub",
                     "is_active":true,"credential_source":{"type":"personal"}}]}
                    """,
                "/api/v1/keys" => KeysResponse ?? """
                    {
                      "keys": [
                        {
                          "id": "user-service-1",
                          "slug": "github",
                          "catalog_service_id": "catalog-github",
                          "label": "GitHub",
                          "is_active": true,
                          "connected": true,
                          "status": "active",
                          "credential_source": { "type": "personal" }
                        }
                      ]
                    }
                    """,
                _ => throw new InvalidOperationException("unexpected_inventory_route"),
            };
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(response),
            });
        }
    }

    private sealed class TestNyxIdApiClientFactory(NyxIdApiClient client) : INyxIdApiClientFactory
    {
        public NyxIdApiClient CreateClient() => client;
    }

    private sealed class RecordingExecutionPort : IAgentToolExecutionPort
    {
        public List<AgentToolExecutionRequest> Requests { get; } = [];

        public async Task<AgentToolExecutionOutcome> ExecuteAsync(
            AgentToolExecutionRequest request,
            CancellationToken ct = default)
        {
            Requests.Add(request);
            string resultJson;
            using (AgentToolContextScope.Push(request.ExecutionContext))
                resultJson = await request.Tool.ExecuteAsync(request.ArgumentsJson, ct);
            var receipt = request.Tool.CreateResultReceipt(
                    request.ExecutionContext.Request.CallId ?? string.Empty,
                    request.Tool.Name,
                    request.ArgumentsJson,
                    resultJson)
                ?? new AgentToolReceipt
                {
                    CallId = request.ExecutionContext.Request.CallId ?? string.Empty,
                    ToolName = request.Tool.Name,
                    Status = AgentToolReceiptStatus.Unspecified,
                    ResultJson = resultJson,
                };
            return new AgentToolExecutionOutcome(
                AgentToolExecutionOutcomeKind.Executed,
                resultJson,
                receipt,
                IsMutation: false,
                FailureCode: string.Empty,
                SafeMessage: string.Empty,
                AgentToolExecutionFailureStage.None,
                TerminalInvoked: true,
                Retryable: false,
                AuditCompleted: true);
        }
    }

    private sealed class RecordingExactFetcher(ExactRemoteSkillFetchResult result) : IExactRemoteSkillFetcher
    {
        public int CallCount { get; private set; }
        public string? ObservedToken { get; private set; }
        public ExactRemoteSkillRef? ObservedRef { get; private set; }

        public Task<ExactRemoteSkillFetchResult> FetchAsync(
            string accessToken,
            ExactRemoteSkillRef skillRef,
            CancellationToken ct = default)
        {
            CallCount++;
            ObservedToken = accessToken;
            ObservedRef = skillRef.Clone();
            return Task.FromResult(result);
        }
    }

    private sealed class CancelingInventoryCapabilityIssuer(CancellationTokenSource callerCancellation)
        : INyxIdConnectedServiceCapabilityIssuer
    {
        public Task<CapabilityHandle> IssueByBindingIdAsync(
            ExternalSubjectRef externalSubject,
            string bindingId,
            CancellationToken ct = default)
        {
            callerCancellation.Cancel();
            return Task.FromCanceled<CapabilityHandle>(callerCancellation.Token);
        }
    }
}

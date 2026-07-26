using System.Net;
using System.Text.Json;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.NyxId.ConnectedServices;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.GAgents.NyxidChat;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class ChannelNyxIdConnectedServiceInventoryToolSourceTests
{
    [Fact]
    public async Task DiscoverToolsAsync_WhenStrictSenderRouteTokenIsUnavailable_UsesBoundSenderInventoryCapability()
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

        var tools = await source.DiscoverToolsAsync();

        tools.Should().ContainSingle().Which.Name.Should().Be("nyxid_service_inventory");
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
    public async Task DiscoverToolsAsync_WhenSenderRouteTokenExists_ReusesItWithoutIssuingAnotherCapability()
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

        var tools = await source.DiscoverToolsAsync();

        tools.Should().ContainSingle().Which.Name.Should().Be("nyxid_service_inventory");
        handler.Authorization.Should().Be("Bearer strict-sender-token");
        await issuer.DidNotReceiveWithAnyArgs()
            .IssueByBindingIdAsync(default!, default!, default);
    }

    [Fact]
    public async Task DiscoverToolsAsync_WhenInventoryCapabilityCannotBeIssued_KeepsHonestInventoryFailureTool()
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
        var result = await tool.ExecuteAsync("{}");

        tool.Name.Should().Be("nyxid_service_inventory");
        tool.IsReadOnly.Should().BeTrue();
        handler.Authorization.Should().BeNull("the bot owner's credential must never be used for sender inventory");
        using var document = JsonDocument.Parse(result);
        document.RootElement.GetProperty("error").GetString().Should().Be("inventory_capability_unavailable");
        result.Should().NotContain("/init");
    }

    [Fact]
    public async Task DiscoverToolsAsync_WhenTypedNyxIdAuthorityIsMissing_FailsClosedWithoutGuessingChannelSubject()
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
    public async Task QueryAsync_WhenNyxIdInventoryEndpointRejectsRequest_ReturnsFailureInsteadOfEmptyInventory()
    {
        var handler = new InventoryHandler(HttpStatusCode.Unauthorized);
        var options = new NyxIdToolOptions { BaseUrl = "https://nyx.test" };
        var source = new ChannelNyxIdConnectedServiceInventoryToolSource(
            options,
            new TestNyxIdApiClientFactory(new NyxIdApiClient(
                options,
                new HttpClient(handler))),
            logger: NullLogger<ChannelNyxIdConnectedServiceInventoryToolSource>.Instance);
        var context = AgentToolExecutionContext.Empty with
        {
            Credentials = new AgentToolCredentials(
                NyxIdAccessToken: null,
                NyxIdOrgToken: null,
                SenderNyxIdAccessToken: "strict-sender-token"),
            SenderBinding = new AgentToolSenderBindingContext("bnd-sender-1"),
        };

        var result = await ((INyxIdConnectedServiceInventoryQuery)source).QueryAsync(context);

        result.Inventory.Should().BeNull();
        result.Failure.Should().Be(NyxIdConnectedServiceInventoryQueryFailure.QueryUnavailable);
    }

    private sealed class InventoryHandler(HttpStatusCode statusCode = HttpStatusCode.OK) : HttpMessageHandler
    {
        public string? Authorization { get; private set; }
        public string? RequestPath { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Authorization = request.Headers.Authorization?.ToString();
            RequestPath = request.RequestUri?.AbsolutePath;
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(statusCode == HttpStatusCode.OK
                    ? """
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
                    """
                    : """{"error":"unauthorized"}"""),
            });
        }
    }

    private sealed class TestNyxIdApiClientFactory(NyxIdApiClient client) : INyxIdApiClientFactory
    {
        public NyxIdApiClient CreateClient() => client;
    }
}

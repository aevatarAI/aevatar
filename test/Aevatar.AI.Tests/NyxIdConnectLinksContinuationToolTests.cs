using System.Net;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.NyxId.Tools;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.Tests;

public sealed class NyxIdConnectLinksContinuationToolTests
{
    [Fact]
    public async Task ChannelCreate_WithoutContinuationPort_FailsClosedWithoutIssuingUntrackedLink()
    {
        var handler = new RejectHandler();
        using var client = new NyxIdApiClient(new NyxIdToolOptions { BaseUrl = "https://nyx.example" },
            new HttpClient(handler), NullLogger<NyxIdApiClient>.Instance);
        var tool = new NyxIdConnectLinksTool(client);
        using var scope = AgentToolContextScope.Push(ChannelContext());

        var result = await tool.ExecuteAsync("""{"service_slug":"google-workspace"}""");

        result.Should().Contain("channel_continuation_unavailable");
        handler.RequestCount.Should().Be(0);
    }

    [Fact]
    public async Task ChannelCreate_UsesTrustedContextAndIgnoresModelSuppliedCallbackAndOwner()
    {
        var handler = new RejectHandler();
        using var client = new NyxIdApiClient(new NyxIdToolOptions { BaseUrl = "https://nyx.example" },
            new HttpClient(handler), NullLogger<NyxIdApiClient>.Instance);
        var continuation = new ContinuationPort();
        var tool = new NyxIdConnectLinksTool(client, continuation);
        var context = ChannelContext();
        using var scope = AgentToolContextScope.Push(context);

        var result = await tool.ExecuteAsync("""{"service_slug":"google-workspace","callback_url":"https://attacker.example","target_org_id":"wrong-owner","conversation_actor_id":"wrong-conversation"}""");

        continuation.Context.Should().BeSameAs(context);
        continuation.Request!.CatalogServiceSlug.Should().Be("google-workspace");
        result.Should().Contain("accepted");
        result.Should().Contain("callback-tracked");
        result.Should().NotContain("connect_url");
        handler.RequestCount.Should().Be(0);
    }

    [Fact]
    public async Task ChannelCreate_CanRemintSenderAuthorizationWithoutKeepingRequestBearer()
    {
        using var client = new NyxIdApiClient(new NyxIdToolOptions { BaseUrl = "https://nyx.example" },
            new HttpClient(new RejectHandler()), NullLogger<NyxIdApiClient>.Instance);
        var continuation = new ContinuationPort();
        using var scope = AgentToolContextScope.Push(ChannelContext() with { Credentials = AgentToolCredentials.Empty });

        var result = await new NyxIdConnectLinksTool(client, continuation).ExecuteAsync("""{"service_slug":"google-workspace"}""");

        result.Should().Contain("callback-tracked");
    }

    private static AgentToolExecutionContext ChannelContext() => AgentToolExecutionContext.Empty with
    {
        Credentials = new AgentToolCredentials("registration-owner-token", null, null),
        Channel = AgentToolChannelContext.Empty with { Platform = "telegram", BotRegistrationId = "registration-original" },
        SenderBinding = new AgentToolSenderBindingContext("binding-original", "user-original", "tenant-original"),
    };

    private sealed class RejectHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"id":"untracked","connect_url":"https://nyx.example/connect/untracked","expires_at":"2026-10-01T00:00:00Z"}"""),
            });
        }
    }

    private sealed class ContinuationPort : IChannelConnectLinkContinuationPort
    {
        public AgentToolExecutionContext? Context { get; private set; }
        public ChannelConnectLinkCreateRequest? Request { get; private set; }
        public Task<ChannelConnectLinkCreateResult> CreateAsync(AgentToolExecutionContext context, ChannelConnectLinkCreateRequest request, CancellationToken ct = default)
        {
            Context = context;
            Request = request;
            return Task.FromResult(new ChannelConnectLinkCreateResult("callback-tracked", Accepted: true));
        }
    }
}

using System.IO;
using Aevatar.AI.Abstractions;
using Aevatar.GAgents.NyxidChat;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.AI.Tests;

public class NyxIdChatSupportCoverageTests
{
    [Fact]
    public async Task ActorStore_ShouldNormalizeScopes_AndKeepEnsureIdempotent()
    {
        var store = new NyxIdChatActorStore();

        await store.EnsureActorAsync(" Scope-A ", "actor-1");
        await store.EnsureActorAsync("scope-a", "actor-1");
        var created = await store.CreateActorAsync("SCOPE-A");

        var listed = await store.ListActorsAsync("scope-a");

        listed.Should().HaveCount(2);
        listed.Select(x => x.ActorId).Should().Contain(["actor-1", created.ActorId]);

        (await store.DeleteActorAsync("scope-a", "actor-1")).Should().BeTrue();
        (await store.DeleteActorAsync("scope-a", "actor-1")).Should().BeFalse();
        (await store.ListActorsAsync("scope-a")).Should().ContainSingle(x => x.ActorId == created.ActorId);
    }

    [Fact]
    public void AddNyxIdChat_ShouldRegisterSingletonStore_AndBindRelayOptions()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Aevatar:NyxId:Relay:ResponseTimeoutSeconds"] = "45",
                ["Aevatar:NyxId:Relay:EnableDebugDiagnostics"] = "true",
            })
            .Build();

        var provider = new ServiceCollection()
            .AddNyxIdChat(configuration)
            .BuildServiceProvider();

        var store1 = provider.GetRequiredService<NyxIdChatActorStore>();
        var store2 = provider.GetRequiredService<NyxIdChatActorStore>();
        var options = provider.GetRequiredService<NyxIdRelayOptions>();

        store1.Should().BeSameAs(store2);
        options.ResponseTimeoutSeconds.Should().Be(45);
        options.EnableDebugDiagnostics.Should().BeTrue();
    }

    [Fact]
    public void GenerateActorId_ShouldUseStablePrefix_AndProduceUniqueValues()
    {
        var first = NyxIdChatServiceDefaults.GenerateActorId();
        var second = NyxIdChatServiceDefaults.GenerateActorId();

        first.Should().StartWith($"{NyxIdChatServiceDefaults.ActorIdPrefix}-");
        second.Should().StartWith($"{NyxIdChatServiceDefaults.ActorIdPrefix}-");
        first.Should().NotBe(second);
    }

    [Fact]
    public async Task NyxIdChatSseWriter_ShouldStartStream_AndSerializeFrames()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        var writer = AgentCoverageTestSupport.CreateNonPublicInstance(
            typeof(NyxIdChatGAgent).Assembly,
            "Aevatar.GAgents.NyxidChat.NyxIdChatSseWriter",
            context.Response);

        await AgentCoverageTestSupport.InvokeAsync(writer, "WriteTextStartAsync", "msg-1", CancellationToken.None);
        await AgentCoverageTestSupport.InvokeAsync(writer, "WriteTextDeltaAsync", "hello", CancellationToken.None);
        await AgentCoverageTestSupport.InvokeAsync(
            writer,
            "WriteMediaContentAsync",
            new MediaContentEvent
            {
                Part = new ChatContentPart
                {
                    Kind = ChatContentPartKind.Image,
                    DataBase64 = "AQID",
                    MediaType = "image/png",
                    Name = "preview",
                }
            },
            CancellationToken.None);
        await AgentCoverageTestSupport.InvokeAsync(
            writer,
            "WriteToolApprovalRequestAsync",
            "req-1",
            "connector.run",
            "call-1",
            """{"slug":"telegram"}""",
            true,
            30,
            CancellationToken.None);

        AgentCoverageTestSupport.GetBooleanProperty(writer, "Started").Should().BeTrue();
        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        context.Response.Headers.ContentType.ToString().Should().Be("text/event-stream; charset=utf-8");
        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        body.Should().Contain("TEXT_MESSAGE_START");
        body.Should().Contain("TEXT_MESSAGE_CONTENT");
        body.Should().Contain("MEDIA_CONTENT");
        body.Should().Contain("\"kind\":\"image\"");
        body.Should().Contain("TOOL_APPROVAL_REQUEST");
    }
}

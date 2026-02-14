using Aevatar.Cqrs.Projections.Abstractions;
using Aevatar.Cqrs.Projections.Abstractions.ReadModels;
using Aevatar.Cqrs.Projections.Configuration;
using Aevatar.Cqrs.Projections.DependencyInjection;
using Aevatar.Cqrs.Projections.Reducers;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Hosts.Api.Tests;

public class ChatProjectionRegistrationTests
{
    [Fact]
    public async Task AddChatProjectionReducer_ShouldSupportExternalReducer()
    {
        var services = new ServiceCollection();
        services.AddChatProjectionCqrs();
        services.AddChatProjectionReducer<CustomChatRequestReducer>();

        await using var provider = services.BuildServiceProvider();
        var coordinator = provider.GetRequiredService<IChatProjectionCoordinator>();
        var store = provider.GetRequiredService<IChatRunReadModelStore>();

        var context = new ChatProjectionContext
        {
            RunId = "ext-1",
            RootActorId = "root",
            WorkflowName = "direct",
            StartedAt = DateTimeOffset.UtcNow,
            Input = "hello",
        };

        await coordinator.InitializeAsync(context);
        await coordinator.ProjectAsync(context, Wrap(new ChatRequestEvent { Prompt = "hello" }));
        await coordinator.CompleteAsync(context, []);

        var report = await store.GetAsync(context.RunId);
        report.Should().NotBeNull();
        report!.Timeline.Should().ContainSingle(x => x.Stage == "custom.chat.request");
    }

    [Fact]
    public async Task AddChatProjectionExtensionsFromAssembly_ShouldAutoRegisterReducer()
    {
        var services = new ServiceCollection();
        services.AddChatProjectionCqrs();
        services.AddChatProjectionExtensionsFromAssembly(typeof(CustomChatRequestReducer).Assembly);

        await using var provider = services.BuildServiceProvider();
        var coordinator = provider.GetRequiredService<IChatProjectionCoordinator>();
        var store = provider.GetRequiredService<IChatRunReadModelStore>();

        var context = new ChatProjectionContext
        {
            RunId = "ext-2",
            RootActorId = "root",
            WorkflowName = "direct",
            StartedAt = DateTimeOffset.UtcNow,
            Input = "hello",
        };

        await coordinator.InitializeAsync(context);
        await coordinator.ProjectAsync(context, Wrap(new ChatRequestEvent { Prompt = "hello" }));
        await coordinator.CompleteAsync(context, []);

        var report = await store.GetAsync(context.RunId);
        report.Should().NotBeNull();
        report!.Timeline.Should().ContainSingle(x => x.Stage == "custom.chat.request");
    }

    [Fact]
    public void AddChatProjectionCqrs_MultipleCalls_ShouldUseLastOptions()
    {
        var services = new ServiceCollection();
        services.AddChatProjectionCqrs(options => options.Enabled = true);
        services.AddChatProjectionCqrs(options => options.Enabled = false);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<ChatProjectionOptions>();
        var coordinator = provider.GetRequiredService<IChatProjectionCoordinator>();
        var store = provider.GetRequiredService<IChatRunReadModelStore>();

        options.Enabled.Should().BeFalse();
        coordinator.Should().NotBeNull();
        store.Should().NotBeNull();
    }

    private static EventEnvelope Wrap(IMessage evt, string publisherId = "test") => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
        Payload = Any.Pack(evt),
        PublisherId = publisherId,
        Direction = EventDirection.Down,
    };

    public sealed class CustomChatRequestReducer : ChatRunEventReducerBase<ChatRequestEvent>
    {
        public override int Order => 1000;

        protected override void Reduce(
            ChatRunReport report,
            ChatProjectionContext context,
            EventEnvelope envelope,
            ChatRequestEvent evt,
            DateTimeOffset now)
        {
            report.Timeline.Add(new ChatTimelineEvent
            {
                Timestamp = now,
                Stage = "custom.chat.request",
                Message = evt.Prompt ?? "",
                AgentId = envelope.PublisherId ?? "",
                EventType = envelope.Payload?.TypeUrl ?? "",
            });
        }
    }
}

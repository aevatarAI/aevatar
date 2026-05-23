using System.Reflection;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Hooks;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Callbacks;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.Foundation.Runtime.Streaming;
using Aevatar.Studio.Workspace;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Tools.Cli.Tests;

public sealed class StudioWorkspaceGAgentTests
{
    [Fact]
    public async Task SettingsAndDraftCommands_ShouldPersistAndReplayWorkspaceFacts()
    {
        var services = CreateServices();
        var agent = CreateAgent("studio-workspace-scope-1", services);

        await agent.ActivateAsync();
        await agent.HandleEventAsync(Envelope(new StudioWorkspaceSettingsUpdated
        {
            WorkspaceId = "studio-workspace-scope-1",
            ScopeId = "scope-1",
            Settings = new StudioWorkspaceSettings
            {
                RuntimeBaseUrl = "http://127.0.0.1:5100",
            },
            UpdatedAtUtc = Now(),
        }));
        await agent.HandleEventAsync(Envelope(new StudioWorkflowDraftSaved
        {
            WorkspaceId = "studio-workspace-scope-1",
            ScopeId = "scope-1",
            Draft = new StudioWorkflowDraft
            {
                WorkflowId = "workflow-1",
                Name = "workflow-one",
                FileName = "workflow-one.yaml",
                DirectoryId = "dir-1",
                DirectoryLabel = "Drafts",
                Yaml = "name: workflow-one\nsteps: []\n",
            },
            SavedAtUtc = Now(),
        }));

        agent.State.ScopeId.Should().Be("scope-1");
        agent.State.Settings.RuntimeBaseUrl.Should().Be("http://127.0.0.1:5100");
        agent.State.Drafts.Should().ContainKey("workflow-1");
        agent.State.Drafts["workflow-1"].Version.Should().Be(1);

        var replayed = CreateAgent("studio-workspace-scope-1", services);
        await replayed.ActivateAsync();

        replayed.State.Settings.RuntimeBaseUrl.Should().Be("http://127.0.0.1:5100");
        replayed.State.Drafts.Should().ContainKey("workflow-1");
        replayed.State.Drafts["workflow-1"].Name.Should().Be("workflow-one");
    }

    [Fact]
    public async Task DraftDelete_ShouldRemoveDraftAndBumpVersion()
    {
        var services = CreateServices();
        var agent = CreateAgent("studio-workspace-scope-2", services);

        await agent.ActivateAsync();
        await agent.HandleEventAsync(Envelope(new StudioWorkflowDraftSaved
        {
            WorkspaceId = "studio-workspace-scope-2",
            ScopeId = "scope-2",
            Draft = new StudioWorkflowDraft
            {
                WorkflowId = "workflow-2",
                Name = "workflow-two",
                FileName = "workflow-two.yaml",
                DirectoryId = "dir-1",
                DirectoryLabel = "Drafts",
                Yaml = "name: workflow-two\nsteps: []\n",
            },
            SavedAtUtc = Now(),
        }));

        await agent.HandleEventAsync(Envelope(new StudioWorkflowDraftDeleted
        {
            WorkspaceId = "studio-workspace-scope-2",
            ScopeId = "scope-2",
            WorkflowId = "workflow-2",
            DeletedAtUtc = Now(),
        }));

        agent.State.Drafts.Should().NotContainKey("workflow-2");
        agent.State.LastAppliedEventVersion.Should().Be(2);
    }

    [Fact]
    public async Task DirectoryCommands_ShouldAddReplaceAndRemoveDirectoryFacts()
    {
        var services = CreateServices();
        var agent = CreateAgent("studio-workspace-scope-3", services);

        await agent.ActivateAsync();
        await agent.HandleEventAsync(Envelope(new StudioWorkspaceDirectoryAdded
        {
            WorkspaceId = "studio-workspace-scope-3",
            ScopeId = "scope-3",
            Directory = new StudioWorkspaceDirectory
            {
                DirectoryId = "dir-1",
                Label = "Drafts",
                Path = "/tmp/drafts",
            },
            AddedAtUtc = Now(),
        }));
        await agent.HandleEventAsync(Envelope(new StudioWorkspaceDirectoryAdded
        {
            WorkspaceId = "studio-workspace-scope-3",
            ScopeId = "scope-3",
            Directory = new StudioWorkspaceDirectory
            {
                DirectoryId = "dir-1",
                Label = "Renamed",
                Path = "/tmp/renamed",
            },
            AddedAtUtc = Now(),
        }));

        agent.State.Directories.Should().ContainSingle();
        agent.State.Directories[0].Label.Should().Be("Renamed");
        agent.State.Directories[0].Path.Should().Be("/tmp/renamed");

        await agent.HandleEventAsync(Envelope(new StudioWorkspaceDirectoryRemoved
        {
            WorkspaceId = "studio-workspace-scope-3",
            ScopeId = "scope-3",
            DirectoryId = "dir-1",
            RemovedAtUtc = Now(),
        }));

        agent.State.Directories.Should().BeEmpty();
    }

    [Fact]
    public async Task BuiltInDirectoryRemoval_ShouldKeepDirectoryAndBumpVersion()
    {
        var services = CreateServices();
        var agent = CreateAgent("studio-workspace-scope-4", services);

        await agent.ActivateAsync();
        await agent.HandleEventAsync(Envelope(new StudioWorkspaceDirectoryAdded
        {
            WorkspaceId = "studio-workspace-scope-4",
            ScopeId = "scope-4",
            Directory = new StudioWorkspaceDirectory
            {
                DirectoryId = "dir-built-in",
                Label = "Built-in",
                Path = "/tmp/built-in",
                IsBuiltIn = true,
            },
            AddedAtUtc = Now(),
        }));

        await agent.HandleEventAsync(Envelope(new StudioWorkspaceDirectoryRemoved
        {
            WorkspaceId = "studio-workspace-scope-4",
            ScopeId = "scope-4",
            DirectoryId = "dir-built-in",
            RemovedAtUtc = Now(),
        }));

        agent.State.Directories.Should().ContainSingle(directory =>
            directory.DirectoryId == "dir-built-in" &&
            directory.IsBuiltIn);
        agent.State.LastAppliedEventVersion.Should().Be(2);
    }

    [Fact]
    public async Task DraftSave_WhenDraftExists_ShouldAdvanceVersionAndPreserveCreatedAt()
    {
        var services = CreateServices();
        var agent = CreateAgent("studio-workspace-scope-6", services);
        var createdAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(-5));

        await agent.ActivateAsync();
        await agent.HandleEventAsync(Envelope(new StudioWorkflowDraftSaved
        {
            WorkspaceId = "studio-workspace-scope-6",
            ScopeId = "scope-6",
            Draft = new StudioWorkflowDraft
            {
                WorkflowId = "workflow-6",
                Name = "workflow-six",
                FileName = "workflow-six.yaml",
                DirectoryId = "dir-1",
                DirectoryLabel = "Drafts",
                Yaml = "name: workflow-six\nsteps: []\n",
                CreatedAtUtc = createdAtUtc,
            },
            SavedAtUtc = Now(),
        }));

        await agent.HandleEventAsync(Envelope(new StudioWorkflowDraftSaved
        {
            WorkspaceId = "studio-workspace-scope-6",
            ScopeId = "scope-6",
            Draft = new StudioWorkflowDraft
            {
                WorkflowId = "workflow-6",
                Name = "workflow-six-renamed",
                FileName = "workflow-six-renamed.yaml",
                DirectoryId = "dir-1",
                DirectoryLabel = "Drafts",
                Yaml = "name: workflow-six-renamed\nsteps: []\n",
            },
            SavedAtUtc = Now(),
        }));

        agent.State.Drafts.Should().ContainKey("workflow-6");
        var draft = agent.State.Drafts["workflow-6"];
        draft.Name.Should().Be("workflow-six-renamed");
        draft.Version.Should().Be(2);
        draft.CreatedAtUtc.Should().Be(createdAtUtc);
        agent.State.LastAppliedEventVersion.Should().Be(2);
    }

    [Fact]
    public async Task Commands_WhenExpectedVersionIsStale_ShouldRejectMutation()
    {
        var services = CreateServices();
        var agent = CreateAgent("studio-workspace-scope-5", services);

        await agent.ActivateAsync();
        await agent.HandleEventAsync(Envelope(new StudioWorkspaceSettingsUpdated
        {
            WorkspaceId = "studio-workspace-scope-5",
            ScopeId = "scope-5",
            Settings = new StudioWorkspaceSettings
            {
                RuntimeBaseUrl = "http://127.0.0.1:5100",
            },
            UpdatedAtUtc = Now(),
        }));

        Func<Task> act = () => agent.HandleEventAsync(Envelope(new StudioWorkflowDraftSaved
        {
            WorkspaceId = "studio-workspace-scope-5",
            ScopeId = "scope-5",
            ExpectedVersion = 42,
            Draft = new StudioWorkflowDraft
            {
                WorkflowId = "workflow-5",
                Name = "workflow-five",
                FileName = "workflow-five.yaml",
                DirectoryId = "dir-1",
                DirectoryLabel = "Drafts",
                Yaml = "name: workflow-five\nsteps: []\n",
            },
            SavedAtUtc = Now(),
        }));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .Where(ex => ex.Message.Contains("expected_version", StringComparison.Ordinal));
        agent.State.Drafts.Should().BeEmpty();
        agent.State.LastAppliedEventVersion.Should().Be(1);
    }

    [Fact]
    public async Task Commands_WhenWorkspaceIdentityChanges_ShouldRejectMutation()
    {
        var services = CreateServices();
        var agent = CreateAgent("studio-workspace-scope-7", services);

        await agent.ActivateAsync();
        await agent.HandleEventAsync(Envelope(new StudioWorkspaceSettingsUpdated
        {
            WorkspaceId = "studio-workspace-scope-7",
            ScopeId = "scope-7",
            Settings = new StudioWorkspaceSettings
            {
                RuntimeBaseUrl = "http://127.0.0.1:5100",
            },
            UpdatedAtUtc = Now(),
        }));

        Func<Task> act = () => agent.HandleEventAsync(Envelope(new StudioWorkspaceSettingsUpdated
        {
            WorkspaceId = "studio-workspace-other",
            ScopeId = "scope-7",
            Settings = new StudioWorkspaceSettings
            {
                RuntimeBaseUrl = "http://127.0.0.1:5101",
            },
            UpdatedAtUtc = Now(),
        }));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .Where(ex => ex.Message.Contains("workspace actor already initialized", StringComparison.Ordinal));
        agent.State.WorkspaceId.Should().Be("studio-workspace-scope-7");
        agent.State.Settings.RuntimeBaseUrl.Should().Be("http://127.0.0.1:5100");
        agent.State.LastAppliedEventVersion.Should().Be(1);
    }

    private static ServiceProvider CreateServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IStreamProvider, InMemoryStreamProvider>();
        services.AddSingleton<InMemoryActorRuntimeCallbackScheduler>();
        services.AddSingleton<IActorRuntimeCallbackScheduler>(sp =>
            sp.GetRequiredService<InMemoryActorRuntimeCallbackScheduler>());
        services.AddSingleton<IEventStore, InMemoryEventStore>();
        services.AddSingleton<EventSourcingRuntimeOptions>();
        services.AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>));
        services.AddSingleton<IEnumerable<IGAgentExecutionHook>>(Array.Empty<IGAgentExecutionHook>());
        return services.BuildServiceProvider();
    }

    private static StudioWorkspaceGAgent CreateAgent(string id, IServiceProvider services)
    {
        var agent = new StudioWorkspaceGAgent
        {
            Services = services,
            EventSourcingBehaviorFactory = services.GetRequiredService<IEventSourcingBehaviorFactory<StudioWorkspaceState>>(),
        };
        typeof(GAgentBase)
            .GetMethod("SetId", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(agent, [id]);
        return agent;
    }

    private static EventEnvelope Envelope<T>(T evt)
        where T : IMessage =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Now(),
            Payload = Any.Pack(evt),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication("test", TopologyAudience.Children),
        };

    private static Timestamp Now() => Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);
}

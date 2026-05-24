using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Demos.Inspector;
using Aevatar.Demos.Inspector.Demo;
using Aevatar.Demos.Inspector.ReadModels;
using Aevatar.Demos.Inspector.Telemetry;
using Aevatar.Foundation.Runtime.Observability;
using Aevatar.GAgents.Registry;
using Aevatar.Studio.Projection.ReadModels;
using Aevatar.Workflow.Projection.ReadModels;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aevatar.Demos.Inspector.Tests;

public sealed class InspectorEndpointsTests
{
    [Fact]
    public async Task ActorsEndpoint_ShouldReadTier1ReadModel_WhenTelemetryListenerIsDisposed()
    {
        await using var host = await InspectorTestHost.StartAsync();
        host.Services.GetRequiredService<InspectorTelemetryBroadcaster>().Dispose();
        await SeedRegistryAsync(host.Services);

        var response = await host.Client.GetFromJsonAsync<InspectorActorsResponse>("/api/inspector/actors");

        response.Should().NotBeNull();
        response!.ScopeId.Should().Be(InspectorGAgentRegistryService.ScopeId);
        response.StateVersion.Should().Be(9);
        response.Groups.Should().ContainSingle(group =>
            group.Type == "RoleGAgent" &&
            group.Count == 2);
        response.Groups.Single().ActorIds.Should().Equal("actor-a", "actor-b");
    }

    [Fact]
    public async Task ReadModelEndpoint_ShouldExposeUnpackedProtobufJson()
    {
        await using var host = await InspectorTestHost.StartAsync();
        await SeedRegistryAsync(host.Services);

        var json = await host.Client.GetStringAsync("/api/inspector/readmodels/gagent-registry");

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        root.GetProperty("name").GetString().Should().Be("gagent-registry");
        var serialized = root.GetProperty("documents")[0].GetRawText();
        serialized.Should().Contain("state_root");
        serialized.Should().Contain("RoleGAgent");
        serialized.Should().Contain("actor-a");
    }

    [Fact]
    public async Task ActorsEndpoint_ShouldReflectInspectorUnregisterCleanup()
    {
        await using var host = await InspectorTestHost.StartAsync();
        var registry = host.Services.GetRequiredService<InspectorGAgentRegistryService>();

        await registry.RegisterActorAsync(nameof(InspectorTransformerAgent), "inspector-parent", CancellationToken.None);
        await registry.RegisterActorAsync(nameof(InspectorTransformerAgent), "stale-parent", CancellationToken.None);
        await registry.UnregisterActorAsync(nameof(InspectorTransformerAgent), "stale-parent", CancellationToken.None);

        var response = await host.Client.GetFromJsonAsync<InspectorActorsResponse>("/api/inspector/actors");

        response.Should().NotBeNull();
        var group = response!.Groups.Should().ContainSingle().Subject;
        group.ActorIds.Should().Equal("inspector-parent");
        group.ActorIds.Should().NotContain("stale-parent");
    }

    [Fact]
    public async Task WorkflowRunsEndpoint_ShouldReadWorkflowCurrentStateReadModel()
    {
        await using var host = await InspectorTestHost.StartAsync();
        var writer = host.Services.GetRequiredService<IProjectionDocumentWriter<WorkflowExecutionCurrentStateDocument>>();
        await writer.UpsertAsync(new WorkflowExecutionCurrentStateDocument
        {
            Id = "workflow-run-1",
            RootActorId = "workflow-run-1",
            WorkflowName = "approval-flow",
            Status = "running",
            StateVersion = 12,
            LastEventId = "workflow-event-12",
            UpdatedAt = new DateTimeOffset(2026, 5, 12, 10, 0, 0, TimeSpan.Zero),
        });

        var json = await host.Client.GetStringAsync("/api/inspector/workflow-runs");

        json.Should().Contain("approval-flow");
        json.Should().Contain("Running");
        json.Should().Contain("workflow-run-1");
    }

    [Fact]
    public async Task EventsEndpoint_ShouldStreamLiveActivityFrames()
    {
        await using var host = await InspectorTestHost.StartAsync();
        _ = host.Services.GetRequiredService<InspectorTelemetryBroadcaster>();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/inspector/events");
        var responseTask = host.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

        using (AevatarActivitySource.StartAgentSpawn("sse-actor", "DemoAgent"))
        {
        }

        using var response = await responseTask.WaitAsync(TimeSpan.FromSeconds(3));
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);
        var eventLine = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3));
        var dataLine = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3));

        eventLine.Should().Be("event: activity");
        dataLine.Should().StartWith("data: ");
        dataLine.Should().Contain("aevatar.agent.spawn");
        dataLine.Should().Contain("sse-actor");
    }

    [Fact]
    public async Task TelemetryBroadcaster_ShouldDropOldestFrames_WhenChannelIsFull()
    {
        using var broadcaster = new InspectorTelemetryBroadcaster(capacity: 2);
        broadcaster.TryPublish(Frame("1")).Should().BeTrue();
        broadcaster.TryPublish(Frame("2")).Should().BeTrue();
        broadcaster.TryPublish(Frame("3")).Should().BeTrue();
        broadcaster.TryPublish(Frame("4")).Should().BeTrue();

        await using var frames = broadcaster.ReadAllAsync().GetAsyncEnumerator();
        (await frames.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3))).Should().BeTrue();
        frames.Current.Id.Should().Be("3");
        (await frames.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3))).Should().BeTrue();
        frames.Current.Id.Should().Be("4");
    }

    [Fact]
    public async Task DemoHierarchyEndpoint_ShouldReturnActorsAndLinks()
    {
        await using var host = await InspectorTestHost.StartAsync();

        var response = await host.Client.PostAsync("/api/inspector/demo/hierarchy", null);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();

        json.Should().Contain("inspector-parent");
        json.Should().Contain("inspector-child");
        json.Should().Contain("InspectorTransformerAgent");
        json.Should().Contain("InspectorCollectorAgent");
    }

    [Fact]
    public async Task DemoHierarchyEndpoint_ShouldEmitMessageActivity_WhenActorsAlreadyExist()
    {
        await using var host = await InspectorTestHost.StartAsync();
        var stopped = new ConcurrentQueue<Activity>();
        TaskCompletionSource? parentHandled = null;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AevatarActivitySource.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = static (ref ActivityCreationOptions<string> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                stopped.Enqueue(activity);
                if (IsActorHandleActivity(activity, "HandleEvent:InspectorPingEvent", "inspector-parent"))
                    parentHandled?.TrySetResult();
            },
        };
        ActivitySource.AddActivityListener(listener);

        var first = await host.Client.PostAsync("/api/inspector/demo/hierarchy", null);
        first.EnsureSuccessStatusCode();
        while (stopped.TryDequeue(out _))
        {
        }
        parentHandled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var second = await host.Client.PostAsync("/api/inspector/demo/hierarchy", null);
        second.EnsureSuccessStatusCode();

        await parentHandled.Task.WaitAsync(TimeSpan.FromSeconds(3));
    }

    private static async Task SeedRegistryAsync(IServiceProvider services)
    {
        var writer = services.GetRequiredService<IProjectionDocumentWriter<GAgentRegistryCurrentStateDocument>>();
        var state = new GAgentRegistryState();
        state.Groups.Add(new GAgentRegistryEntry
        {
            GagentType = "RoleGAgent",
            ActorIds = { "actor-a", "actor-b" },
        });

        await writer.UpsertAsync(new GAgentRegistryCurrentStateDocument
        {
            Id = InspectorGAgentRegistryService.RegistryActorId,
            ActorId = InspectorGAgentRegistryService.RegistryActorId,
            StateVersion = 9,
            LastEventId = "event-9",
            UpdatedAt = Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 5, 12, 9, 0, 0, TimeSpan.Zero)),
            StateRoot = Any.Pack(state),
        });
    }

    private static TelemetryFrame Frame(string id) =>
        new(
            id,
            $"trace-{id}",
            $"span-{id}",
            "aevatar.agent.spawn",
            DateTimeOffset.UtcNow,
            1,
            "Ok",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AevatarActivitySource.AgentIdTag] = id,
            });

    private static bool IsActorHandleActivity(Activity activity, string displayName, string actorId) =>
        activity.DisplayName == displayName &&
        string.Equals(
            activity.GetTagItem(AevatarActivitySource.AgentIdTag) as string,
            actorId,
            StringComparison.Ordinal);

    private sealed class InspectorTestHost : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private InspectorTestHost(WebApplication app, HttpClient client)
        {
            _app = app;
            Client = client;
            Services = app.Services;
        }

        public HttpClient Client { get; }

        public IServiceProvider Services { get; }

        public static async Task<InspectorTestHost> StartAsync()
        {
            var builder = InspectorApplication.CreateBuilder(["--no-browser"]);
            builder.WebHost.UseTestServer();
            var app = InspectorApplication.Build(builder);
            await app.StartAsync();
            return new InspectorTestHost(app, app.GetTestClient());
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.DisposeAsync();
        }
    }
}

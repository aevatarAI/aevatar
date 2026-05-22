using System.Net;
using System.Text;
using System.Text.Json;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Interop.A2A.Abstractions;
using Aevatar.Interop.A2A.Abstractions.Models;
using Aevatar.Interop.A2A.Application;
using Aevatar.Interop.A2A.Hosting;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Interop.A2A.Tests;

// Refactor (iter30/cluster-031-a2a-actor-owned):
//   Old pattern: endpoint tests used InMemoryA2ATaskStore ledger/subscriber registry.
//   New principle: endpoint tests use typed dispatch/readmodel/subscription ports.
public class A2AEndpointsTests : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private readonly TestServer _server;
    private readonly HttpClient _client;
    private readonly StubActorRuntime _runtime = new();
    private readonly StubDispatchPort _dispatchPort = new();
    private readonly StubProjectionReader _reader = new();
    private readonly StubTaskSubscriptionPort _subscriptionPort = new();

    public A2AEndpointsTests()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IActorRuntime>(_runtime);
        builder.Services.AddSingleton<IActorDispatchPort>(_dispatchPort);
        builder.Services.AddSingleton<IProjectionDocumentReader<A2ATaskCurrentStateReadModel, string>>(_reader);
        builder.Services.AddSingleton<IActorEventSubscriptionProvider>(_subscriptionPort);
        builder.Services.AddLogging();
        builder.Services.AddA2AAdapter();

        var app = builder.Build();
        app.MapA2AEndpoints();
        app.StartAsync().GetAwaiter().GetResult();

        _server = app.GetTestServer();
        _client = _server.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _server.Dispose();
    }

    [Fact]
    public async Task AgentCard_ReturnsValidJson()
    {
        var response = await _client.GetAsync("/.well-known/agent.json");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        var card = JsonSerializer.Deserialize<AgentCard>(body, JsonOptions);
        card.Should().NotBeNull();
        card!.Url.Should().Contain("/a2a");
        card.Skills.Should().NotBeEmpty();
    }

    [Fact]
    public async Task TasksSend_ValidRequest_ReturnsSubmittedTask()
    {
        var response = await PostJsonRpcAsync(new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tasks/send",
            @params = new
            {
                id = "t-1",
                message = new { role = "user", parts = new[] { new { type = "text", text = "hello" } } },
                metadata = new Dictionary<string, string> { ["agentId"] = "actor-1" },
            },
        });

        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().Contain("\"result\"");
        body.Should().Contain("submitted");
        body.Should().NotContain("\"error\"");
        _dispatchPort.LastEnvelope!.Payload.Unpack<A2ATaskSubmitCommand>().TaskId.Should().Be("t-1");
    }

    [Fact]
    public async Task TasksSend_MissingAgentId_ReturnsInvalidParams()
    {
        var response = await PostJsonRpcAsync(new
        {
            jsonrpc = "2.0",
            id = 2,
            method = "tasks/send",
            @params = new
            {
                id = "t-2",
                message = new { role = "user", parts = new[] { new { type = "text", text = "hello" } } },
            },
        });

        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().Contain("\"error\"");
        body.Should().Contain("-32602");
    }

    [Fact]
    public async Task TasksGet_ExistingTask_ReturnsTask()
    {
        _reader.Documents[A2ATaskActorId.Build("t-get")] = MakeDocument("t-get", TaskState.Working);

        var response = await PostJsonRpcAsync(new { jsonrpc = "2.0", id = 3, method = "tasks/get", @params = new { id = "t-get" } });
        var body = await response.Content.ReadAsStringAsync();

        body.Should().Contain("\"result\"");
        body.Should().Contain("t-get");
        body.Should().Contain("working");
    }

    [Fact]
    public async Task TasksGet_NonExistent_ReturnsTaskNotFound()
    {
        var response = await PostJsonRpcAsync(new { jsonrpc = "2.0", id = 4, method = "tasks/get", @params = new { id = "missing" } });
        var body = await response.Content.ReadAsStringAsync();

        body.Should().Contain("\"error\"");
        body.Should().Contain("-32001");
    }

    [Fact]
    public async Task TasksCancel_WorkingTask_ReturnsSubmittedReceipt()
    {
        _reader.Documents[A2ATaskActorId.Build("t-cancel")] = MakeDocument("t-cancel", TaskState.Working);

        var response = await PostJsonRpcAsync(new { jsonrpc = "2.0", id = 5, method = "tasks/cancel", @params = new { id = "t-cancel" } });
        var body = await response.Content.ReadAsStringAsync();

        body.Should().Contain("\"result\"");
        body.Should().Contain("submitted");
        _dispatchPort.LastEnvelope!.Payload.Unpack<A2ATaskCancelCommand>().TaskId.Should().Be("t-cancel");
    }

    [Fact]
    public async Task TasksCancel_CompletedTask_ReturnsNotCancelable()
    {
        _reader.Documents[A2ATaskActorId.Build("t-done")] = MakeDocument("t-done", TaskState.Completed);

        var response = await PostJsonRpcAsync(new { jsonrpc = "2.0", id = 6, method = "tasks/cancel", @params = new { id = "t-done" } });
        var body = await response.Content.ReadAsStringAsync();

        body.Should().Contain("\"error\"");
        body.Should().Contain("-32002");
    }

    [Fact]
    public async Task TasksCancel_NonExistent_ReturnsTaskNotFound()
    {
        var response = await PostJsonRpcAsync(new { jsonrpc = "2.0", id = 7, method = "tasks/cancel", @params = new { id = "nope" } });
        var body = await response.Content.ReadAsStringAsync();

        body.Should().Contain("\"error\"");
        body.Should().Contain("-32001");
    }

    [Fact]
    public async Task UnknownMethod_ReturnsMethodNotFound()
    {
        var response = await PostJsonRpcAsync(new { jsonrpc = "2.0", id = 8, method = "tasks/unknown", @params = new { id = "x" } });
        var body = await response.Content.ReadAsStringAsync();

        body.Should().Contain("\"error\"");
        body.Should().Contain("-32601");
    }

    [Fact]
    public async Task MalformedJson_ReturnsParseError()
    {
        var content = new StringContent("{not valid json}", Encoding.UTF8, "application/json");
        var response = await _client.PostAsync("/a2a", content);
        var body = await response.Content.ReadAsStringAsync();

        body.Should().Contain("\"error\"");
        body.Should().Contain("-32700");
    }

    [Fact]
    public async Task EmptyMethod_ReturnsInvalidRequest()
    {
        var response = await PostJsonRpcAsync(new { jsonrpc = "2.0", id = 9, method = "" });
        var body = await response.Content.ReadAsStringAsync();

        body.Should().Contain("\"error\"");
        body.Should().Contain("-32600");
    }

    [Fact]
    public async Task MissingParams_ReturnsInvalidParams()
    {
        var response = await PostJsonRpcAsync(new { jsonrpc = "2.0", id = 10, method = "tasks/get" });
        var body = await response.Content.ReadAsStringAsync();

        body.Should().Contain("\"error\"");
        body.Should().Contain("-32602");
    }

    [Fact]
    public async Task TasksSend_DispatchFails_ReturnsErrorInsteadOfSyntheticFailedState()
    {
        _dispatchPort.ShouldThrow = true;

        var response = await PostJsonRpcAsync(new
        {
            jsonrpc = "2.0",
            id = 11,
            method = "tasks/send",
            @params = new
            {
                id = "t-err",
                message = new { role = "user", parts = new[] { new { type = "text", text = "hello" } } },
                metadata = new Dictionary<string, string> { ["agentId"] = "actor-1" },
            },
        });
        var body = await response.Content.ReadAsStringAsync();

        body.Should().Contain("\"error\"");
        body.Should().Contain("-32603");
        body.Should().NotContain("\"state\":\"failed\"");
    }

    [Fact]
    public async Task Subscribe_NonExistentTask_Returns404()
    {
        var response = await _client.GetAsync("/a2a/subscribe/nonexistent");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Subscribe_CompletedTask_ReturnsStatusAndCloses()
    {
        _reader.Documents[A2ATaskActorId.Build("t-sse")] = MakeDocument("t-sse", TaskState.Completed);

        var response = await _client.GetAsync("/a2a/subscribe/t-sse");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("event: status");
        body.Should().Contain("event: close");
        body.Should().Contain("terminal_state");
    }

    [Fact]
    public async Task Subscribe_WorkingTask_UsesTypedUpdateSubscription()
    {
        _reader.Documents[A2ATaskActorId.Build("t-stream")] = MakeDocument("t-stream", TaskState.Working);

        using var cts = new CancellationTokenSource();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/a2a/subscribe/t-stream");
        var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(stream);

        var initialEvent = await ReadSseEventAsync(reader);
        initialEvent.Should().Contain("event: status");
        initialEvent.Should().Contain("working");
        _subscriptionPort.LastActorId.Should().Be(A2ATaskActorId.Build("t-stream"));

        await _subscriptionPort.EmitAsync(new A2ATaskUpdate
        {
            TaskId = "t-stream",
            ActorId = A2ATaskActorId.Build("t-stream"),
            Status = A2ATaskModelMapper.BuildStatus(
                A2ATaskLifecycleState.Canceled,
                Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(DateTime.UtcNow)),
            IsFinal = true,
        });
        var finalEvent = await ReadSseEventAsync(reader);
        finalEvent.Should().Contain("canceled");

        cts.Cancel();
    }

    [Fact]
    public void AddA2AAdapter_RegistersAdapterAndPortsWithoutTaskStore()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IActorRuntime>(new StubActorRuntime());
        services.AddSingleton<IActorDispatchPort>(new StubDispatchPort());
        services.AddSingleton<IProjectionDocumentReader<A2ATaskCurrentStateReadModel, string>>(new StubProjectionReader());
        services.AddSingleton<IActorEventSubscriptionProvider>(new StubTaskSubscriptionPort());
        services.AddLogging();
        services.AddA2AAdapter();

        var serviceTypes = services.Select(descriptor => descriptor.ServiceType).ToArray();
        serviceTypes.Should().NotContain(type => type.Name.Contains("IA2ATaskStore", StringComparison.Ordinal));

        using var provider = services.BuildServiceProvider();
        provider.GetService<IA2AAdapterService>().Should().NotBeNull();
    }

    [Fact]
    public void ProcessLocalA2ATaskStoreTypes_AreRemovedFromProductionSources()
    {
        var root = FindRepositoryRoot();
        var references = Directory
            .EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(path))
            .Where(path =>
            {
                var source = File.ReadAllText(path);
                return source.Contains("interface " + "IA2ATaskStore", StringComparison.Ordinal) ||
                       source.Contains("class " + "InMemoryA2ATaskStore", StringComparison.Ordinal);
            })
            .Select(path => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'))
            .Order()
            .ToArray();

        references.Should().BeEmpty();
    }

    private async Task<HttpResponseMessage> PostJsonRpcAsync(object request)
    {
        var json = JsonSerializer.Serialize(request, JsonOptions);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        return await _client.PostAsync("/a2a", content);
    }

    private static async Task<string> ReadSseEventAsync(StreamReader reader)
    {
        var lines = new List<string>();
        while (true)
        {
            var line = await reader.ReadLineAsync();
            line.Should().NotBeNull("the SSE stream should emit a complete event");
            if (string.IsNullOrEmpty(line))
                break;

            lines.Add(line);
        }

        return string.Join(Environment.NewLine, lines) + Environment.NewLine + Environment.NewLine;
    }

    private static A2ATaskCurrentStateReadModel MakeDocument(string taskId, TaskState taskState)
    {
        var now = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(DateTime.UtcNow);
        var actorId = A2ATaskActorId.Build(taskId);
        var lifecycleState = taskState switch
        {
            TaskState.Submitted => A2ATaskLifecycleState.Submitted,
            TaskState.Working => A2ATaskLifecycleState.Working,
            TaskState.InputRequired => A2ATaskLifecycleState.InputRequired,
            TaskState.Completed => A2ATaskLifecycleState.Completed,
            TaskState.Canceled => A2ATaskLifecycleState.Canceled,
            TaskState.Failed => A2ATaskLifecycleState.Failed,
            _ => A2ATaskLifecycleState.Unknown,
        };
        var state = new A2ATaskState
        {
            TaskId = taskId,
            Status = A2ATaskModelMapper.BuildStatus(lifecycleState, now),
            StateVersion = 1,
            LastEventId = "evt-1",
            UpdatedAt = now,
        };
        state.History.Add(A2ATaskModelMapper.ToProto(new Message
        {
            Role = "user",
            Parts = [new TextPart { Text = "hi" }],
        }));

        return new A2ATaskCurrentStateReadModel
        {
            Id = actorId,
            ActorId = actorId,
            StateVersion = 1,
            LastEventId = "evt-1",
            UpdatedAtUtcValue = now,
            State = state,
        };
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var marker = Path.Combine(
                directory.FullName,
                "src",
                "Aevatar.Interop.A2A.Hosting",
                "A2AServiceCollectionExtensions.cs");
            if (File.Exists(marker))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    private static bool IsBuildOutput(string path)
    {
        var normalized = path.Replace(Path.DirectorySeparatorChar, '/');
        return normalized.Contains("/bin/", StringComparison.Ordinal) ||
               normalized.Contains("/obj/", StringComparison.Ordinal);
    }

    private sealed class StubActorRuntime : IActorRuntime
    {
        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default) where TAgent : IAgent =>
            CreateAsync(typeof(TAgent), id, ct);

        public Task<IActor> CreateAsync(Type agentType, string? id = null, CancellationToken ct = default) =>
            Task.FromResult<IActor>(new StubActor(id ?? agentType.Name));

        public Task DestroyAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IActor?> GetAsync(string id) => Task.FromResult<IActor?>(null);
        public Task<bool> ExistsAsync(string id) => Task.FromResult(false);
        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) => Task.CompletedTask;
        public Task UnlinkAsync(string childId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class StubDispatchPort : IActorDispatchPort
    {
        public EventEnvelope? LastEnvelope { get; private set; }
        public bool ShouldThrow { get; set; }

        public Task DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            if (ShouldThrow) throw new ApplicationException("Dispatch failed");
            LastEnvelope = envelope;
            return Task.CompletedTask;
        }
    }

    private sealed class StubProjectionReader : IProjectionDocumentReader<A2ATaskCurrentStateReadModel, string>
    {
        public Dictionary<string, A2ATaskCurrentStateReadModel> Documents { get; } = [];

        public Task<A2ATaskCurrentStateReadModel?> GetAsync(string key, CancellationToken ct = default)
        {
            Documents.TryGetValue(key, out var document);
            return Task.FromResult(document);
        }

        public Task<ProjectionDocumentQueryResult<A2ATaskCurrentStateReadModel>> QueryAsync(
            ProjectionDocumentQuery query,
            CancellationToken ct = default) =>
            Task.FromResult(ProjectionDocumentQueryResult<A2ATaskCurrentStateReadModel>.Empty);
    }

    private sealed class StubTaskSubscriptionPort : IActorEventSubscriptionProvider
    {
        private Func<A2ATaskUpdate, Task>? _handler;
        public string? LastActorId { get; private set; }

        public Task<IAsyncDisposable> SubscribeAsync<TMessage>(
            string actorId,
            Func<TMessage, Task> handler,
            CancellationToken ct = default)
            where TMessage : class, Google.Protobuf.IMessage, new()
        {
            LastActorId = actorId;
            _handler = update => handler((TMessage)(object)update);
            return Task.FromResult<IAsyncDisposable>(new NoopAsyncDisposable());
        }

        public Task EmitAsync(A2ATaskUpdate update) =>
            _handler?.Invoke(update) ?? Task.CompletedTask;
    }

    private sealed class StubActor(string id) : IActor
    {
        public string Id { get; } = id;
        public IAgent Agent { get; } = new StubAgent();
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class StubAgent : IAgent
    {
        public string Id => "stub-agent";
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> GetDescriptionAsync() => Task.FromResult("stub");
        public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<Type>>([]);
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class NoopAsyncDisposable : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

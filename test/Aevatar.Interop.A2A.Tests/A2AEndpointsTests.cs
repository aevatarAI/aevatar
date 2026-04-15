using System.Net;
using System.Text;
using System.Text.Json;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.MultiAgent;
using Aevatar.Interop.A2A.Abstractions;
using Aevatar.Interop.A2A.Abstractions.Models;
using Aevatar.Interop.A2A.Application;
using Aevatar.Interop.A2A.Hosting;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Interop.A2A.Tests;

public class A2AEndpointsTests : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private readonly TestServer _server;
    private readonly HttpClient _client;
    private readonly StubDispatchPort _dispatchPort = new();
    private readonly InMemoryA2ATaskStore _taskStore = new();

    public A2AEndpointsTests()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IActorDispatchPort>(_dispatchPort);
        builder.Services.AddSingleton<IA2ATaskStore>(_taskStore);
        builder.Services.AddScoped<IA2AAdapterService, A2AAdapterService>();

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
            {
                break;
            }

            lines.Add(line);
        }

        return string.Join(Environment.NewLine, lines) + Environment.NewLine + Environment.NewLine;
    }

    // ─── Agent Card ───

    [Fact]
    public async Task AgentCard_ReturnsValidJson()
    {
        var response = await _client.GetAsync("/.well-known/agent.json");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        var card = JsonSerializer.Deserialize<AgentCard>(body, JsonOptions);
        card.Should().NotBeNull();
        card!.Name.Should().NotBeNullOrWhiteSpace();
        card.Url.Should().Contain("/a2a");
        card.Skills.Should().NotBeEmpty();
    }

    // ─── tasks/send ───

    [Fact]
    public async Task TasksSend_ValidRequest_ReturnsWorkingTask()
    {
        var rpc = new
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
        };

        var response = await PostJsonRpcAsync(rpc);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"result\"");
        body.Should().Contain("working");
        body.Should().NotContain("\"error\"");
    }

    [Fact]
    public async Task TasksSend_MissingAgentId_ReturnsInvalidParams()
    {
        var rpc = new
        {
            jsonrpc = "2.0",
            id = 2,
            method = "tasks/send",
            @params = new
            {
                id = "t-2",
                message = new { role = "user", parts = new[] { new { type = "text", text = "hello" } } },
            },
        };

        var response = await PostJsonRpcAsync(rpc);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"error\"");
        body.Should().Contain("-32602");
    }

    // ─── tasks/get ───

    [Fact]
    public async Task TasksGet_ExistingTask_ReturnsTask()
    {
        await _taskStore.CreateTaskAsync("t-get", null,
            new Message { Role = "user", Parts = [new TextPart { Text = "hi" }] });

        var rpc = new { jsonrpc = "2.0", id = 3, method = "tasks/get", @params = new { id = "t-get" } };
        var response = await PostJsonRpcAsync(rpc);
        var body = await response.Content.ReadAsStringAsync();

        body.Should().Contain("\"result\"");
        body.Should().Contain("t-get");
    }

    [Fact]
    public async Task TasksGet_NonExistent_ReturnsTaskNotFound()
    {
        var rpc = new { jsonrpc = "2.0", id = 4, method = "tasks/get", @params = new { id = "missing" } };
        var response = await PostJsonRpcAsync(rpc);
        var body = await response.Content.ReadAsStringAsync();

        body.Should().Contain("\"error\"");
        body.Should().Contain("-32001");
    }

    // ─── tasks/cancel ───

    [Fact]
    public async Task TasksCancel_WorkingTask_ReturnsCanceled()
    {
        await _taskStore.CreateTaskAsync("t-cancel", null,
            new Message { Role = "user", Parts = [new TextPart { Text = "hi" }] });
        await _taskStore.UpdateTaskStateAsync("t-cancel", TaskState.Working);

        var rpc = new { jsonrpc = "2.0", id = 5, method = "tasks/cancel", @params = new { id = "t-cancel" } };
        var response = await PostJsonRpcAsync(rpc);
        var body = await response.Content.ReadAsStringAsync();

        body.Should().Contain("\"result\"");
        body.Should().Contain("canceled");
    }

    [Fact]
    public async Task TasksCancel_CompletedTask_ReturnsNotCancelable()
    {
        await _taskStore.CreateTaskAsync("t-done", null,
            new Message { Role = "user", Parts = [new TextPart { Text = "hi" }] });
        await _taskStore.UpdateTaskStateAsync("t-done", TaskState.Completed);

        var rpc = new { jsonrpc = "2.0", id = 6, method = "tasks/cancel", @params = new { id = "t-done" } };
        var response = await PostJsonRpcAsync(rpc);
        var body = await response.Content.ReadAsStringAsync();

        body.Should().Contain("\"error\"");
        body.Should().Contain("-32002");
    }

    [Fact]
    public async Task TasksCancel_NonExistent_ReturnsTaskNotFound()
    {
        var rpc = new { jsonrpc = "2.0", id = 7, method = "tasks/cancel", @params = new { id = "nope" } };
        var response = await PostJsonRpcAsync(rpc);
        var body = await response.Content.ReadAsStringAsync();

        body.Should().Contain("\"error\"");
        body.Should().Contain("-32001");
    }

    // ─── Error handling ───

    [Fact]
    public async Task UnknownMethod_ReturnsMethodNotFound()
    {
        var rpc = new { jsonrpc = "2.0", id = 8, method = "tasks/unknown", @params = new { id = "x" } };
        var response = await PostJsonRpcAsync(rpc);
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
        var rpc = new { jsonrpc = "2.0", id = 9, method = "" };
        var response = await PostJsonRpcAsync(rpc);
        var body = await response.Content.ReadAsStringAsync();

        body.Should().Contain("\"error\"");
        body.Should().Contain("-32600");
    }

    [Fact]
    public async Task MissingParams_ReturnsInvalidParams()
    {
        var rpc = new { jsonrpc = "2.0", id = 10, method = "tasks/get" };
        var response = await PostJsonRpcAsync(rpc);
        var body = await response.Content.ReadAsStringAsync();

        body.Should().Contain("\"error\"");
        body.Should().Contain("-32602");
    }

    [Fact]
    public async Task TasksSend_DispatchFails_ReturnsResultWithFailedState()
    {
        _dispatchPort.ShouldThrow = true;

        var rpc = new
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
        };

        var response = await PostJsonRpcAsync(rpc);
        var body = await response.Content.ReadAsStringAsync();

        body.Should().Contain("\"result\"");
        body.Should().Contain("failed");
    }

    // ─── SSE subscribe ───

    [Fact]
    public async Task Subscribe_NonExistentTask_Returns404()
    {
        var response = await _client.GetAsync("/a2a/subscribe/nonexistent");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Subscribe_CompletedTask_ReturnsStatusAndCloses()
    {
        await _taskStore.CreateTaskAsync("t-sse", null,
            new Message { Role = "user", Parts = [new TextPart { Text = "hi" }] });
        await _taskStore.UpdateTaskStateAsync("t-sse", TaskState.Completed);

        var response = await _client.GetAsync("/a2a/subscribe/t-sse");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("event: status");
        body.Should().Contain("event: close");
        body.Should().Contain("terminal_state");
    }

    [Fact]
    public async Task Subscribe_WorkingTask_StreamsUpdates()
    {
        await _taskStore.CreateTaskAsync("t-stream", null,
            new Message { Role = "user", Parts = [new TextPart { Text = "hi" }] });
        await _taskStore.UpdateTaskStateAsync("t-stream", TaskState.Working);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/a2a/subscribe/t-stream");
        var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);

        var initialEvent = await ReadSseEventAsync(reader);
        initialEvent.Should().Contain("event: status");
        initialEvent.Should().Contain("working");

        await _taskStore.UpdateTaskStateAsync("t-stream", TaskState.Completed);

        var body = initialEvent + await reader.ReadToEndAsync();

        body.Should().Contain("event: status");
        body.Should().Contain("event: close");
    }

    // ─── DI extension ───

    [Fact]
    public void AddA2AAdapter_RegistersServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IActorDispatchPort>(new StubDispatchPort());
        services.AddLogging();
        services.AddA2AAdapter();

        var provider = services.BuildServiceProvider();

        provider.GetService<IA2ATaskStore>().Should().NotBeNull();
        provider.GetService<IA2AAdapterService>().Should().NotBeNull();
    }

    [Fact]
    public void AddA2AAdapter_DoesNotOverrideExistingRegistrations()
    {
        var customStore = new InMemoryA2ATaskStore();
        var services = new ServiceCollection();
        services.AddSingleton<IActorDispatchPort>(new StubDispatchPort());
        services.AddSingleton<IA2ATaskStore>(customStore);
        services.AddLogging();
        services.AddA2AAdapter();

        var provider = services.BuildServiceProvider();
        provider.GetService<IA2ATaskStore>().Should().BeSameAs(customStore);
    }

    // ─── Stub ───

    private sealed class StubDispatchPort : IActorDispatchPort
    {
        public bool ShouldThrow { get; set; }

        public Task DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            if (ShouldThrow) throw new InvalidOperationException("Dispatch failed");
            return Task.CompletedTask;
        }
    }
}

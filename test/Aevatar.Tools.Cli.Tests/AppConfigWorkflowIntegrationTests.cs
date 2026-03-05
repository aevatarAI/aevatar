using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.CommandLine;
using Aevatar.Tools.Cli.Commands;
using Aevatar.Tools.Cli.Hosting;
using Aevatar.Workflow.Sdk;
using Aevatar.Workflow.Sdk.Contracts;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Tools.Cli.Tests;

public class AppConfigWorkflowIntegrationTests
{
    [Fact]
    public async Task ConfigUiEnsureCommand_WhenConfigUiIsHealthy_ShouldReturnUrlInJson()
    {
        var port = AllocateTcpPort();
        await using var app = await StartConfigHealthServerAsync(port);

        var root = new RootCommand();
        root.AddCommand(ConfigCommand.Create());
        var output = await CaptureStdOutAsync(() => root.InvokeAsync(
            [
                "config",
                "ui",
                "ensure",
                "--port",
                port.ToString(),
                "--json",
            ]));

        output.ExitCode.Should().Be(0);
        using var json = JsonDocument.Parse(output.StdOut);
        json.RootElement.GetProperty("ok").GetBoolean().Should().BeTrue();
        json.RootElement.GetProperty("data").GetProperty("url").GetString().Should().Be($"http://localhost:{port}");
        json.RootElement.GetProperty("data").GetProperty("started").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task HandleOpenConfigAsync_WhenEmbeddedModeAndWorkflowReturnsUrl_ShouldReturnJumpUrl()
    {
        var terminalResult = JsonDocument.Parse(
            """
            {
              "output": "{\"ok\":true,\"code\":\"OK\",\"data\":{\"url\":\"http://localhost:6677\",\"started\":true}}"
            }
            """).RootElement.Clone();
        var runResult = new WorkflowRunResult(
            [
                WorkflowEvent.FromFrame(new WorkflowOutputFrame
                {
                    Type = WorkflowEventTypes.RunFinished,
                    Result = terminalResult,
                }),
            ]);
        var client = new StubWorkflowClient(runResult);

        var result = await AppDemoPlaygroundEndpoints.HandleOpenConfigAsync(
            new AppDemoPlaygroundEndpoints.AppConfigOpenRequest(6677),
            client,
            embeddedWorkflowMode: true,
            CancellationToken.None);

        var payload = ExtractResultPayload(result);
        payload.StatusCode.Should().Be(StatusCodes.Status200OK);
        payload.Json.RootElement.GetProperty("ok").GetBoolean().Should().BeTrue();
        payload.Json.RootElement.GetProperty("configUrl").GetString().Should().Be("http://localhost:6677");
        payload.Json.RootElement.GetProperty("started").GetBoolean().Should().BeTrue();
        client.LastRunRequest.Should().NotBeNull();
        client.LastRunRequest!.WorkflowYamls.Should().ContainSingle();
        client.LastRunRequest.WorkflowYamls![0].Should().Contain("config ui ensure --no-browser --port 6677 --json");
        client.LastRunRequest.WorkflowYamls![0].Should().NotContain("__CONFIG_UI_PORT__");
    }

    [Fact]
    public async Task HandleOpenConfigAsync_WhenProxyMode_ShouldReject()
    {
        var client = new StubWorkflowClient(new WorkflowRunResult([]));
        var result = await AppDemoPlaygroundEndpoints.HandleOpenConfigAsync(
            new AppDemoPlaygroundEndpoints.AppConfigOpenRequest(null),
            client,
            embeddedWorkflowMode: false,
            CancellationToken.None);

        var payload = ExtractResultPayload(result);
        payload.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        payload.Json.RootElement.GetProperty("code").GetString().Should().Be("APP_CONFIG_OPEN_UNSUPPORTED_MODE");
    }

    private static async Task<(int ExitCode, string StdOut)> CaptureStdOutAsync(Func<Task<int>> action)
    {
        var originalOut = Console.Out;
        var writer = new StringWriter();
        Console.SetOut(writer);
        try
        {
            var exitCode = await action();
            return (exitCode, writer.ToString().Trim());
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    private static (int StatusCode, JsonDocument Json) ExtractResultPayload(IResult result)
    {
        var resultType = result.GetType();
        var statusCode = resultType.GetProperty("StatusCode")?.GetValue(result) as int? ?? StatusCodes.Status200OK;
        var value = resultType.GetProperty("Value")?.GetValue(result);
        var json = JsonSerializer.Serialize(value);
        return (statusCode, JsonDocument.Parse(json));
    }

    private static int AllocateTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task<WebApplication> StartConfigHealthServerAsync(int port)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://localhost:{port}");
        var app = builder.Build();
        app.MapGet("/api/health", () => Results.Text("ok"));
        await app.StartAsync();
        return app;
    }

    private sealed class StubWorkflowClient(WorkflowRunResult result) : IAevatarWorkflowClient
    {
        public ChatRunRequest? LastRunRequest { get; private set; }

        public IAsyncEnumerable<WorkflowEvent> StartRunStreamAsync(ChatRunRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<WorkflowRunResult> RunToCompletionAsync(ChatRunRequest request, CancellationToken cancellationToken = default)
        {
            LastRunRequest = request;
            return Task.FromResult(result);
        }

        public Task<WorkflowResumeResponse> ResumeAsync(WorkflowResumeRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<WorkflowSignalResponse> SignalAsync(WorkflowSignalRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<JsonElement>> GetWorkflowCatalogAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<JsonElement?> GetCapabilitiesAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<JsonElement?> GetWorkflowDetailAsync(string workflowName, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<JsonElement?> GetActorSnapshotAsync(string actorId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<JsonElement>> GetActorTimelineAsync(string actorId, int take = 200, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Aevatar.Hosting;
using Aevatar.Studio.Application.Scripts.Contracts;
using Aevatar.Studio.Application.Studio.Authoring;
using Aevatar.Studio.Hosting.Endpoints;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aevatar.Tools.Cli.Tests;

public class AppStudioEndpointsTests
{
    [Fact]
    public void NormalizeStudioDocumentId_ShouldSlugifyReadableNames()
    {
        var result = StudioEndpoints.NormalizeStudioDocumentId(
            " Customer Support Workflow 2026 ",
            "workflow");

        result.Should().Be("customer-support-workflow-2026");
    }

    [Fact]
    public void NormalizeStudioDocumentId_WhenInputIsBlank_ShouldUseFallbackPrefix()
    {
        var result = StudioEndpoints.NormalizeStudioDocumentId(
            "   ",
            "script");

        result.Should().StartWith("script-");
    }

    [Fact]
    public void AppScriptProtocol_ShouldRoundTripStringsAndLists()
    {
        var state = AppScriptProtocol.CreateState(
            input: "hello",
            output: "HELLO",
            status: "ok",
            lastCommandId: "command-1",
            notes: ["trimmed", "uppercased"]);

        AppScriptProtocol.GetString(state, AppScriptProtocol.InputField).Should().Be("hello");
        AppScriptProtocol.GetString(state, AppScriptProtocol.OutputField).Should().Be("HELLO");
        AppScriptProtocol.GetString(state, AppScriptProtocol.StatusField).Should().Be("ok");
        AppScriptProtocol.GetString(state, AppScriptProtocol.LastCommandIdField).Should().Be("command-1");
        AppScriptProtocol.GetStringList(state, AppScriptProtocol.NotesField).Should().Equal("trimmed", "uppercased");
    }

    [Fact]
    public async Task WorkflowGeneratorEndpoint_WhenEmbedded_ShouldReturnSsePreviewFrames()
    {
        var previewService = new FakeAuthoringPreviewService(
            new StudioAuthoringPreviewEvent.ReasoningDelta("thinking"),
            new StudioAuthoringPreviewEvent.Progress(StudioAuthoringProgressStage.GeneratingDraft, 1, "validating"),
            new StudioAuthoringPreviewEvent.ContentDelta("name: demo"),
            new StudioAuthoringPreviewEvent.WorkflowCompleted(new WorkflowGenerateResult(
                "name: demo\nsteps: []",
                1,
                [])));
        await using var host = await StudioGeneratorTestHost.StartAsync(
            embeddedWorkflowMode: true,
            previewService);

        var response = await host.Client.PostAsJsonAsync("/api/app/workflow-generator", new
        {
            prompt = "Create workflow",
            availableWorkflowNames = Array.Empty<string>(),
        });
        var frames = await ReadSseFramesAsync(response);
        var responseBody = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, responseBody);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/event-stream");
        frames.Select(GetFrameType).Should().ContainInOrder(
            "TEXT_MESSAGE_REASONING",
            "TEXT_MESSAGE_REASONING",
            "TEXT_MESSAGE_CONTENT",
            "TEXT_MESSAGE_END");
        frames.Last().RootElement.GetProperty("message").GetString().Should().Contain("name: demo");
        previewService.Requests.Single().Kind.Should().Be(StudioAuthoringKind.Workflow);
    }

    [Fact]
    public async Task ScriptGeneratorEndpoint_WhenEmbedded_ShouldReturnPackageFieldsInCompletionFrame()
    {
        var package = new AppScriptPackage(
            [new AppScriptPackageFile("Behavior.cs", "public sealed class Behavior {}")],
            [new AppScriptPackageFile("behavior.proto", "syntax = \"proto3\";")],
            "Behavior",
            "Behavior.cs");
        var previewService = new FakeAuthoringPreviewService(
            new StudioAuthoringPreviewEvent.ScriptCompleted(new ScriptGenerateResult(
                "public sealed class Behavior {}",
                1,
                [],
                package,
                "Behavior.cs")));
        await using var host = await StudioGeneratorTestHost.StartAsync(
            embeddedWorkflowMode: true,
            previewService);

        var response = await host.Client.PostAsJsonAsync("/api/scripts/generator", new
        {
            prompt = "Create script",
        });
        var frames = await ReadSseFramesAsync(response);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var completion = frames.Single();
        completion.RootElement.GetProperty("type").GetString().Should().Be("TEXT_MESSAGE_END");
        completion.RootElement.GetProperty("currentFilePath").GetString().Should().Be("Behavior.cs");
        var scriptPackage = completion.RootElement.GetProperty("scriptPackage");
        scriptPackage.GetProperty("entryBehaviorTypeName").GetString().Should().Be("Behavior");
        scriptPackage.GetProperty("csharpSources")[0].GetProperty("path").GetString().Should().Be("Behavior.cs");
        previewService.Requests.Single().Kind.Should().Be(StudioAuthoringKind.Script);
    }

    [Theory]
    [InlineData(false, "Create workflow", "WORKFLOW_GENERATOR_UNAVAILABLE")]
    [InlineData(true, "   ", "WORKFLOW_GENERATOR_PROMPT_REQUIRED")]
    public async Task WorkflowGeneratorEndpoint_WhenUnavailableOrBlankPrompt_ShouldReturnDocumentedError(
        bool embeddedWorkflowMode,
        string prompt,
        string expectedCode)
    {
        await using var host = await StudioGeneratorTestHost.StartAsync(
            embeddedWorkflowMode,
            new FakeAuthoringPreviewService());

        var response = await host.Client.PostAsJsonAsync("/api/workflows/generator", new
        {
            prompt,
        });
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        payload.GetProperty("code").GetString().Should().Be(expectedCode);
    }

    [Theory]
    [InlineData(false, "Create script", "SCRIPT_GENERATOR_UNAVAILABLE")]
    [InlineData(true, "   ", "SCRIPT_GENERATOR_PROMPT_REQUIRED")]
    public async Task ScriptGeneratorEndpoint_WhenUnavailableOrBlankPrompt_ShouldReturnDocumentedError(
        bool embeddedWorkflowMode,
        string prompt,
        string expectedCode)
    {
        await using var host = await StudioGeneratorTestHost.StartAsync(
            embeddedWorkflowMode,
            new FakeAuthoringPreviewService());

        var response = await host.Client.PostAsJsonAsync("/api/app/scripts/generator", new
        {
            prompt,
        });
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        payload.GetProperty("code").GetString().Should().Be(expectedCode);
    }

    [Theory]
    [InlineData("/api/app/workflow-generator", "WORKFLOW_GENERATOR_MISSING")]
    [InlineData("/api/app/scripts/generator", "SCRIPT_GENERATOR_MISSING")]
    public async Task GeneratorEndpoints_WhenPreviewServiceMissing_ShouldReturnDocumentedError(
        string path,
        string expectedCode)
    {
        await using var host = await StudioGeneratorTestHost.StartAsync(
            embeddedWorkflowMode: true,
            previewService: null);

        var response = await host.Client.PostAsJsonAsync(path, new
        {
            prompt = "Create preview",
        });
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        payload.GetProperty("code").GetString().Should().Be(expectedCode);
    }

    private static async Task<JsonDocument[]> ReadSseFramesAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        return body
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(frame => frame.Trim())
            .Where(frame => frame.StartsWith("data: ", StringComparison.Ordinal))
            .Select(frame => JsonDocument.Parse(frame["data: ".Length..]))
            .ToArray();
    }

    private static string? GetFrameType(JsonDocument document) =>
        document.RootElement.GetProperty("type").GetString();

    private sealed class StudioGeneratorTestHost : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private StudioGeneratorTestHost(WebApplication app, HttpClient client)
        {
            _app = app;
            Client = client;
        }

        public HttpClient Client { get; }

        public static async Task<StudioGeneratorTestHost> StartAsync(
            bool embeddedWorkflowMode,
            IStudioAuthoringPreviewApplicationService? previewService)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = Environments.Development,
            });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            if (previewService != null)
                builder.Services.AddSingleton(previewService);
            builder.Services.AddSingleton(new AevatarHostMetadata
            {
                ServiceName = "test-studio-generator",
            });
            builder.Services.AddSingleton<AevatarHostHealthService>();

            var app = builder.Build();
            StudioEndpoints.Map(app, embeddedWorkflowMode);
            await app.StartAsync();

            var addressFeature = app.Services
                .GetRequiredService<IServer>()
                .Features
                .Get<IServerAddressesFeature>()
                ?? throw new InvalidOperationException("Server addresses are unavailable.");
            var client = new HttpClient
            {
                BaseAddress = new Uri(addressFeature.Addresses.Single()),
            };

            return new StudioGeneratorTestHost(app, client);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.DisposeAsync();
        }
    }

    private sealed class FakeAuthoringPreviewService(params StudioAuthoringPreviewEvent[] events)
        : IStudioAuthoringPreviewApplicationService
    {
        public List<StudioAuthoringPreviewRequest> Requests { get; } = [];

        public async IAsyncEnumerable<StudioAuthoringPreviewEvent> PreviewAsync(
            StudioAuthoringPreviewRequest request,
            [EnumeratorCancellation] CancellationToken ct)
        {
            Requests.Add(request);
            foreach (var item in events)
            {
                ct.ThrowIfCancellationRequested();
                yield return item;
            }

            await Task.CompletedTask;
        }
    }
}

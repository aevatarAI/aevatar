using System.Text;
using System.Text.Json;
using Aevatar.Configuration;
using Aevatar.Tools.Cli.Hosting;
using Aevatar.Workflow.Sdk;
using Aevatar.Workflow.Sdk.Contracts;
using FluentAssertions;

namespace Aevatar.Tools.Cli.Tests;

public class ChatWorkflowCommandHandlerTests
{
    [Fact]
    public async Task RunWorkflowYamlAsync_WithYes_ShouldSaveYamlToAevatarHomeWorkflows()
    {
        var sandbox = CreateSandbox();
        try
        {
            var home = Path.Combine(sandbox, ".aevatar-home");
            var generatedYaml = """
                name: generated_from_chat
                description: Generated test workflow.
                steps:
                  - id: reply
                    type: llm_call
                """;
            var client = new StubWorkflowClient(CreateRunResult(
                CreateStepCompletedFrame("validate_yaml", ToYamlFence(generatedYaml), success: true)));

            var exitCode = await RunWithEnvironmentVariableAsync(
                AevatarPaths.HomeEnv,
                home,
                () => ChatCommandHandler.RunWorkflowYamlAsync(
                    message: "Generate workflow yaml",
                    readFromStdin: false,
                    urlOverride: "http://localhost:5100",
                    filenameOverride: null,
                    yes: true,
                    cancellationToken: CancellationToken.None,
                    workflowClient: client));

            exitCode.Should().Be(0);
            var expectedPath = Path.Combine(home, "workflows", "generated_from_chat.yaml");
            File.Exists(expectedPath).Should().BeTrue();
            File.ReadAllText(expectedPath, Encoding.UTF8).Should().Contain("name: generated_from_chat");
            client.LastRunRequest.Should().NotBeNull();
            client.LastRunRequest!.Workflow.Should().Be("auto_review");
            client.LastRunRequest.Metadata.Should().ContainKey("workflow.authoring.enabled");
            client.LastRunRequest.Metadata.Should().ContainKey("workflow.intent");
        }
        finally
        {
            SafeDeleteDirectory(sandbox);
        }
    }

    [Fact]
    public async Task RunWorkflowYamlAsync_WhenConfirmationAccepted_ShouldSaveYamlFile()
    {
        var sandbox = CreateSandbox();
        try
        {
            var home = Path.Combine(sandbox, ".aevatar-home");
            var generatedYaml = """
                name: accepted_save_flow
                description: Save after confirmation.
                steps:
                  - id: done
                    type: assign
                    parameters:
                      target: "result"
                      value: "$input"
                """;
            var client = new StubWorkflowClient(CreateRunResult(
                CreateStepCompletedFrame("validate_yaml", ToYamlFence(generatedYaml), success: true)));

            var exitCode = await RunWithEnvironmentVariableAsync(
                AevatarPaths.HomeEnv,
                home,
                () => ChatCommandHandler.RunWorkflowYamlAsync(
                    message: "Generate workflow yaml",
                    readFromStdin: false,
                    urlOverride: "http://localhost:5100",
                    filenameOverride: "confirmed",
                    yes: false,
                    cancellationToken: CancellationToken.None,
                    workflowClient: client,
                    confirmSave: static (_, _) => true));

            exitCode.Should().Be(0);
            var expectedPath = Path.Combine(home, "workflows", "confirmed.yaml");
            File.Exists(expectedPath).Should().BeTrue();
        }
        finally
        {
            SafeDeleteDirectory(sandbox);
        }
    }

    [Fact]
    public async Task RunWorkflowYamlAsync_WhenConfirmationDeclined_ShouldNotSaveFile()
    {
        var sandbox = CreateSandbox();
        try
        {
            var home = Path.Combine(sandbox, ".aevatar-home");
            var generatedYaml = """
                name: decline_save_flow
                description: Skip save after confirmation.
                steps:
                  - id: reply
                    type: llm_call
                """;
            var client = new StubWorkflowClient(CreateRunResult(
                CreateStepCompletedFrame("validate_yaml", ToYamlFence(generatedYaml), success: true)));

            var exitCode = await RunWithEnvironmentVariableAsync(
                AevatarPaths.HomeEnv,
                home,
                () => ChatCommandHandler.RunWorkflowYamlAsync(
                    message: "Generate workflow yaml",
                    readFromStdin: false,
                    urlOverride: "http://localhost:5100",
                    filenameOverride: "declined",
                    yes: false,
                    cancellationToken: CancellationToken.None,
                    workflowClient: client,
                    confirmSave: static (_, _) => false));

            exitCode.Should().Be(0);
            var expectedPath = Path.Combine(home, "workflows", "declined.yaml");
            File.Exists(expectedPath).Should().BeFalse();
        }
        finally
        {
            SafeDeleteDirectory(sandbox);
        }
    }

    [Fact]
    public async Task RunWorkflowYamlAsync_ShouldPreferValidateYamlStepOutput_WhenMultipleYamlCandidatesExist()
    {
        var sandbox = CreateSandbox();
        try
        {
            var home = Path.Combine(sandbox, ".aevatar-home");
            var generatedStepYaml = """
                name: unvalidated_draft
                description: Draft yaml from generation step.
                steps:
                  - id: reply
                    type: llm_call
                """;
            var validatedYaml = """
                name: validated_yaml
                description: Final validated yaml.
                steps:
                  - id: done
                    type: assign
                    parameters:
                      target: "result"
                      value: "$input"
                """;

            var runResult = CreateRunResult(
                CreateStepCompletedFrame("generate_workflow_yaml", ToYamlFence(generatedStepYaml), success: true),
                CreateStepCompletedFrame("validate_yaml", ToYamlFence(validatedYaml), success: true),
                CreateTerminalRunFinishedFrame(ToYamlFence(generatedStepYaml)));
            var client = new StubWorkflowClient(runResult);

            var exitCode = await RunWithEnvironmentVariableAsync(
                AevatarPaths.HomeEnv,
                home,
                () => ChatCommandHandler.RunWorkflowYamlAsync(
                    message: "Generate workflow yaml",
                    readFromStdin: false,
                    urlOverride: "http://localhost:5100",
                    filenameOverride: "preferred",
                    yes: true,
                    cancellationToken: CancellationToken.None,
                    workflowClient: client));

            exitCode.Should().Be(0);
            var savedPath = Path.Combine(home, "workflows", "preferred.yaml");
            File.Exists(savedPath).Should().BeTrue();
            var savedYaml = File.ReadAllText(savedPath, Encoding.UTF8);
            savedYaml.Should().Contain("name: validated_yaml");
            savedYaml.Should().NotContain("name: unvalidated_draft");
        }
        finally
        {
            SafeDeleteDirectory(sandbox);
        }
    }

    private static string CreateSandbox()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "aevatar-chat-workflow-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static WorkflowRunResult CreateRunResult(params WorkflowEvent[] events) =>
        new(events);

    private static WorkflowEvent CreateStepCompletedFrame(string stepId, string output, bool success)
    {
        var payload = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            runId = "run-chat-workflow",
            stepId,
            success,
            output,
        })).RootElement.Clone();
        return WorkflowEvent.FromFrame(new WorkflowOutputFrame
        {
            Type = WorkflowEventTypes.Custom,
            Name = WorkflowCustomEventNames.StepCompleted,
            Value = payload,
        });
    }

    private static WorkflowEvent CreateTerminalRunFinishedFrame(string output)
    {
        var result = JsonDocument.Parse(JsonSerializer.Serialize(new { output })).RootElement.Clone();
        return WorkflowEvent.FromFrame(new WorkflowOutputFrame
        {
            Type = WorkflowEventTypes.RunFinished,
            Result = result,
        });
    }

    private static string ToYamlFence(string yaml) => $"```yaml\n{yaml}\n```";

    private static async Task<T> RunWithEnvironmentVariableAsync<T>(
        string variableName,
        string? value,
        Func<Task<T>> action)
    {
        var previous = Environment.GetEnvironmentVariable(variableName);
        Environment.SetEnvironmentVariable(variableName, value);
        try
        {
            return await action();
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, previous);
        }
    }

    private static void SafeDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }

    private sealed class StubWorkflowClient(WorkflowRunResult runResult) : IAevatarWorkflowClient
    {
        public ChatRunRequest? LastRunRequest { get; private set; }

        public IAsyncEnumerable<WorkflowEvent> StartRunStreamAsync(ChatRunRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<WorkflowRunResult> RunToCompletionAsync(ChatRunRequest request, CancellationToken cancellationToken = default)
        {
            LastRunRequest = request;
            return Task.FromResult(runResult);
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

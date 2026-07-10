using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.RunForks;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Infrastructure.CapabilityApi;
using Aevatar.Workflow.Infrastructure.DependencyInjection;
using Aevatar.Foundation.Abstractions.Connectors;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using ApplicationWorkflowFileRef = Aevatar.Workflow.Application.Abstractions.Runs.WorkflowFileRef;
using ApplicationWorkflowFileSourceKind = Aevatar.Workflow.Application.Abstractions.Runs.WorkflowFileSourceKind;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class ChatEndpointsInternalTests
{
    [Fact]
    public async Task HandleCommand_ShouldReturnAcceptedPayload_WhenDispatchSucceeds()
    {
        var service = new FakeCommandDispatchService
        {
            Result = CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>.Success(
                new WorkflowChatRunAcceptedReceipt("actor-1", "direct", "cmd-1", "corr-1")),
        };

        var result = await WorkflowCapabilityEndpoints.HandleCommand(
            new ChatInput { Prompt = "hello", Workflow = "direct" },
            service,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);
        var body = await ReadBodyAsync(http.Response);

        service.LastCommand.Should().NotBeNull();
        service.LastCommand!.Source.WorkflowName.Should().Be("direct");
        http.Response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        body.Should().Contain("cmd-1");
        body.Should().Contain("corr-1");
        body.Should().Contain("actor-1");
    }

    [Fact]
    public async Task HandleForkRun_ShouldDispatchTypedForkCommandAndReturnAccepted()
    {
        var service = new RecordingDispatchService<WorkflowForkRunCommand, WorkflowForkRunAcceptedReceipt, WorkflowForkRunStartError>
        {
            Result = CommandDispatchResult<WorkflowForkRunAcceptedReceipt, WorkflowForkRunStartError>.Success(
                new WorkflowForkRunAcceptedReceipt(
                    "source-run",
                    "new-run-actor",
                    "direct",
                    true,
                    "cmd-1",
                    "corr-1",
                    new DateTimeOffset(2026, 6, 8, 0, 0, 0, TimeSpan.Zero))),
        };

        var result = await WorkflowCapabilityEndpoints.HandleForkRun(
            new WorkflowForkRunInput
            {
                SourceRunId = " source-run ",
                StartAtStepId = " step-b ",
                Input = "resume input",
                CommandId = " cmd-1 ",
                CorrelationId = " corr-1 ",
                ScopeId = " scope-1 ",
                VariableOverrides = new Dictionary<string, string>
                {
                    [" topic "] = "override",
                },
            },
            service,
            ct: CancellationToken.None);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);
        var body = await ReadBodyAsync(http.Response);

        service.Commands.Should().ContainSingle();
        var command = service.Commands[0];
        command.SourceRunId.Should().Be("source-run");
        command.StartAtStepId.Should().Be("step-b");
        command.ScopeId.Should().Be("scope-1");
        command.VariableOverrides.Should().Contain("topic", "override");
        http.Response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        body.Should().Contain("new-run-actor");
        body.Should().Contain("cmd-1");
        body.Should().Contain("corr-1");
    }

    [Fact]
    public async Task HandleForkRun_ShouldMapInvalidWorkflowYaml()
    {
        var service = new RecordingDispatchService<WorkflowForkRunCommand, WorkflowForkRunAcceptedReceipt, WorkflowForkRunStartError>
        {
            Result = CommandDispatchResult<WorkflowForkRunAcceptedReceipt, WorkflowForkRunStartError>.Failure(
                WorkflowForkRunStartError.InvalidWorkflowYaml("source-run", "step-b", "Workflow YAML is invalid.")),
        };

        var result = await WorkflowCapabilityEndpoints.HandleForkRun(
            new WorkflowForkRunInput
            {
                SourceRunId = "source-run",
                StartAtStepId = "step-b",
            },
            service,
            ct: CancellationToken.None);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);
        var body = await ReadBodyAsync(http.Response);

        http.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        body.Should().Contain("Workflow YAML is invalid.");
    }

    [Fact]
    public async Task HandleCommand_ShouldReturnAcceptedPayload_WithoutWaitingForTerminalWorkflowEvents()
    {
        var service = new FakeCommandDispatchService
        {
            Result = CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>.Success(
                new WorkflowChatRunAcceptedReceipt("actor-1", "direct", "cmd-1", "corr-1")),
        };

        var result = await WorkflowCapabilityEndpoints.HandleCommand(
            new ChatInput { Prompt = "hello", Workflow = "direct" },
            service,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);
        var body = await ReadBodyAsync(http.Response);

        http.Response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        body.Should().Contain("cmd-1");
        service.DispatchCalls.Should().Be(1);
    }

    [Fact]
    public async Task HandleCommand_ShouldPreserveOpaqueActorIdInAcceptedLocationAndPayload()
    {
        const string opaqueActorId = "script-runtime:opaque-actor-9";
        var service = new FakeCommandDispatchService
        {
            Result = CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>.Success(
                new WorkflowChatRunAcceptedReceipt(opaqueActorId, "direct", "cmd-1", "corr-1")),
        };

        var result = await WorkflowCapabilityEndpoints.HandleCommand(
            new ChatInput { Prompt = "hello" },
            service,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);
        var body = await ReadBodyAsync(http.Response);

        http.Response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        http.Response.Headers.Location.ToString().Should().Be($"/api/workflow-actors/{Uri.EscapeDataString(opaqueActorId)}/current-state");
        body.Should().Contain(opaqueActorId);
    }

    [Fact]
    public async Task HandleCommand_ShouldMapStartError_WhenDispatchFails()
    {
        var service = new FakeCommandDispatchService
        {
            Result = CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>.Failure(
                WorkflowChatRunStartError.WorkflowNotFound),
        };

        var result = await WorkflowCapabilityEndpoints.HandleCommand(
            new ChatInput { Prompt = "hello", Workflow = "missing" },
            service,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);
        var body = await ReadBodyAsync(http.Response);

        http.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        body.Should().Contain("WORKFLOW_NOT_FOUND");
        service.LastCommand.Should().NotBeNull();
        service.LastCommand!.Source.WorkflowName.Should().Be("missing");
    }

    [Fact]
    public async Task HandleCommand_ShouldRejectEmptyPrompt()
    {
        var service = new FakeCommandDispatchService();
        var result = await WorkflowCapabilityEndpoints.HandleCommand(
            new ChatInput { Prompt = " " },
            service,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);
        var body = await ReadBodyAsync(http.Response);

        http.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        body.Should().Contain("PROMPT_REQUIRED");
        service.LastCommand.Should().BeNull();
    }

    [Fact]
    public async Task HandleCommand_ShouldAcceptMultimodalInputWithoutPrompt()
    {
        var service = new FakeCommandDispatchService
        {
            Result = CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>.Success(
                new WorkflowChatRunAcceptedReceipt("actor-1", "direct", "cmd-1", "corr-1")),
        };

        var result = await WorkflowCapabilityEndpoints.HandleCommand(
            new ChatInput
            {
                InputParts =
                [
                    new ChatInputContentPart
                    {
                        Type = "image",
                        Uri = "https://example.com/cat.png",
                        MediaType = "image/png",
                    },
                ],
            },
            service,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        service.LastCommand.Should().NotBeNull();
        service.LastCommand!.Prompt.Should().Be("[image]");
        service.LastCommand.InputParts.Should().ContainSingle();
        service.LastCommand.InputParts![0].Kind.Should()
            .Be(Aevatar.Workflow.Application.Abstractions.Runs.WorkflowChatInputPartKind.Image);
    }

    [Fact]
    public async Task PostChat_ShouldReturnInvalidFileInput_WhenInlineFileSizeBytesMismatchesDecodedBytes()
    {
        var service = new FakeCommandDispatchService();
        var input = JsonSerializer.Deserialize<ChatInput>(
            """
            {
              "inputParts": [
                {
                  "type": "image",
                  "inlineFile": {
                    "dataBase64": "aGVsbG8=",
                    "mediaType": "image/png",
                    "sizeBytes": 6
                  }
                }
              ]
            }
            """,
            ChatWebSocketProtocol.JsonOptions)!;

        var result = await WorkflowCapabilityEndpoints.HandleCommand(
            input,
            service,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);
        var body = await ReadBodyAsync(http.Response);

        http.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        body.Should().Contain("INVALID_FILE_INPUT");
        service.DispatchCalls.Should().Be(0);
        service.LastCommand.Should().BeNull();
    }

    [Fact]
    public async Task PostChat_ShouldReturnInvalidFileInput_WhenUnsupportedInputPartHasInvalidInlineFileSizeBytes()
    {
        var service = new FakeCommandDispatchService();
        var input = JsonSerializer.Deserialize<ChatInput>(
            """
            {
              "prompt": "describe this",
              "inputParts": [
                {
                  "type": "unsupported",
                  "inlineFile": {
                    "dataBase64": "aGVsbG8=",
                    "mediaType": "image/png",
                    "sizeBytes": -1
                  }
                }
              ]
            }
            """,
            ChatWebSocketProtocol.JsonOptions)!;

        var result = await WorkflowCapabilityEndpoints.HandleCommand(
            input,
            service,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);
        var body = await ReadBodyAsync(http.Response);

        http.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        body.Should().Contain("INVALID_FILE_INPUT");
        service.DispatchCalls.Should().Be(0);
        service.LastCommand.Should().BeNull();
    }

    [Fact]
    public async Task PostChat_ShouldDispatch_WhenInlineFileSizeBytesMatchesDecodedBytes()
    {
        var service = new FakeCommandDispatchService
        {
            Result = CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>.Success(
                new WorkflowChatRunAcceptedReceipt("actor-1", "direct", "cmd-1", "corr-1")),
        };
        var ingressPort = new RecordingWorkflowFileIngressPort();
        var input = JsonSerializer.Deserialize<ChatInput>(
            """
            {
              "inputParts": [
                {
                  "type": "image",
                  "inlineFile": {
                    "dataBase64": "aGVsbG8=",
                    "mediaType": "image/png",
                    "name": "hello.png",
                    "sizeBytes": 5
                  }
                }
              ]
            }
            """,
            ChatWebSocketProtocol.JsonOptions)!;

        var result = await WorkflowCapabilityEndpoints.HandleCommand(
            input,
            service,
            NullLoggerFactory.Instance,
            CancellationToken.None,
            fileIngressPort: ingressPort);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        service.DispatchCalls.Should().Be(1);
        ingressPort.Requests.Should().ContainSingle();
        ingressPort.Requests[0].Content.ToArray().Should().Equal(Encoding.UTF8.GetBytes("hello"));
        ingressPort.Requests[0].SourceKind.Should().Be(ApplicationWorkflowFileSourceKind.ChatInput);
        ingressPort.Requests[0].FileName.Should().Be("hello.png");
        ingressPort.Requests[0].MediaType.Should().Be("image/png");
        service.LastCommand.Should().NotBeNull();
        var part = service.LastCommand!.InputParts.Should().ContainSingle().Which;
        part.DataBase64.Should().BeNull();
        part.FileRef.Should().NotBeNull();
        part.FileRef!.FileId.Should().Be("file-1");
        part.FileRef.ArtifactId.Should().Be("workflow-file://file-1");
        part.FileRef.SizeBytes.Should().Be(5);
        part.Uri.Should().Be("workflow-file://file-1");
    }

    [Fact]
    public async Task HandleCommand_ShouldRejectUnsupportedOnlyInputParts()
    {
        var service = new FakeCommandDispatchService();
        var result = await WorkflowCapabilityEndpoints.HandleCommand(
            new ChatInput
            {
                InputParts =
                [
                    new ChatInputContentPart
                    {
                        Type = "foo",
                    },
                ],
            },
            service,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);
        var body = await ReadBodyAsync(http.Response);

        http.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        body.Should().Contain("PROMPT_REQUIRED");
        service.LastCommand.Should().BeNull();
    }

    [Fact]
    public async Task HandleCommand_ShouldReturn499_WhenDispatchCanceled()
    {
        var service = new FakeCommandDispatchService
        {
            DispatchException = new OperationCanceledException("cancelled"),
        };

        var result = await WorkflowCapabilityEndpoints.HandleCommand(
            new ChatInput { Prompt = "hello" },
            service,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(499);
    }

    [Fact]
    public async Task HandleCommand_ShouldReturnServerError_WhenDispatchThrows()
    {
        var service = new FakeCommandDispatchService
        {
            DispatchException = new InvalidOperationException("boom"),
        };

        var result = await WorkflowCapabilityEndpoints.HandleCommand(
            new ChatInput { Prompt = "hello" },
            service,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);
        var body = await ReadBodyAsync(http.Response);

        http.Response.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
        body.Should().Contain("EXECUTION_FAILED");
    }

    [Fact]
    public async Task HandleChat_ShouldRejectEmptyPrompt()
    {
        var http = CreateHttpContext();
        var interactionService = new FakeCommandInteractionService
        {
            ResultFactory = (_, _, _, _) => Task.FromResult(
                CommandInteractionResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowProjectionCompletionStatus>
                    .Failure(WorkflowChatRunStartError.AgentNotFound)),
        };

        await WorkflowCapabilityEndpoints.HandleChat(
            http,
            new ChatInput { Prompt = "" },
            interactionService,
            CancellationToken.None);

        var body = await ReadBodyAsync(http.Response);
        http.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        body.Should().Contain("PROMPT_REQUIRED");
    }

    [Fact]
    public async Task HandleChat_ShouldRejectUnsupportedOnlyInputParts()
    {
        var http = CreateHttpContext();
        var called = false;
        var interactionService = new FakeCommandInteractionService
        {
            ResultFactory = (_, _, _, _) =>
            {
                called = true;
                return Task.FromResult(
                    CommandInteractionResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowProjectionCompletionStatus>
                        .Failure(WorkflowChatRunStartError.AgentNotFound));
            },
        };

        await WorkflowCapabilityEndpoints.HandleChat(
            http,
            new ChatInput
            {
                InputParts =
                [
                    new ChatInputContentPart
                    {
                        Type = "foo",
                    },
                ],
            },
            interactionService,
            CancellationToken.None);

        var body = await ReadBodyAsync(http.Response);
        http.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        body.Should().Contain("PROMPT_REQUIRED");
        called.Should().BeFalse();
    }

    [Fact]
    public async Task HandleChat_ShouldReturnJsonError_WhenExecutionReturnsStartErrorBeforeWriterStarts()
    {
        var http = CreateHttpContext();
        var interactionService = new FakeCommandInteractionService
        {
            ResultFactory = (_, _, _, _) => Task.FromResult(
                CommandInteractionResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowProjectionCompletionStatus>
                    .Failure(WorkflowChatRunStartError.WorkflowBindingMismatch)),
        };

        await WorkflowCapabilityEndpoints.HandleChat(
            http,
            new ChatInput { Prompt = "hello" },
            interactionService,
            CancellationToken.None);

        var body = await ReadBodyAsync(http.Response);
        http.Response.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        body.Should().Contain("WORKFLOW_BINDING_MISMATCH");
    }

    [Fact]
    public async Task HandleChat_ShouldPassTrustedBearerAsWorkflowCallerCredential()
    {
        var capturedCommand = default(WorkflowChatRunRequest);
        var interactionService = new FakeCommandInteractionService
        {
            ResultFactory = (command, _, _, _) =>
            {
                capturedCommand = command;
                return Task.FromResult(
                    CommandInteractionResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowProjectionCompletionStatus>
                        .Failure(WorkflowChatRunStartError.WorkflowBindingMismatch));
            },
        };
        var http = CreateHttpContext();
        http.Request.Headers.Authorization = "Bearer trusted-token";

        await WorkflowCapabilityEndpoints.HandleChat(
            http,
            new ChatInput
            {
                Prompt = "hello",
                Metadata = new Dictionary<string, string>
                {
                    ["connector.http.authorization"] = "Bearer untrusted",
                },
            },
            interactionService,
            CancellationToken.None);

        capturedCommand.Should().NotBeNull();
        capturedCommand!.CallerCredential!.BearerToken.Should().Be("trusted-token");
        capturedCommand.Metadata.Should().NotContainKey("connector.http.authorization");
    }

    [Fact]
    public async Task HandleChat_ShouldResolveIngressPortAndDispatchFileRefForInlineFile()
    {
        var capturedCommand = default(WorkflowChatRunRequest);
        var ingressPort = new RecordingWorkflowFileIngressPort();
        var interactionService = new FakeCommandInteractionService
        {
            ResultFactory = (command, _, _, _) =>
            {
                capturedCommand = command;
                return Task.FromResult(
                    CommandInteractionResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowProjectionCompletionStatus>
                        .Failure(WorkflowChatRunStartError.WorkflowBindingMismatch));
            },
        };
        var http = CreateHttpContext();
        http.RequestServices = new ServiceCollection()
            .AddLogging()
            .AddOptions()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
            .AddSingleton<IHostEnvironment>(new StubHostEnvironment())
            .AddSingleton<IWorkflowFileIngressPort>(ingressPort)
            .BuildServiceProvider();
        var input = JsonSerializer.Deserialize<ChatInput>(
            """
            {
              "inputParts": [
                {
                  "type": "image",
                  "inlineFile": {
                    "dataBase64": "aGVsbG8=",
                    "mediaType": "image/png",
                    "name": "hello.png",
                    "sizeBytes": 5
                  }
                }
              ]
            }
            """,
            ChatWebSocketProtocol.JsonOptions)!;

        await WorkflowCapabilityEndpoints.HandleChat(
            http,
            input,
            interactionService,
            CancellationToken.None);

        ingressPort.Requests.Should().ContainSingle();
        capturedCommand.Should().NotBeNull();
        var part = capturedCommand!.InputParts.Should().ContainSingle().Which;
        part.DataBase64.Should().BeNull();
        part.FileRef.Should().NotBeNull();
        part.FileRef!.ArtifactId.Should().Be("workflow-file://file-1");
        part.FileRef.SizeBytes.Should().Be(5);
    }

    [Fact]
    public async Task HandleChatPost_ShouldParseMultipartUploadBeforeDispatchingWorkflowCommand()
    {
        var capturedCommand = default(WorkflowChatRunRequest);
        var ingressPort = new RecordingWorkflowFileIngressPort();
        var interactionService = new FakeCommandInteractionService
        {
            ResultFactory = (command, _, _, _) =>
            {
                capturedCommand = command;
                return Task.FromResult(
                    CommandInteractionResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowProjectionCompletionStatus>
                        .Failure(WorkflowChatRunStartError.WorkflowBindingMismatch));
            },
        };
        var parser = new WorkflowMultipartChatRequestParser(
            ingressPort,
            Options.Create(new WorkflowMultipartFileIngressOptions()));
        var http = CreateHttpContext("Bearer trusted-token");
        http.Request.ContentType = "multipart/form-data; boundary=test";
        http.Features.Set<IFormFeature>(new FormFeature(new FormCollection(
            ToFormFields(new Dictionary<string, string>
            {
                ["prompt"] = "describe this",
                ["workflow"] = "direct",
                ["scopeId"] = "scope-1",
            }),
            new FormFileCollection
            {
                CreateFormFile("file", "cat.png", "image/png", "hello"),
            })));

        await WorkflowCapabilityEndpoints.HandleChatPost(
            http,
            interactionService,
            parser,
            CancellationToken.None);

        ingressPort.Requests.Should().ContainSingle();
        ingressPort.Requests[0].SourceKind.Should().Be(ApplicationWorkflowFileSourceKind.FormUpload);
        ingressPort.Requests[0].OwnerScopeId.Should().Be("scope-1");
        capturedCommand.Should().NotBeNull();
        capturedCommand!.CallerCredential!.BearerToken.Should().Be("trusted-token");
        capturedCommand.ScopeId.Should().Be("scope-1");
        var part = capturedCommand.InputParts.Should().ContainSingle().Which;
        part.DataBase64.Should().BeNull();
        part.FileRef.Should().NotBeNull();
        part.FileRef!.SourceKind.Should().Be(ApplicationWorkflowFileSourceKind.FormUpload);
        part.FileRef.ArtifactId.Should().Be("workflow-file://file-1");
    }

    [Fact]
    public async Task HandleChatPost_ShouldRejectMalformedBearerBeforeIngestingMultipartFile()
    {
        var ingressPort = new RecordingWorkflowFileIngressPort();
        var parser = new WorkflowMultipartChatRequestParser(
            ingressPort,
            Options.Create(new WorkflowMultipartFileIngressOptions()));
        var http = CreateHttpContext("Bearer token 123");
        http.Request.ContentType = "multipart/form-data; boundary=test";
        http.Features.Set<IFormFeature>(new FormFeature(new FormCollection(
            ToFormFields(new Dictionary<string, string>
            {
                ["prompt"] = "describe this",
            }),
            new FormFileCollection
            {
                CreateFormFile("file", "cat.png", "image/png", "hello"),
            })));

        await WorkflowCapabilityEndpoints.HandleChatPost(
            http,
            new FakeCommandInteractionService(),
            parser,
            CancellationToken.None);

        var body = await ReadBodyAsync(http.Response);
        http.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        body.Should().Contain("INVALID_CALLER_CREDENTIAL");
        ingressPort.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleChatPost_ShouldDeserializeJsonBodyBeforeDispatchingWorkflowCommand()
    {
        var capturedCommand = default(WorkflowChatRunRequest);
        var interactionService = new FakeCommandInteractionService
        {
            ResultFactory = (command, _, _, _) =>
            {
                capturedCommand = command;
                return Task.FromResult(
                    CommandInteractionResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowProjectionCompletionStatus>
                        .Failure(WorkflowChatRunStartError.WorkflowBindingMismatch));
            },
        };
        var parser = new WorkflowMultipartChatRequestParser(
            new RecordingWorkflowFileIngressPort(),
            Options.Create(new WorkflowMultipartFileIngressOptions()));
        var http = CreateHttpContext("Bearer trusted-token");
        http.Request.ContentType = "application/json";
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(
            """
            {
              "prompt": "describe the release plan",
              "workflow": "direct",
              "sessionId": "session-1",
              "scopeId": "scope-1"
            }
            """));

        await WorkflowCapabilityEndpoints.HandleChatPost(
            http,
            interactionService,
            parser,
            CancellationToken.None);

        capturedCommand.Should().NotBeNull();
        capturedCommand!.Prompt.Should().Be("describe the release plan");
        capturedCommand.Source.WorkflowName.Should().Be("direct");
        capturedCommand.SessionId.Should().Be("session-1");
        capturedCommand.ScopeId.Should().Be("scope-1");
        capturedCommand.CallerCredential!.BearerToken.Should().Be("trusted-token");
    }

    [Fact]
    public async Task HandleChatPost_ShouldReturnInvalidChatInputAndSkipDispatch_WhenJsonBodyIsMalformed()
    {
        var called = false;
        var interactionService = new FakeCommandInteractionService
        {
            ResultFactory = (_, _, _, _) =>
            {
                called = true;
                return Task.FromResult(
                    CommandInteractionResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowProjectionCompletionStatus>
                        .Failure(WorkflowChatRunStartError.WorkflowBindingMismatch));
            },
        };
        var parser = new WorkflowMultipartChatRequestParser(
            new RecordingWorkflowFileIngressPort(),
            Options.Create(new WorkflowMultipartFileIngressOptions()));
        var http = CreateHttpContext("Bearer trusted-token");
        http.Request.ContentType = "application/json";
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("""{ "prompt": """));

        await WorkflowCapabilityEndpoints.HandleChatPost(
            http,
            interactionService,
            parser,
            CancellationToken.None);

        var body = await ReadBodyAsync(http.Response);
        http.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        body.Should().Contain("INVALID_CHAT_INPUT");
        called.Should().BeFalse();
    }

    [Fact]
    public async Task HandleChatPost_ShouldReturnUnsupportedMediaType_WhenContentTypeIsNotJsonOrMultipart()
    {
        var parser = new WorkflowMultipartChatRequestParser(
            new RecordingWorkflowFileIngressPort(),
            Options.Create(new WorkflowMultipartFileIngressOptions()));
        var http = CreateHttpContext();
        http.Request.ContentType = "text/plain";

        await WorkflowCapabilityEndpoints.HandleChatPost(
            http,
            new FakeCommandInteractionService(),
            parser,
            CancellationToken.None);

        var body = await ReadBodyAsync(http.Response);
        http.Response.StatusCode.Should().Be(StatusCodes.Status415UnsupportedMediaType);
        body.Should().Contain("UNSUPPORTED_MEDIA_TYPE");
        body.Should().Contain("Content-Type must be application/json or multipart/form-data.");
    }

    [Fact]
    public void MapWorkflowCapabilityEndpoints_ShouldMapChatHttpAndWebSocketRoutes()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddWorkflowCapability(new ConfigurationBuilder().Build());
        var app = builder.Build();

        app.MapWorkflowCapabilityEndpoints();

        ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(x => x.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(x => x.RoutePattern.RawText)
            .Should()
            .Contain(["/api/chat", "/api/ws/chat"]);
    }

    [Fact]
    public async Task HandleChat_ShouldReturnInvalidCallerCredential_WhenAuthorizationBearerIsMalformed()
    {
        var called = false;
        var interactionService = new FakeCommandInteractionService
        {
            ResultFactory = (_, _, _, _) =>
            {
                called = true;
                return Task.FromResult(
                    CommandInteractionResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowProjectionCompletionStatus>
                        .Failure(WorkflowChatRunStartError.AgentNotFound));
            },
        };
        var http = CreateHttpContext();
        http.Request.Headers.Authorization = "Bearer token 123";

        await WorkflowCapabilityEndpoints.HandleChat(
            http,
            new ChatInput { Prompt = "hello" },
            interactionService,
            CancellationToken.None);

        var body = await ReadBodyAsync(http.Response);
        http.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        body.Should().Contain("INVALID_CALLER_CREDENTIAL");
        body.Should().Contain("Caller credential is invalid.");
        called.Should().BeFalse();
    }

    [Fact]
    public void WorkflowCallerCredentialExtractor_ShouldExposeMissingValidAndInvalidStatus()
    {
        var missingHttpContext = WorkflowCallerCredentialExtractor.Extract(null);
        var missingHttp = CreateHttpContext();
        var unsupportedSchemeHttp = CreateHttpContext();
        unsupportedSchemeHttp.Request.Headers.Authorization = "Basic token-123";
        var validHttp = CreateHttpContext();
        validHttp.Request.Headers.Authorization = "Bearer token-123";
        var bareBearerHttp = CreateHttpContext();
        bareBearerHttp.Request.Headers.Authorization = "Bearer";
        var invalidHttp = CreateHttpContext();
        invalidHttp.Request.Headers.Authorization = "Bearer token 123";

        var missing = WorkflowCallerCredentialExtractor.Extract(missingHttp);
        var unsupportedScheme = WorkflowCallerCredentialExtractor.Extract(unsupportedSchemeHttp);
        var valid = WorkflowCallerCredentialExtractor.Extract(validHttp);
        var bareBearer = WorkflowCallerCredentialExtractor.Extract(bareBearerHttp);
        var invalid = WorkflowCallerCredentialExtractor.Extract(invalidHttp);

        missingHttpContext.Succeeded.Should().BeTrue();
        missingHttpContext.Credential.Should().BeNull();
        missing.Succeeded.Should().BeTrue();
        missing.Credential.Should().BeNull();
        unsupportedScheme.Succeeded.Should().BeTrue();
        unsupportedScheme.Credential.Should().BeNull();
        valid.Succeeded.Should().BeTrue();
        valid.Credential!.BearerToken.Should().Be("token-123");
        bareBearer.Succeeded.Should().BeFalse();
        bareBearer.Error.Should().Be(WorkflowChatRunStartError.InvalidCallerCredential);
        bareBearer.Credential.Should().BeNull();
        invalid.Succeeded.Should().BeFalse();
        invalid.Error.Should().Be(WorkflowChatRunStartError.InvalidCallerCredential);
        invalid.Credential.Should().BeNull();
    }

    [Fact]
    public async Task HandleChat_ShouldWriteSseFramesAndCorrelationHeader_WhenExecutionSucceeds()
    {
        var interactionService = new FakeCommandInteractionService
        {
            ResultFactory = async (_, emitAsync, onAcceptedAsync, ct) =>
            {
                var receipt = new WorkflowChatRunAcceptedReceipt("actor-1", "direct", "cmd-1", "corr-1");
                if (onAcceptedAsync != null)
                    await onAcceptedAsync(receipt, ct);
                await emitAsync(new WorkflowRunEventEnvelope
                {
                    TextMessageContent = new WorkflowTextMessageContentEventPayload
                    {
                        MessageId = "message-1",
                        Delta = "hello",
                    },
                }, ct);
                return CommandInteractionResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowProjectionCompletionStatus>
                    .Success(receipt, new CommandInteractionFinalizeResult<WorkflowProjectionCompletionStatus>(WorkflowProjectionCompletionStatus.Completed, true));
            },
        };
        var http = CreateHttpContext();

        await WorkflowCapabilityEndpoints.HandleChat(
            http,
            new ChatInput { Prompt = "hello" },
            interactionService,
            CancellationToken.None);

        var body = await ReadBodyAsync(http.Response);
        http.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        http.Response.Headers["X-Correlation-Id"].ToString().Should().Be("corr-1");
        body.Should().Contain("aevatar.run.context");
        body.Should().Contain("\"delta\": \"hello\"");
    }

    [Fact]
    public async Task HandleChat_ShouldSerializeRawObservedWorkflowExecutionStartedPayload()
    {
        var interactionService = new FakeCommandInteractionService
        {
            ResultFactory = async (_, emitAsync, onAcceptedAsync, ct) =>
            {
                var receipt = new WorkflowChatRunAcceptedReceipt("actor-1", "direct", "cmd-1", "corr-1");
                if (onAcceptedAsync != null)
                    await onAcceptedAsync(receipt, ct);
                await emitAsync(BuildRawObservedWorkflowExecutionStartedFrame(), ct);
                return CommandInteractionResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowProjectionCompletionStatus>
                    .Success(receipt, new CommandInteractionFinalizeResult<WorkflowProjectionCompletionStatus>(WorkflowProjectionCompletionStatus.Completed, true));
            },
        };
        var http = CreateHttpContext();

        await WorkflowCapabilityEndpoints.HandleChat(
            http,
            new ChatInput { Prompt = "hello" },
            interactionService,
            CancellationToken.None);

        var body = await ReadBodyAsync(http.Response);
        http.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        body.Should().Contain("aevatar.raw.observed");
        body.Should().Contain("WorkflowRunExecutionStartedEvent");
        body.Should().Contain("\"runId\": \"run-1\"");
        body.Should().NotContain("EXECUTION_FAILED");
    }

    [Fact]
    public async Task HandleChat_ShouldSerializeRawObservedWorkflowExecutionStatePayloadWithNestedKernelState()
    {
        var interactionService = new FakeCommandInteractionService
        {
            ResultFactory = async (_, emitAsync, onAcceptedAsync, ct) =>
            {
                var receipt = new WorkflowChatRunAcceptedReceipt("actor-1", "direct", "cmd-1", "corr-1");
                if (onAcceptedAsync != null)
                    await onAcceptedAsync(receipt, ct);
                await emitAsync(BuildRawObservedWorkflowExecutionStateUpsertedFrame(), ct);
                return CommandInteractionResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowProjectionCompletionStatus>
                    .Success(receipt, new CommandInteractionFinalizeResult<WorkflowProjectionCompletionStatus>(WorkflowProjectionCompletionStatus.Completed, true));
            },
        };
        var http = CreateHttpContext();

        await WorkflowCapabilityEndpoints.HandleChat(
            http,
            new ChatInput { Prompt = "hello" },
            interactionService,
            CancellationToken.None);

        var body = await ReadBodyAsync(http.Response);
        http.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        body.Should().Contain("aevatar.raw.observed");
        body.Should().Contain("WorkflowExecutionStateUpsertedEvent");
        body.Should().Contain("WorkflowExecutionKernelState");
        body.Should().Contain("\"scopeKey\": \"workflow_execution_kernel\"");
        body.Should().Contain("\"runId\": \"run-1\"");
        body.Should().Contain("\"currentStepId\": \"analyze\"");
        body.Should().NotContain("EXECUTION_FAILED");
    }

    [Fact]
    public async Task HandleChat_ShouldReturnServerError_WhenExecutionThrowsBeforeStreamStarts()
    {
        var http = CreateHttpContext();
        var interactionService = new FakeCommandInteractionService
        {
            ResultFactory = (_, _, _, _) => throw new InvalidOperationException("provider secret token leaked"),
        };

        await WorkflowCapabilityEndpoints.HandleChat(
            http,
            new ChatInput { Prompt = "hello" },
            interactionService,
            CancellationToken.None);

        var body = await ReadBodyAsync(http.Response);
        http.Response.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
        body.Should().Contain("EXECUTION_FAILED");
        body.Should().Contain("Workflow execution failed.");
        body.Should().NotContain("provider secret token leaked");
    }

    [Fact]
    public async Task HandleChat_ShouldWriteRunErrorFrame_WhenExecutionThrowsAfterStreamStarts()
    {
        var http = CreateHttpContext();
        var interactionService = new FakeCommandInteractionService
        {
            ResultFactory = async (_, emitAsync, onAcceptedAsync, ct) =>
            {
                var receipt = new WorkflowChatRunAcceptedReceipt("actor-1", "direct", "cmd-1", "corr-1");
                if (onAcceptedAsync != null)
                    await onAcceptedAsync(receipt, ct);
                await emitAsync(new WorkflowRunEventEnvelope
                {
                    TextMessageContent = new WorkflowTextMessageContentEventPayload
                    {
                        MessageId = "message-1",
                        Delta = "hello",
                    },
                }, ct);
                throw new InvalidOperationException("line1\r\nline2");
            },
        };

        await WorkflowCapabilityEndpoints.HandleChat(
            http,
            new ChatInput { Prompt = "hello" },
            interactionService,
            CancellationToken.None);

        var body = await ReadBodyAsync(http.Response);
        http.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        body.Should().Contain("\"delta\": \"hello\"");
        body.Should().Contain("Workflow execution failed.");
        body.Should().NotContain("line1");
    }

    [Fact]
    public async Task HandleChat_ShouldWriteCompatibilityError_WhenTypeRegistryDescriptorIsMissing()
    {
        var http = CreateHttpContext();
        var interactionService = new FakeCommandInteractionService
        {
            ResultFactory = async (_, _, onAcceptedAsync, ct) =>
            {
                var receipt = new WorkflowChatRunAcceptedReceipt("actor-1", "direct", "cmd-1", "corr-1");
                if (onAcceptedAsync != null)
                    await onAcceptedAsync(receipt, ct);

                throw new InvalidOperationException(
                    "Type registry has no descriptor for type name 'aevatar.ai.InitializeRoleAgentEvent'");
            },
        };

        await WorkflowCapabilityEndpoints.HandleChat(
            http,
            new ChatInput { Prompt = "hello" },
            interactionService,
            CancellationToken.None);

        var body = await ReadBodyAsync(http.Response);
        http.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        body.Should().Contain("WORKFLOW_REVISION_INCOMPATIBLE");
        body.Should().Contain("Re-publish or migrate the workflow/service revision");
        body.Should().NotContain("EXECUTION_FAILED");
    }

    [Fact]
    public async Task HandleResume_ShouldRejectMissingFields()
    {
        var service = new RecordingDispatchService<WorkflowResumeCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>();
        var result = await WorkflowCapabilityEndpoints.HandleResume(
            new WorkflowResumeInput
            {
                ActorId = "",
                RunId = "run-1",
                StepId = "step-1",
            },
            service,
            ct: CancellationToken.None);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);
        var body = await ReadBodyAsync(http.Response);

        http.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        body.Should().Contain("actorId, runId and stepId are required");
    }

    [Fact]
    public async Task HandleResume_ShouldReturnNotFound_WhenActorMissing()
    {
        var service = new RecordingDispatchService<WorkflowResumeCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>
        {
            Result = CommandDispatchResult<WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>.Failure(
                WorkflowRunControlStartError.ActorNotFound("actor-404", "run-1")),
        };
        var result = await WorkflowCapabilityEndpoints.HandleResume(
            new WorkflowResumeInput
            {
                ActorId = "actor-404",
                RunId = "run-1",
                StepId = "step-1",
            },
            service,
            ct: CancellationToken.None);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);
        var body = await ReadBodyAsync(http.Response);

        http.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        body.Should().Contain("Actor 'actor-404' not found");
    }

    [Fact]
    public async Task HandleResume_ShouldDispatchCommand_WhenActorIsWorkflowRun()
    {
        var service = new RecordingDispatchService<WorkflowResumeCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>
        {
            Result = CommandDispatchResult<WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>.Success(
                new WorkflowRunControlAcceptedReceipt("actor-1", "run-1", "cmd-1", "cmd-1")),
        };

        var result = await WorkflowCapabilityEndpoints.HandleResume(
            new WorkflowResumeInput
            {
                ActorId = "actor-1",
                RunId = "run-1",
                StepId = "step-1",
                CommandId = "cmd-1",
                Approved = true,
                UserInput = "approved",
                EditedContent = "approved edited",
                Feedback = "looks good",
                Metadata = new Dictionary<string, string>
                {
                    ["source"] = "host",
                },
            },
            service,
            ct: CancellationToken.None);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        http.Response.Headers.Location.ToString().Should().Be("/api/workflow-actors/actor-1/current-state");
        var body = await ReadBodyAsync(http.Response);
        body.Should().Contain("\"acceptedCommandId\":\"cmd-1\"");
        body.Should().Contain("\"statusUrl\":\"/api/workflow-actors/actor-1/current-state\"");
        service.Commands.Should().ContainSingle();
        service.Commands.Single().ActorId.Should().Be("actor-1");
        service.Commands.Single().RunId.Should().Be("run-1");
        service.Commands.Single().StepId.Should().Be("step-1");
        service.Commands.Single().CommandId.Should().Be("cmd-1");
        service.Commands.Single().Approved.Should().BeTrue();
        service.Commands.Single().UserInput.Should().Be("approved");
        service.Commands.Single().EditedContent.Should().Be("approved edited");
        service.Commands.Single().Feedback.Should().Be("looks good");
        service.Commands.Single().Metadata.Should().ContainKey("source").WhoseValue.Should().Be("host");
    }

    [Fact]
    public async Task HandleResume_ShouldDispatchNestedToolApproval()
    {
        var service = new RecordingDispatchService<WorkflowResumeCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>
        {
            Result = CommandDispatchResult<WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>.Success(
                new WorkflowRunControlAcceptedReceipt("actor-1", "run-1", "cmd-1", "cmd-1")),
        };

        var result = await WorkflowCapabilityEndpoints.HandleResume(
            new WorkflowResumeInput
            {
                ActorId = "actor-1",
                RunId = "run-1",
                StepId = "tool-step",
                Approved = true,
                ToolApproval = new WorkflowToolApprovalResumeInput
                {
                    ExecutionId = "exec-1",
                    ToolCallId = "tool-call-1",
                    ApprovalRequestId = "approval-1",
                },
            },
            service,
            ct: CancellationToken.None);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        service.Commands.Should().ContainSingle();
        var command = service.Commands.Single();
        command.ToolApproval.Should().NotBeNull();
        command.ToolApproval!.ExecutionId.Should().Be("exec-1");
        command.ToolApproval.ToolCallId.Should().Be("tool-call-1");
        command.ToolApproval.ApprovalRequestId.Should().Be("approval-1");
    }

    [Fact]
    public async Task HandleResume_ShouldTreatActorIdAsOpaqueAndForwardItUnchanged()
    {
        const string opaqueActorId = "static-gagent:script-runtime:mixed-shape";
        var service = new RecordingDispatchService<WorkflowResumeCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>
        {
            Result = CommandDispatchResult<WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>.Success(
                new WorkflowRunControlAcceptedReceipt(opaqueActorId, "run-1", "cmd-1", "cmd-1")),
        };

        var result = await WorkflowCapabilityEndpoints.HandleResume(
            new WorkflowResumeInput
            {
                ActorId = opaqueActorId,
                RunId = "run-1",
                StepId = "step-1",
            },
            service,
            ct: CancellationToken.None);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        http.Response.Headers.Location.ToString().Should().Be($"/api/workflow-actors/{Uri.EscapeDataString(opaqueActorId)}/current-state");
        var body = await ReadBodyAsync(http.Response);
        body.Should().Contain("\"acceptedCommandId\":\"cmd-1\"");
        service.Commands.Should().ContainSingle();
        service.Commands.Single().ActorId.Should().Be(opaqueActorId);
    }

    [Fact]
    public async Task HandleResume_ShouldRejectMismatchedRunId()
    {
        var service = new RecordingDispatchService<WorkflowResumeCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>
        {
            Result = CommandDispatchResult<WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>.Failure(
                WorkflowRunControlStartError.RunBindingMismatch("actor-1", "run-other", "run-expected")),
        };

        var result = await WorkflowCapabilityEndpoints.HandleResume(
            new WorkflowResumeInput
            {
                ActorId = "actor-1",
                RunId = "run-other",
                StepId = "step-1",
            },
            service,
            ct: CancellationToken.None);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);
        var body = await ReadBodyAsync(http.Response);

        http.Response.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        body.Should().Contain("run-expected");
        service.Commands.Should().ContainSingle();
    }

    [Fact]
    public async Task HandleResume_ShouldMapInvalidStepId_FromApplicationLayer()
    {
        var service = new RecordingDispatchService<WorkflowResumeCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>
        {
            Result = CommandDispatchResult<WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>.Failure(
                WorkflowRunControlStartError.InvalidStepId("actor-1", "run-1", " ")),
        };

        var result = await WorkflowCapabilityEndpoints.HandleResume(
            new WorkflowResumeInput
            {
                ActorId = "actor-1",
                RunId = "run-1",
                StepId = "step-1",
            },
            service,
            ct: CancellationToken.None);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);
        var body = await ReadBodyAsync(http.Response);

        http.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        body.Should().Contain("stepId is required");
    }

    [Fact]
    public async Task HandleSignal_ShouldRejectNonRunActor()
    {
        var service = new RecordingDispatchService<WorkflowSignalCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>
        {
            Result = CommandDispatchResult<WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>.Failure(
                WorkflowRunControlStartError.ActorNotWorkflowRun("actor-1", "run-1")),
        };

        var result = await WorkflowCapabilityEndpoints.HandleSignal(
            new WorkflowSignalInput
            {
                ActorId = "actor-1",
                RunId = "run-1",
                SignalName = "approve",
            },
            service,
            CancellationToken.None);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);
        var body = await ReadBodyAsync(http.Response);

        http.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        body.Should().Contain("not a workflow run actor");
        service.Commands.Should().ContainSingle();
    }

    [Fact]
    public async Task HandleSignal_ShouldRejectMissingFields()
    {
        var service = new RecordingDispatchService<WorkflowSignalCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>();
        var result = await WorkflowCapabilityEndpoints.HandleSignal(
            new WorkflowSignalInput
            {
                ActorId = "actor-1",
                RunId = "",
                SignalName = "approve",
            },
            service,
            CancellationToken.None);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);
        var body = await ReadBodyAsync(http.Response);

        http.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        body.Should().Contain("actorId, runId and signalName are required");
    }

    [Fact]
    public async Task HandleSignal_ShouldRejectMismatchedRunId()
    {
        var service = new RecordingDispatchService<WorkflowSignalCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>
        {
            Result = CommandDispatchResult<WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>.Failure(
                WorkflowRunControlStartError.RunBindingMismatch("actor-1", "run-other", "run-expected")),
        };

        var result = await WorkflowCapabilityEndpoints.HandleSignal(
            new WorkflowSignalInput
            {
                ActorId = "actor-1",
                RunId = "run-other",
                SignalName = "approve",
            },
            service,
            CancellationToken.None);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);
        var body = await ReadBodyAsync(http.Response);

        http.Response.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        body.Should().Contain("run-expected");
        service.Commands.Should().ContainSingle();
    }

    [Fact]
    public async Task HandleSignal_ShouldMapInvalidSignalName_FromApplicationLayer()
    {
        var service = new RecordingDispatchService<WorkflowSignalCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>
        {
            Result = CommandDispatchResult<WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>.Failure(
                WorkflowRunControlStartError.InvalidSignalName("actor-1", "run-1", " ")),
        };

        var result = await WorkflowCapabilityEndpoints.HandleSignal(
            new WorkflowSignalInput
            {
                ActorId = "actor-1",
                RunId = "run-1",
                SignalName = "approve",
            },
            service,
            CancellationToken.None);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);
        var body = await ReadBodyAsync(http.Response);

        http.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        body.Should().Contain("signalName is required");
    }

    [Fact]
    public async Task HandleSignal_ShouldForwardStepId_WhenProvided()
    {
        var receipt = new WorkflowRunControlAcceptedReceipt("actor-1", "run-1", "signal-cmd-1", "corr-1");
        var service = new RecordingDispatchService<WorkflowSignalCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>
        {
            Result = CommandDispatchResult<WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>.Success(receipt),
        };

        var result = await WorkflowCapabilityEndpoints.HandleSignal(
            new WorkflowSignalInput
            {
                ActorId = "actor-1",
                RunId = "run-1",
                StepId = "wait-approval",
                SignalName = "approval",
                Payload = "approved",
                CommandId = "signal-cmd-1",
            },
            service,
            CancellationToken.None);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);
        var body = await ReadBodyAsync(http.Response);

        http.Response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        http.Response.Headers.Location.ToString().Should().Be("/api/workflow-actors/actor-1/current-state");
        service.Commands.Should().ContainSingle();
        service.Commands.Single().ActorId.Should().Be("actor-1");
        service.Commands.Single().RunId.Should().Be("run-1");
        service.Commands.Single().SignalName.Should().Be("approval");
        service.Commands.Single().Payload.Should().Be("approved");
        service.Commands.Single().StepId.Should().Be("wait-approval");
        service.Commands.Single().CommandId.Should().Be("signal-cmd-1");
        body.Should().Contain("wait-approval");
    }

    [Fact]
    public async Task HandleSignal_ShouldDispatchCommand_AndGenerateCommandId_WhenMissing()
    {
        var receipt = new WorkflowRunControlAcceptedReceipt(
            "actor-1",
            "run-1",
            Guid.NewGuid().ToString("N"),
            Guid.NewGuid().ToString("N"));
        var service = new RecordingDispatchService<WorkflowSignalCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>
        {
            Result = CommandDispatchResult<WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>.Success(receipt),
        };

        var result = await WorkflowCapabilityEndpoints.HandleSignal(
            new WorkflowSignalInput
            {
                ActorId = "actor-1",
                RunId = "run-1",
                SignalName = "approve",
                Payload = "yes",
            },
            service,
            CancellationToken.None);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);
        var body = await ReadBodyAsync(http.Response);

        http.Response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        http.Response.Headers.Location.ToString().Should().Be("/api/workflow-actors/actor-1/current-state");
        service.Commands.Should().ContainSingle();
        service.Commands.Single().ActorId.Should().Be("actor-1");
        service.Commands.Single().RunId.Should().Be("run-1");
        service.Commands.Single().SignalName.Should().Be("approve");
        service.Commands.Single().Payload.Should().Be("yes");
        service.Commands.Single().CommandId.Should().BeNull();
        service.Commands.Single().StepId.Should().BeNull();
        body.Should().Contain($"\"acceptedCommandId\":\"{receipt.CommandId}\"");
        body.Should().Contain("\"statusUrl\":\"/api/workflow-actors/actor-1/current-state\"");
        body.Should().Contain("\"accepted\":true");
    }

    [Fact]
    public async Task HandleStop_ShouldRejectMissingFields()
    {
        var service = new RecordingDispatchService<WorkflowStopCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>();
        var result = await WorkflowCapabilityEndpoints.HandleStop(
            new WorkflowStopInput
            {
                ActorId = "actor-1",
                RunId = "",
            },
            service,
            CancellationToken.None);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);
        var body = await ReadBodyAsync(http.Response);

        http.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        body.Should().Contain("actorId and runId are required");
    }

    [Fact]
    public async Task HandleStop_ShouldMapRunBindingMismatch()
    {
        var service = new RecordingDispatchService<WorkflowStopCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>
        {
            Result = CommandDispatchResult<WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>.Failure(
                WorkflowRunControlStartError.RunBindingMismatch("actor-1", "run-other", "run-expected")),
        };

        var result = await WorkflowCapabilityEndpoints.HandleStop(
            new WorkflowStopInput
            {
                ActorId = "actor-1",
                RunId = "run-other",
            },
            service,
            CancellationToken.None);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);
        var body = await ReadBodyAsync(http.Response);

        http.Response.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        body.Should().Contain("run-expected");
        service.Commands.Should().ContainSingle();
    }

    [Fact]
    public async Task HandleStop_ShouldDispatchCommand_WhenRunOwnershipMatches()
    {
        var receipt = new WorkflowRunControlAcceptedReceipt("actor-1", "run-1", "stop-cmd-1", "corr-1");
        var service = new RecordingDispatchService<WorkflowStopCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>
        {
            Result = CommandDispatchResult<WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>.Success(receipt),
        };

        var result = await WorkflowCapabilityEndpoints.HandleStop(
            new WorkflowStopInput
            {
                ActorId = "actor-1",
                RunId = "run-1",
                CommandId = "stop-cmd-1",
                Reason = "user requested stop",
            },
            service,
            CancellationToken.None);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);
        var body = await ReadBodyAsync(http.Response);

        http.Response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        http.Response.Headers.Location.ToString().Should().Be("/api/workflow-actors/actor-1/current-state");
        service.Commands.Should().ContainSingle();
        service.Commands.Single().ActorId.Should().Be("actor-1");
        service.Commands.Single().RunId.Should().Be("run-1");
        service.Commands.Single().CommandId.Should().Be("stop-cmd-1");
        service.Commands.Single().Reason.Should().Be("user requested stop");
        body.Should().Contain("user requested stop");
        body.Should().Contain("\"acceptedCommandId\":\"stop-cmd-1\"");
        body.Should().Contain("\"statusUrl\":\"/api/workflow-actors/actor-1/current-state\"");
    }

    [Fact]
    public async Task HandleRetryCompensation_ShouldDispatchCommand_WhenRunOwnershipMatches()
    {
        var receipt = new WorkflowRunControlAcceptedReceipt("actor-1", "run-1", "retry-cmd-1", "corr-1");
        var service = new RecordingDispatchService<WorkflowRetryCompensationCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>
        {
            Result = CommandDispatchResult<WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>.Success(receipt),
        };

        var result = await WorkflowCapabilityEndpoints.HandleRetryCompensation(
            new WorkflowRetryCompensationInput
            {
                ActorId = "actor-1",
                RunId = "run-1",
                FailedCompensationStepId = " refund_payment ",
                CommandId = "retry-cmd-1",
                Reason = "operator retry",
            },
            service,
            CancellationToken.None);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);
        var body = await ReadBodyAsync(http.Response);

        http.Response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        http.Response.Headers.Location.ToString().Should().Be("/api/workflow-actors/actor-1/current-state");
        service.Commands.Should().ContainSingle();
        service.Commands.Single().ActorId.Should().Be("actor-1");
        service.Commands.Single().RunId.Should().Be("run-1");
        service.Commands.Single().FailedCompensationStepId.Should().Be("refund_payment");
        service.Commands.Single().CommandId.Should().Be("retry-cmd-1");
        service.Commands.Single().Reason.Should().Be("operator retry");
        body.Should().Contain("\"failedCompensationStepId\":\"refund_payment\"");
        body.Should().Contain("\"acceptedCommandId\":\"retry-cmd-1\"");
        body.Should().Contain("\"statusUrl\":\"/api/workflow-actors/actor-1/current-state\"");
    }

    [Fact]
    public async Task HandleRetryCompensation_ShouldRejectMissingFailedStep()
    {
        var service = new RecordingDispatchService<WorkflowRetryCompensationCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>();

        var result = await WorkflowCapabilityEndpoints.HandleRetryCompensation(
            new WorkflowRetryCompensationInput
            {
                ActorId = "actor-1",
                RunId = "run-1",
                FailedCompensationStepId = " ",
            },
            service,
            CancellationToken.None);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);
        var body = await ReadBodyAsync(http.Response);

        http.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        body.Should().Contain("actorId, runId and failedCompensationStepId are required");
        service.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleForkRun_ShouldReturnAcceptedLocationAndDispatchMappedCommand()
    {
        var service = new RecordingDispatchService<WorkflowForkRunCommand, WorkflowForkRunAcceptedReceipt, WorkflowForkRunStartError>
        {
            Result = CommandDispatchResult<WorkflowForkRunAcceptedReceipt, WorkflowForkRunStartError>.Success(
                new WorkflowForkRunAcceptedReceipt(
                    "source-run",
                    "new-run-actor",
                    "workflow-1",
                    true,
                    "cmd-1",
                    "corr-1",
                    new DateTimeOffset(2026, 6, 8, 0, 0, 0, TimeSpan.Zero))),
        };

        var result = await WorkflowCapabilityEndpoints.HandleForkRun(
            new WorkflowForkRunInput
            {
                SourceRunId = " source-run ",
                StartAtStepId = " step-b ",
                InlineYaml = "name: workflow-1\nsteps: []",
                InlineSubYamls = new Dictionary<string, string>
                {
                    [" helper "] = "name: helper",
                },
                VariableOverrides = new Dictionary<string, string>
                {
                    [" topic "] = "recovered",
                },
                Input = "resume input",
                ScopeId = " scope-1 ",
                CommandId = " cmd-1 ",
                CorrelationId = " corr-1 ",
            },
            service,
            CreateHttpContext("Bearer trusted-token"),
            CancellationToken.None);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        http.Response.Headers.Location.ToString().Should().Be("/api/workflow-actors/new-run-actor/current-state");
        var body = await ReadBodyAsync(http.Response);
        body.Should().Contain("\"newRunActorId\":\"new-run-actor\"");
        body.Should().Contain("\"acceptedCommandId\":\"cmd-1\"");
        body.Should().Contain("\"statusUrl\":\"/api/workflow-actors/new-run-actor/current-state\"");
        service.Commands.Should().ContainSingle();
        service.Commands.Single().SourceRunId.Should().Be("source-run");
        service.Commands.Single().StartAtStepId.Should().Be("step-b");
        service.Commands.Single().InlineYaml.Should().Be("name: workflow-1\nsteps: []");
        service.Commands.Single().InlineSubYamls.Should().ContainKey("helper").WhoseValue.Should().Be("name: helper");
        service.Commands.Single().VariableOverrides.Should().ContainKey("topic").WhoseValue.Should().Be("recovered");
        service.Commands.Single().Input.Should().Be("resume input");
        service.Commands.Single().ScopeId.Should().Be("scope-1");
        service.Commands.Single().CallerCredential!.BearerToken.Should().Be("trusted-token");
        service.Commands.Single().CommandId.Should().Be("cmd-1");
        service.Commands.Single().CorrelationId.Should().Be("corr-1");
    }

    [Fact]
    public async Task HandleForkRun_ShouldRejectMalformedBearerBeforeDispatch()
    {
        var service = new RecordingDispatchService<WorkflowForkRunCommand, WorkflowForkRunAcceptedReceipt, WorkflowForkRunStartError>();

        var result = await WorkflowCapabilityEndpoints.HandleForkRun(
            new WorkflowForkRunInput
            {
                SourceRunId = "source-run",
                StartAtStepId = "step-b",
            },
            service,
            CreateHttpContext("Bearer token 123"),
            CancellationToken.None);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);
        var body = await ReadBodyAsync(http.Response);

        http.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        body.Should().Contain("INVALID_CALLER_CREDENTIAL");
        service.Commands.Should().BeEmpty();
    }

    [Theory]
    [InlineData("", "step-b")]
    [InlineData("source-run", "   ")]
    public async Task HandleForkRun_ShouldRejectMissingRequiredFields(string sourceRunId, string startAtStepId)
    {
        var service = new RecordingDispatchService<WorkflowForkRunCommand, WorkflowForkRunAcceptedReceipt, WorkflowForkRunStartError>();
        var result = await WorkflowCapabilityEndpoints.HandleForkRun(
            new WorkflowForkRunInput
            {
                SourceRunId = sourceRunId,
                StartAtStepId = startAtStepId,
            },
            service,
            null,
            CancellationToken.None);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);
        var body = await ReadBodyAsync(http.Response);

        http.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        body.Should().Contain("sourceRunId and startAtStepId are required");
        service.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleForkRun_ShouldMapStartErrorWithReason()
    {
        var service = new RecordingDispatchService<WorkflowForkRunCommand, WorkflowForkRunAcceptedReceipt, WorkflowForkRunStartError>
        {
            Result = CommandDispatchResult<WorkflowForkRunAcceptedReceipt, WorkflowForkRunStartError>.Failure(
                WorkflowForkRunStartError.StartStepNotFound("source-run", "missing-step")),
        };

        var result = await WorkflowCapabilityEndpoints.HandleForkRun(
            new WorkflowForkRunInput
            {
                SourceRunId = "source-run",
                StartAtStepId = "missing-step",
            },
            service,
            null,
            CancellationToken.None);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);
        var body = await ReadBodyAsync(http.Response);

        http.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        body.Should().Contain("Start step 'missing-step' was not found");
        service.Commands.Should().ContainSingle();
    }

    [Fact]
    public async Task HandleChatWebSocket_ShouldRejectNonWebSocketRequests()
    {
        var http = CreateHttpContext();

        await WorkflowCapabilityEndpoints.HandleChatWebSocket(
            http,
            new FakeCommandInteractionService(),
            NullLoggerFactory.Instance,
            CancellationToken.None);

        var body = await ReadBodyAsync(http.Response);
        http.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        body.Should().Contain("Expected websocket request.");
    }

    [Fact]
    public async Task HandleChatWebSocket_ShouldSendCommandError_WhenCommandParseFails()
    {
        var socket = new FakeWebSocket(WebSocketState.Open);
        socket.EnqueueReceive("""{"type":"unknown","payload":{"prompt":"hello"}}""");
        var http = CreateHttpContext();
        http.Features.Set<IHttpWebSocketFeature>(new FakeHttpWebSocketFeature(socket));

        await WorkflowCapabilityEndpoints.HandleChatWebSocket(
            http,
            new FakeCommandInteractionService(),
            NullLoggerFactory.Instance,
            CancellationToken.None);

        socket.SentTexts.Should().ContainSingle();
        socket.SentTexts[0].Should().Contain("\"type\":\"command.error\"");
        socket.SentTexts[0].Should().Contain("INVALID_COMMAND");
        socket.CloseCalls.Should().Be(1);
    }

    [Fact]
    public async Task HandleChatWebSocket_ShouldSendFailure_WhenExecutionThrows()
    {
        var socket = new FakeWebSocket(WebSocketState.Open);
        socket.EnqueueReceive("""{"type":"chat.command","requestId":"req-1","payload":{"prompt":"hello"}}""");
        var http = CreateHttpContext();
        http.Features.Set<IHttpWebSocketFeature>(new FakeHttpWebSocketFeature(socket));
        var interactionService = new FakeCommandInteractionService
        {
            ResultFactory = (_, _, _, _) => throw new InvalidOperationException("boom"),
        };

        await WorkflowCapabilityEndpoints.HandleChatWebSocket(
            http,
            interactionService,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        socket.SentTexts.Should().ContainSingle();
        socket.SentTexts[0].Should().Contain("\"type\":\"command.error\"");
        socket.SentTexts[0].Should().Contain("RUN_EXECUTION_FAILED");
        socket.CloseCalls.Should().Be(1);
    }

    private static async Task<string> ReadBodyAsync(HttpResponse response)
    {
        response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(response.Body, Encoding.UTF8, leaveOpen: true);
        return await reader.ReadToEndAsync();
    }

    private static DefaultHttpContext CreateHttpContext(string? authorization = null)
    {
        var http = new DefaultHttpContext
        {
            // 06-20-observatory-run-state-feed (R2d): HandleChat now derives the run scope from the caller
            // claim via AevatarScopeAccessGuard, which resolves auth-enablement from IConfiguration +
            // IHostEnvironment (always present in real HTTP hosting). Register both so the harness mirrors
            // production; with no authenticated user the guard yields no scope and the run falls back to the
            // body scopeId, preserving these tests' expectations.
            RequestServices = new ServiceCollection()
                .AddLogging()
                .AddOptions()
                .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
                .AddSingleton<IHostEnvironment>(new StubHostEnvironment())
                .BuildServiceProvider(),
        };
        if (!string.IsNullOrWhiteSpace(authorization))
            http.Request.Headers.Authorization = authorization;
        http.Response.Body = new MemoryStream();
        return http;
    }

    private sealed class StubHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "Aevatar.Workflow.Host.Api.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } =
            new NullFileProvider();
    }

    private static IFormFile CreateFormFile(
        string fieldName,
        string fileName,
        string contentType,
        string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, fieldName, fileName)
        {
            Headers = new HeaderDictionary(),
            ContentType = contentType,
        };
    }

    private static Dictionary<string, StringValues> ToFormFields(IDictionary<string, string> fields) =>
        fields.ToDictionary(
            static pair => pair.Key,
            static pair => new StringValues(pair.Value),
            StringComparer.Ordinal);

    private static WorkflowRunEventEnvelope BuildRawObservedWorkflowExecutionStartedFrame()
    {
        var payload = new WorkflowRunExecutionStartedEvent
        {
            RunId = "run-1",
            WorkflowName = "direct",
            Input = "hello",
            DefinitionActorId = "definition-actor-1",
        };

        return new WorkflowRunEventEnvelope
        {
            Custom = new WorkflowCustomEventPayload
            {
                Name = "aevatar.raw.observed",
                Payload = Any.Pack(new WorkflowObservedEnvelopeCustomPayload
                {
                    EventId = "evt-1",
                    PayloadTypeUrl = Any.Pack(payload).TypeUrl,
                    PublisherActorId = "definition-actor-1",
                    CorrelationId = "corr-1",
                    StateVersion = 1,
                    Payload = Any.Pack(payload),
                }),
            },
        };
    }

    private static WorkflowRunEventEnvelope BuildRawObservedWorkflowExecutionStateUpsertedFrame()
    {
        var payload = new WorkflowExecutionStateUpsertedEvent
        {
            ScopeKey = "workflow_execution_kernel",
            State = Any.Pack(new WorkflowExecutionKernelState
            {
                Active = true,
                RunId = "run-1",
                CurrentStepId = "analyze",
                CurrentStepInput = "hello",
                Variables =
                {
                    ["decision"] = "approved",
                },
            }),
        };

        return new WorkflowRunEventEnvelope
        {
            Custom = new WorkflowCustomEventPayload
            {
                Name = "aevatar.raw.observed",
                Payload = Any.Pack(new WorkflowObservedEnvelopeCustomPayload
                {
                    EventId = "evt-2",
                    PayloadTypeUrl = Any.Pack(payload).TypeUrl,
                    PublisherActorId = "workflow-run-actor-1",
                    CorrelationId = "corr-1",
                    StateVersion = 2,
                    Payload = Any.Pack(payload),
                }),
            },
        };
    }

    private sealed class FakeCommandInteractionService : IWorkflowChatRunInteractionPort
    {
        public Func<WorkflowChatRunRequest, Func<WorkflowRunEventEnvelope, CancellationToken, ValueTask>, Func<WorkflowChatRunAcceptedReceipt, CancellationToken, ValueTask>?, CancellationToken, Task<CommandInteractionResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowProjectionCompletionStatus>>> ResultFactory { get; set; } =
            (_, _, _, _) => Task.FromResult(
                CommandInteractionResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowProjectionCompletionStatus>
                    .Failure(WorkflowChatRunStartError.AgentNotFound));

        public Task<CommandInteractionResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowProjectionCompletionStatus>> ExecuteAsync(
            WorkflowChatRunRequest request,
            Func<WorkflowRunEventEnvelope, CancellationToken, ValueTask> emitAsync,
            Func<WorkflowChatRunAcceptedReceipt, CancellationToken, ValueTask>? onAcceptedAsync = null,
            CancellationToken ct = default) =>
            ResultFactory(request, emitAsync, onAcceptedAsync, ct);
    }

    private sealed class FakeCommandDispatchService
        : ICommandDispatchService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>
    {
        public CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError> Result { get; set; } =
            CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>.Failure(
                WorkflowChatRunStartError.AgentNotFound);

        public Exception? DispatchException { get; set; }
        public WorkflowChatRunRequest? LastCommand { get; private set; }
        public int DispatchCalls { get; private set; }

        public Task<CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>> DispatchAsync(
            WorkflowChatRunRequest command,
            CancellationToken ct = default)
        {
            LastCommand = command;
            DispatchCalls++;
            ct.ThrowIfCancellationRequested();
            if (DispatchException != null)
                throw DispatchException;
            return Task.FromResult(Result);
        }
    }

    private sealed class RecordingWorkflowFileIngressPort : IWorkflowFileIngressPort
    {
        public List<WorkflowFileIngressRequest> Requests { get; } = [];

        public ValueTask<WorkflowFileIngressResult> IngestAsync(
            WorkflowFileIngressRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return ValueTask.FromResult(new WorkflowFileIngressResult(new ApplicationWorkflowFileRef
            {
                FileId = "file-1",
                ArtifactId = "workflow-file://file-1",
                SourceKind = request.SourceKind,
                FileName = request.FileName,
                MediaType = request.MediaType,
                SizeBytes = request.Content.Length,
                Sha256 = "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824",
                CreatedAtUnixMs = 1710000000000,
                ExpiresAtUnixMs = 1710003600000,
            }));
        }
    }

    private sealed class RecordingDispatchService<TCommand, TReceipt, TError>
        : ICommandDispatchService<TCommand, TReceipt, TError>
    {
        public List<TCommand> Commands { get; } = [];

        public CommandDispatchResult<TReceipt, TError> Result { get; set; } =
            CommandDispatchResult<TReceipt, TError>.Failure(default!);

        public Exception? DispatchException { get; set; }

        public Task<CommandDispatchResult<TReceipt, TError>> DispatchAsync(
            TCommand command,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Commands.Add(command);
            if (DispatchException != null)
                throw DispatchException;
            return Task.FromResult(Result);
        }
    }

    private sealed class FakeHttpWebSocketFeature(FakeWebSocket socket) : IHttpWebSocketFeature
    {
        public bool IsWebSocketRequest => true;

        public Task<WebSocket> AcceptAsync(WebSocketAcceptContext context)
        {
            _ = context;
            return Task.FromResult<WebSocket>(socket);
        }
    }

    private sealed class FakeWebSocket : WebSocket
    {
        private readonly Queue<byte[]> _receives = new();
        private WebSocketState _state;

        public FakeWebSocket(WebSocketState state)
        {
            _state = state;
        }

        public List<string> SentTexts { get; } = [];
        public int CloseCalls { get; private set; }

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;

        public void EnqueueReceive(string text) => _receives.Enqueue(Encoding.UTF8.GetBytes(text));

        public override void Abort()
        {
            _state = WebSocketState.Aborted;
        }

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = closeStatus;
            _ = statusDescription;
            CloseCalls++;
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public override void Dispose()
        {
            _state = WebSocketState.Closed;
        }

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_receives.Count == 0)
            {
                _state = WebSocketState.CloseReceived;
                return Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));
            }

            var frame = _receives.Dequeue();
            Array.Copy(frame, 0, buffer.Array!, buffer.Offset, frame.Length);
            return Task.FromResult(new WebSocketReceiveResult(frame.Length, WebSocketMessageType.Text, true));
        }

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = messageType;
            _ = endOfMessage;
            SentTexts.Add(Encoding.UTF8.GetString(buffer.Array!, buffer.Offset, buffer.Count));
            return Task.CompletedTask;
        }
    }
}

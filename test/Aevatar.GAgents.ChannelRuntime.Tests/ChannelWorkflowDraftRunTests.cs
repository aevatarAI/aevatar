using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions.Credentials.Testing;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.AI.ToolProviders.Lark;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Abstractions.Slash;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.NyxidChat;
using Aevatar.GAgents.NyxidChat.WorkflowDraftRun;
using Aevatar.Workflow.Application.Abstractions.Runs;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class ChannelWorkflowDraftRunTests
{
    private static readonly DateTimeOffset WorkflowDraftRunNow =
        DateTimeOffset.Parse("2026-08-02T00:00:00Z");

    [Theory]
    [InlineData("/workflow run daily-greeting", "daily-greeting")]
    [InlineData("/run-workflow daily.greeting", "daily.greeting")]
    public void IntentParser_ShouldMatchRegisteredWorkflowRunSlashCommand(string text, string workflowId)
    {
        var parser = new ChannelWorkflowDraftRunIntentParser(BuildWorkflowSlashRegistry());

        var matched = parser.TryParse(text, out var intent);

        matched.Should().BeTrue();
        intent.WorkflowId.Should().Be(workflowId);
        intent.Prompt.Should().Be(text);
    }

    [Theory]
    [InlineData("please run daily-greeting workflow")]
    [InlineData("跑一下 daily_greeting 的 workflow")]
    [InlineData("run daily-greeting workflow")]
    [InlineData("/workflow run")]
    [InlineData("/workflow run daily greeting")]
    [InlineData("aevatar_start_workflow daily-greeting")]
    public void IntentParser_ShouldRejectUnregisteredNaturalLanguageOrToolNamedText(string text)
    {
        var parser = new ChannelWorkflowDraftRunIntentParser(BuildWorkflowSlashRegistry());

        parser.TryParse(text, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("missing-scope", null, "user-token-1", true, true, "scope")]
    [InlineData("missing-token", "scope-1", null, true, true, "授权")]
    [InlineData("missing-query-port", "scope-1", "user-token-1", false, true, "查询服务")]
    [InlineData("workflow-not-found", "scope-1", "user-token-1", true, false, "未找到")]
    [InlineData("workflow-actor-missing", "scope-1", "user-token-1", true, true, "actor")]
    public async Task Admission_ShouldReturnUserVisibleRejection_ForUnavailableWorkflowDraftRunInputs(
        string caseName,
        string? scopeId,
        string? userToken,
        bool registerQueryPort,
        bool workflowExists,
        string expectedText)
    {
        var workflow = workflowExists
            ? BuildWorkflowSummary(
                "scope-1",
                "daily-greeting",
                actorId: caseName == "workflow-actor-missing" ? string.Empty : "actor-daily-greeting")
            : null;
        var admission = new ChannelWorkflowDraftRunAdmission(
            new ChannelWorkflowDraftRunIntentParser(BuildWorkflowSlashRegistry()),
            registerQueryPort ? new StubScopeWorkflowQueryPort(workflow) : null);
        var activity = BuildWorkflowActivity(scopeId, userToken);
        var runtimeContext = string.IsNullOrWhiteSpace(userToken)
            ? ConversationTurnRuntimeContext.Empty
            : new ConversationTurnRuntimeContext(null, userToken);

        var result = await admission.TryAdmitAsync(
            activity,
            new ChannelBotRegistrationEntry { Id = "reg-1", ScopeId = scopeId ?? string.Empty },
            new ChannelInboundEvent
            {
                Text = "/workflow run daily-greeting",
                Platform = "lark",
                MessageId = "msg-1",
                ConversationId = "oc_group_chat_1",
            },
            runtimeContext,
            CancellationToken.None);

        result.Matched.Should().BeTrue();
        result.Request.Should().BeNull();
        result.Rejection.Should().NotBeNull();
        result.Rejection!.Text.Should().Contain(expectedText);
    }

    [Fact]
    public async Task Admission_ShouldBuildDefinitionActorSource_FromRunnableScopeWorkflowLookup()
    {
        var workflow = BuildWorkflowSummary(
            "scope-1",
            "daily-greeting",
            actorId: "scope-workflow-definition-actor-1");
        var admission = new ChannelWorkflowDraftRunAdmission(
            new ChannelWorkflowDraftRunIntentParser(BuildWorkflowSlashRegistry()),
            new StubScopeWorkflowQueryPort(workflow));

        var result = await admission.TryAdmitAsync(
            BuildWorkflowActivity("scope-1", "user-token-1"),
            new ChannelBotRegistrationEntry { Id = "reg-1", ScopeId = "scope-1" },
            new ChannelInboundEvent
            {
                Text = "/workflow run daily-greeting",
                Platform = "lark",
                MessageId = "msg-1",
                ConversationId = "oc_group_chat_1",
            },
            new ConversationTurnRuntimeContext(null, "user-token-1"),
            CancellationToken.None);

        result.Matched.Should().BeTrue();
        result.Rejection.Should().BeNull();
        result.Request.Should().NotBeNull();
        result.Request!.WorkflowSource.Kind.Should().Be(ChannelWorkflowDraftRunSourceKind.DefinitionActor);
        result.Request.WorkflowSource.ScopeId.Should().Be("scope-1");
        result.Request.WorkflowSource.WorkflowId.Should().Be("daily-greeting");
        result.Request.WorkflowSource.WorkflowName.Should().Be("daily-greeting");
        result.Request.WorkflowSource.DefinitionActorId.Should().Be("scope-workflow-definition-actor-1");
        result.Request.Headers.Should().Contain("workflow_id", "daily-greeting");
        result.Request.NyxUserAccessToken.Should().Be("user-token-1");
    }

    [Fact]
    public async Task InteractionPort_ShouldDispatchAcceptedStartToRunScopedActor()
    {
        var actorRuntime = new RecordingActorRuntime();
        var dispatch = new RecordingActorDispatchPort();
        var port = new ChannelWorkflowDraftRunInteractionPort(
            actorRuntime,
            dispatch,
            NullLogger<ChannelWorkflowDraftRunInteractionPort>.Instance,
            timeProvider: TimeProvider.System);
        var request = BuildWorkflowRequest();

        await port.DispatchAsync(request, CancellationToken.None);

        var runActorId = "channel-workflow-draft-run:workflow-draft-run-1";
        actorRuntime.Created.Should().ContainSingle().Which.Should().Be(runActorId);
        dispatch.Envelopes.Should().ContainSingle();
        dispatch.Envelopes[0].ActorId.Should().Be(runActorId);
        dispatch.Envelopes[0].Envelope.Runtime.DeliveryIdentity.OperationId.Should()
            .Be("workflow-draft-run-start:workflow-draft-run-1");
        var command = dispatch.Envelopes[0].Envelope.Payload.Unpack<ChannelWorkflowDraftRunStartRequested>();
        command.RunId.Should().Be("workflow-draft-run-1");
        command.Request.TargetActorId.Should().Be("conversation-actor-1");
        command.Request.ReplyToken.Should().Be("reply-token-1");
    }

    [Fact]
    public async Task InteractionPort_ShouldBuildWorkflowRequestAndDispatchMappedSuccessFrames()
    {
        var workflow = new RecordingWorkflowChatRunInteractionPort
        {
            Frames =
            [
                new WorkflowRunEventEnvelope
                {
                    TextMessageContent = new WorkflowTextMessageContentEventPayload
                    {
                        Delta = "hello",
                    },
                },
                new WorkflowRunEventEnvelope
                {
                    RunFinished = new WorkflowRunFinishedEventPayload
                    {
                        Result = Any.Pack(new WorkflowRunResultPayload { Output = "done" }),
                    },
                },
            ],
        };
        var dispatch = new RecordingActorDispatchPort();
        var port = CreateInteractionPort(dispatch, workflow);
        var request = BuildWorkflowRequest();

        await port.StartWorkflowInteractionAsync("channel-workflow-draft-run:workflow-draft-run-1", request, CancellationToken.None);

        var workflowRequest = await workflow.WaitForRequestAsync();
        workflowRequest.Prompt.Should().Be("/workflow run daily-greeting");
        workflowRequest.Source.Kind.Should().Be(WorkflowChatSourceKind.DefinitionActor);
        workflowRequest.Source.ActorId.Should().Be("workflow-actor-1");
        workflowRequest.Source.WorkflowName.Should().Be("daily-greeting");
        workflowRequest.SessionId.Should().Be("workflow-draft-run-1");
        workflowRequest.ScopeId.Should().Be("scope-1");
        workflowRequest.CallerCredential.Should().Be(new WorkflowCallerCredential("user-token-1"));
        workflowRequest.Headers.Should().Contain("registration_id", "reg-1");
        workflowRequest.Metadata.Should().Contain("channel.registration_id", "reg-1");
        workflowRequest.Metadata.Should().Contain("channel.correlation_id", "msg-1");
        workflowRequest.CommandIdSeed.Should().Be("workflow-draft-run-1");
        workflowRequest.CorrelationIdSeed.Should().Be("msg-1");

        var envelopes = await dispatch.WaitForEnvelopeCountAsync(3);
        envelopes.Should().OnlyContain(x => x.ActorId == "channel-workflow-draft-run:workflow-draft-run-1");
        var text = envelopes[0].Envelope.Payload.Unpack<ChannelWorkflowDraftRunFrameObserved>();
        text.Frame.TextMessageContent.Delta.Should().Be("hello");
        text.Request.RunId.Should().Be("workflow-draft-run-1");

        var finished = envelopes[1].Envelope.Payload.Unpack<ChannelWorkflowDraftRunFrameObserved>();
        finished.Frame.RunFinished.ResultOutput.Should().Be("done");

        var completed = envelopes[2].Envelope.Payload.Unpack<ChannelWorkflowDraftRunInteractionCompleted>();
        completed.Succeeded.Should().BeTrue();
        completed.Completed.Should().BeTrue();
        completed.ErrorCode.Should().BeEmpty();
    }

    [Fact]
    public async Task InteractionPort_ShouldIngestLarkAttachmentsIntoWorkflowInputParts()
    {
        var lark = Substitute.For<ILarkNyxClient>();
        lark.DownloadMessageResourceAsync(
                "user-token-1",
                Arg.Any<LarkMessageResourceDownloadRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var resource = call.Arg<LarkMessageResourceDownloadRequest>();
                return Task.FromResult(new LarkMessageResourceDownloadResult(
                    true,
                    resource.Kind == LarkMessageResourceKind.Image ? [1, 2, 3] : [4, 5, 6, 7],
                    resource.Kind == LarkMessageResourceKind.Image ? "image/png" : "application/pdf",
                    resource.Kind == LarkMessageResourceKind.Image ? "receipt.png" : "invoice.pdf"));
            });
        var ingress = new RecordingWorkflowFileIngressPort();
        var workflow = new RecordingWorkflowChatRunInteractionPort();
        var dispatch = new RecordingActorDispatchPort();
        var port = CreateInteractionPort(dispatch, workflow, lark, ingress);
        var request = BuildWorkflowRequestWithLarkAttachments();

        await port.StartWorkflowInteractionAsync(
            "channel-workflow-draft-run:workflow-draft-run-1",
            request,
            CancellationToken.None);

        var workflowRequest = await workflow.WaitForRequestAsync();
        workflowRequest.InputParts.Should().NotBeNull();
        workflowRequest.InputParts!.Should().HaveCount(2);
        workflowRequest.InputParts[0].Kind.Should().Be(WorkflowChatInputPartKind.Image);
        workflowRequest.InputParts[0].DataBase64.Should().BeNull();
        workflowRequest.InputParts[0].FileRef.Should().NotBeNull();
        workflowRequest.InputParts[0].FileRef!.SourceKind.Should().Be(FileArtifactSourceKind.ConnectedServiceResource);
        workflowRequest.InputParts[0].FileRef!.SourceMessageId.Should().Be("om_123");
        workflowRequest.InputParts[0].FileRef!.SourceResourceKey.Should().Be("img_v3_1");
        workflowRequest.InputParts[0].FileRef!.MediaType.Should().Be("image/png");
        workflowRequest.InputParts[1].Kind.Should().Be(WorkflowChatInputPartKind.File);
        workflowRequest.InputParts[1].DataBase64.Should().BeNull();
        workflowRequest.InputParts[1].Uri.Should().NotBeNullOrWhiteSpace();
        workflowRequest.InputParts[1].FileRef.Should().NotBeNull();
        workflowRequest.InputParts[1].FileRef!.SourceKind.Should().Be(FileArtifactSourceKind.ConnectedServiceResource);
        workflowRequest.InputParts[1].FileRef!.SourceMessageId.Should().Be("om_123");
        workflowRequest.InputParts[1].FileRef!.SourceResourceKey.Should().Be("file_v3_1");
        workflowRequest.InputParts[1].FileRef!.MediaType.Should().Be("application/pdf");

        ingress.Requests.Should().HaveCount(2);
        ingress.Requests[0].Content.ToArray().Should().Equal(1, 2, 3);
        ingress.Requests[0].SourceMessageId.Should().Be("om_123");
        ingress.Requests[0].SourceResourceKey.Should().Be("img_v3_1");
        ingress.Requests[0].OwnerRunId.Should().BeNull();
        ingress.Requests[0].OwnerScopeId.Should().Be("scope-1");
        ingress.Requests[1].Content.ToArray().Should().Equal(4, 5, 6, 7);
        ingress.Requests[1].OwnerRunId.Should().BeNull();
        ingress.Requests[1].OwnerScopeId.Should().Be("scope-1");
        await lark.Received(1).DownloadMessageResourceAsync(
            "user-token-1",
            Arg.Is<LarkMessageResourceDownloadRequest>(x =>
                x.MessageId == "om_123" &&
                x.ResourceKey == "img_v3_1" &&
                x.Kind == LarkMessageResourceKind.Image),
            Arg.Any<CancellationToken>());
        await lark.Received(1).DownloadMessageResourceAsync(
            "user-token-1",
            Arg.Is<LarkMessageResourceDownloadRequest>(x =>
                x.MessageId == "om_123" &&
                x.ResourceKey == "file_v3_1" &&
                x.Kind == LarkMessageResourceKind.File),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InteractionPort_ShouldNormalizeLarkResourceUrlAttachmentIds()
    {
        var lark = Substitute.For<ILarkNyxClient>();
        lark.DownloadMessageResourceAsync(
                "user-token-1",
                Arg.Any<LarkMessageResourceDownloadRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new LarkMessageResourceDownloadResult(
                true,
                [4, 5, 6, 7],
                "application/pdf",
                "invoice.pdf")));
        var ingress = new RecordingWorkflowFileIngressPort();
        var workflow = new RecordingWorkflowChatRunInteractionPort();
        var dispatch = new RecordingActorDispatchPort();
        var port = CreateInteractionPort(dispatch, workflow, lark, ingress);
        var request = BuildWorkflowRequestWithLarkAttachments();
        request.Activity.Content.Attachments.Clear();
        request.Activity.Content.Attachments.Add(new AttachmentRef
        {
            AttachmentId = "https://open.larksuite.com/open-apis/im/v1/messages/om_123/resources/file_v3_1?type=file",
            ExternalUrl = "https://open.larksuite.com/open-apis/im/v1/messages/om_123/resources/file_v3_1?type=file",
            Kind = AttachmentKind.File,
            Name = "invoice.pdf",
            ContentType = "file",
        });

        await port.StartWorkflowInteractionAsync(
            "channel-workflow-draft-run:workflow-draft-run-1",
            request,
            CancellationToken.None);

        var workflowRequest = await workflow.WaitForRequestAsync();
        workflowRequest.InputParts.Should().ContainSingle();
        workflowRequest.InputParts![0].FileRef.Should().NotBeNull();
        workflowRequest.InputParts[0].FileRef!.SourceResourceKey.Should().Be("file_v3_1");
        ingress.Requests.Should().ContainSingle().Which.SourceResourceKey.Should().Be("file_v3_1");
        await lark.Received(1).DownloadMessageResourceAsync(
            "user-token-1",
            Arg.Is<LarkMessageResourceDownloadRequest>(x =>
                x.MessageId == "om_123" &&
                x.ResourceKey == "file_v3_1" &&
                x.Kind == LarkMessageResourceKind.File),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InteractionPort_ShouldRouteLarkAttachmentDownloadsThroughInboundProviderSlug()
    {
        var defaultLark = Substitute.For<ILarkNyxClient>();
        var inboundLark = Substitute.For<ILarkNyxClient>();
        inboundLark.DownloadMessageResourceAsync(
                "user-token-1",
                Arg.Any<LarkMessageResourceDownloadRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new LarkMessageResourceDownloadResult(
                true,
                [1, 2, 3],
                "image/png",
                "receipt.png")));
        var clientFactory = Substitute.For<ILarkOutboundClientFactory>();
        clientFactory.ResolveNyxClient("api-lark-bot-4").Returns(inboundLark);

        var ingress = new RecordingWorkflowFileIngressPort();
        var workflow = new RecordingWorkflowChatRunInteractionPort();
        var dispatch = new RecordingActorDispatchPort();
        var port = CreateInteractionPort(
            dispatch,
            workflow,
            defaultLark,
            ingress,
            clientFactory);
        var request = BuildWorkflowRequestWithLarkAttachments();
        request.Activity.Content.Attachments.RemoveAt(1);
        request.Activity.TransportExtras.NyxProviderSlug = " api-lark-bot-4 ";

        await port.StartWorkflowInteractionAsync(
            "channel-workflow-draft-run:workflow-draft-run-1",
            request,
            CancellationToken.None);

        var workflowRequest = await workflow.WaitForRequestAsync();
        workflowRequest.InputParts.Should().ContainSingle();
        ingress.Requests.Should().ContainSingle();
        clientFactory.Received(1).ResolveNyxClient("api-lark-bot-4");
        await inboundLark.Received(1).DownloadMessageResourceAsync(
            "user-token-1",
            Arg.Is<LarkMessageResourceDownloadRequest>(x =>
                x.MessageId == "om_123" &&
                x.ResourceKey == "img_v3_1" &&
                x.Kind == LarkMessageResourceKind.Image),
            Arg.Any<CancellationToken>());
        await defaultLark.DidNotReceiveWithAnyArgs().DownloadMessageResourceAsync(default!, default!, default);
    }

    [Fact]
    public async Task InteractionPort_ShouldUseDefaultLarkClient_WhenInboundProviderSlugIsMissing()
    {
        var defaultLark = Substitute.For<ILarkNyxClient>();
        defaultLark.DownloadMessageResourceAsync(
                "user-token-1",
                Arg.Any<LarkMessageResourceDownloadRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new LarkMessageResourceDownloadResult(
                true,
                [1, 2, 3],
                "image/png",
                "receipt.png")));
        var clientFactory = Substitute.For<ILarkOutboundClientFactory>();

        var ingress = new RecordingWorkflowFileIngressPort();
        var workflow = new RecordingWorkflowChatRunInteractionPort();
        var dispatch = new RecordingActorDispatchPort();
        var port = CreateInteractionPort(
            dispatch,
            workflow,
            defaultLark,
            ingress,
            clientFactory);
        var request = BuildWorkflowRequestWithLarkAttachments();
        request.Activity.Content.Attachments.RemoveAt(1);
        request.Activity.TransportExtras.NyxProviderSlug = " ";

        await port.StartWorkflowInteractionAsync(
            "channel-workflow-draft-run:workflow-draft-run-1",
            request,
            CancellationToken.None);

        var workflowRequest = await workflow.WaitForRequestAsync();
        workflowRequest.InputParts.Should().ContainSingle();
        ingress.Requests.Should().ContainSingle();
        clientFactory.DidNotReceiveWithAnyArgs().ResolveNyxClient(default);
        await defaultLark.Received(1).DownloadMessageResourceAsync(
            "user-token-1",
            Arg.Is<LarkMessageResourceDownloadRequest>(x =>
                x.MessageId == "om_123" &&
                x.ResourceKey == "img_v3_1" &&
                x.Kind == LarkMessageResourceKind.Image),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InteractionPort_ShouldFailClosed_WhenLarkAttachmentIngressPortIsMissing()
    {
        var lark = Substitute.For<ILarkNyxClient>();
        var workflow = new RecordingWorkflowChatRunInteractionPort();
        var dispatch = new RecordingActorDispatchPort();
        var port = CreateInteractionPort(dispatch, workflow, lark, fileIngressPort: null);

        await port.StartWorkflowInteractionAsync(
            "channel-workflow-draft-run:workflow-draft-run-1",
            BuildWorkflowRequestWithLarkAttachments(),
            CancellationToken.None);

        var completion = await dispatch.WaitForSinglePayloadAsync<ChannelWorkflowDraftRunInteractionCompleted>();
        completion.Succeeded.Should().BeFalse();
        completion.Completed.Should().BeFalse();
        completion.ErrorCode.Should().Be("workflow_attachment_ingress_failed");
        workflow.HasRequest.Should().BeFalse();
        await lark.DidNotReceiveWithAnyArgs().DownloadMessageResourceAsync(default!, default!, default);
    }

    [Fact]
    public async Task InteractionPort_ShouldFailClosed_WhenLarkAttachmentDownloadFails()
    {
        var lark = Substitute.For<ILarkNyxClient>();
        lark.DownloadMessageResourceAsync(
                "user-token-1",
                Arg.Any<LarkMessageResourceDownloadRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new LarkMessageResourceDownloadResult(
                false,
                [],
                Detail: "download denied",
                HttpStatus: 403)));
        var workflow = new RecordingWorkflowChatRunInteractionPort();
        var dispatch = new RecordingActorDispatchPort();
        var port = CreateInteractionPort(
            dispatch,
            workflow,
            lark,
            new RecordingWorkflowFileIngressPort());

        await port.StartWorkflowInteractionAsync(
            "channel-workflow-draft-run:workflow-draft-run-1",
            BuildWorkflowRequestWithLarkAttachments(),
            CancellationToken.None);

        var completion = await dispatch.WaitForSinglePayloadAsync<ChannelWorkflowDraftRunInteractionCompleted>();
        completion.Succeeded.Should().BeFalse();
        completion.Completed.Should().BeFalse();
        completion.ErrorCode.Should().Be("workflow_attachment_ingress_failed");
        workflow.HasRequest.Should().BeFalse();
    }

    [Fact]
    public async Task InteractionPort_ShouldIgnoreNonLarkAttachmentRefs()
    {
        var lark = Substitute.For<ILarkNyxClient>();
        var ingress = new RecordingWorkflowFileIngressPort();
        var workflow = new RecordingWorkflowChatRunInteractionPort();
        var dispatch = new RecordingActorDispatchPort();
        var port = CreateInteractionPort(dispatch, workflow, lark, ingress);
        var request = BuildWorkflowRequestWithLarkAttachments();
        request.Activity.ChannelId = ChannelId.From("telegram");
        request.Activity.TransportExtras.NyxPlatform = "telegram";

        await port.StartWorkflowInteractionAsync(
            "channel-workflow-draft-run:workflow-draft-run-1",
            request,
            CancellationToken.None);

        var workflowRequest = await workflow.WaitForRequestAsync();
        workflowRequest.InputParts.Should().BeNull();
        ingress.Requests.Should().BeEmpty();
        await lark.DidNotReceiveWithAnyArgs().DownloadMessageResourceAsync(default!, default!, default);
    }

    [Fact]
    public async Task InteractionPort_ShouldMapWorkflowErrorAndStoppedFrames()
    {
        var workflow = new RecordingWorkflowChatRunInteractionPort
        {
            Frames =
            [
                new WorkflowRunEventEnvelope
                {
                    RunError = new WorkflowRunErrorEventPayload
                    {
                        Message = "boom",
                        Code = "bad_input",
                    },
                },
                new WorkflowRunEventEnvelope
                {
                    RunStopped = new WorkflowRunStoppedEventPayload
                    {
                        Reason = "user canceled",
                    },
                },
            ],
        };
        var dispatch = new RecordingActorDispatchPort();
        var port = CreateInteractionPort(dispatch, workflow);

        await port.StartWorkflowInteractionAsync(
            "channel-workflow-draft-run:workflow-draft-run-1",
            BuildWorkflowRequest(),
            CancellationToken.None);

        await workflow.WaitForRequestAsync();
        var envelopes = await dispatch.WaitForEnvelopeCountAsync(3);
        var error = envelopes[0].Envelope.Payload.Unpack<ChannelWorkflowDraftRunFrameObserved>();
        error.Frame.RunError.Message.Should().Be("boom");
        error.Frame.RunError.Code.Should().Be("bad_input");

        var stopped = envelopes[1].Envelope.Payload.Unpack<ChannelWorkflowDraftRunFrameObserved>();
        stopped.Frame.RunStopped.Reason.Should().Be("user canceled");
    }

    [Fact]
    public async Task InteractionPort_ShouldDispatchStartFailureCompletion()
    {
        var workflow = new RecordingWorkflowChatRunInteractionPort
        {
            Result = WorkflowChatRunInteractionResult
                .Failure(WorkflowChatRunStartError.WorkflowNotFound),
        };
        var dispatch = new RecordingActorDispatchPort();
        var port = CreateInteractionPort(dispatch, workflow);

        await port.StartWorkflowInteractionAsync(
            "channel-workflow-draft-run:workflow-draft-run-1",
            BuildWorkflowRequest(),
            CancellationToken.None);

        await workflow.WaitForRequestAsync();
        var completion = await dispatch.WaitForSinglePayloadAsync<ChannelWorkflowDraftRunInteractionCompleted>();
        completion.Succeeded.Should().BeFalse();
        completion.Completed.Should().BeFalse();
        completion.ErrorCode.Should().Be("workflow_start_failed:WorkflowNotFound");
        completion.ErrorSummary.Should().Be("Workflow start failed: WorkflowNotFound");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InteractionPort_ShouldDispatchUnknownCompletion_WhenFinalizeResultIsMissingOrIncomplete(
        bool missingFinalizeResult)
    {
        var receipt = new WorkflowChatRunAcceptedReceipt(
            "workflow-actor-1",
            "daily-greeting",
            "workflow-draft-run-1",
            "msg-1");
        var workflow = new RecordingWorkflowChatRunInteractionPort
        {
            Result = missingFinalizeResult
                ? new WorkflowChatRunInteractionResult
                {
                    Succeeded = true,
                    Error = WorkflowChatRunStartError.None,
                    Receipt = receipt,
                    Completion = WorkflowProjectionCompletionStatus.Unknown,
                    Completed = false,
                    FinalizeResult = null,
                }
                : WorkflowChatRunInteractionResult
                    .Success(
                        receipt,
                        new CommandInteractionFinalizeResult<WorkflowProjectionCompletionStatus>(
                            WorkflowProjectionCompletionStatus.Unknown,
                            false)),
        };
        var dispatch = new RecordingActorDispatchPort();
        var port = CreateInteractionPort(dispatch, workflow);

        await port.StartWorkflowInteractionAsync(
            "channel-workflow-draft-run:workflow-draft-run-1",
            BuildWorkflowRequest(),
            CancellationToken.None);

        await workflow.WaitForRequestAsync();
        var completion = await dispatch.WaitForSinglePayloadAsync<ChannelWorkflowDraftRunInteractionCompleted>();
        completion.Succeeded.Should().BeTrue();
        completion.Completed.Should().BeFalse();
        completion.ErrorCode.Should().Be("workflow_completion_unknown");
        completion.ErrorSummary.Should().Be("Workflow ended without a terminal frame.");
    }

    [Fact]
    public async Task InteractionPort_ShouldDispatchExceptionCompletion()
    {
        var workflow = new RecordingWorkflowChatRunInteractionPort
        {
            Exception = new InvalidOperationException("workflow execution failed"),
        };
        var dispatch = new RecordingActorDispatchPort();
        var port = CreateInteractionPort(dispatch, workflow);

        await port.StartWorkflowInteractionAsync(
            "channel-workflow-draft-run:workflow-draft-run-1",
            BuildWorkflowRequest(),
            CancellationToken.None);

        await workflow.WaitForRequestAsync();
        var completion = await dispatch.WaitForSinglePayloadAsync<ChannelWorkflowDraftRunInteractionCompleted>();
        completion.Succeeded.Should().BeFalse();
        completion.Completed.Should().BeFalse();
        completion.ErrorCode.Should().Be("workflow_draft_run_exception");
        completion.ErrorSummary.Should().Be("Workflow draft-run failed.");
    }

    [Fact]
    public async Task WorkflowDraftRunGAgent_ShouldBuildWorkflowRequestAndRenderFramesIntoConversationCarriers()
    {
        var workflow = new RecordingWorkflowDraftRunInteractionPort();
        var dispatch = new RecordingActorDispatchPort();
        var agent = await CreateWorkflowDraftRunAgentAsync(
            dispatch,
            workflow);
        var request = BuildWorkflowRequest();

        await agent.HandleStartAsync(new ChannelWorkflowDraftRunStartRequested
        {
            Request = request,
            RunId = request.RunId,
        });

        workflow.Started.Should().ContainSingle();
        workflow.Started[0].RunActorId.Should().Be("channel-workflow-draft-run:workflow-draft-run-1");
        workflow.Started[0].Request.RunId.Should().Be("workflow-draft-run-1");
        workflow.Started[0].Request.Prompt.Should().Be("/workflow run daily-greeting");
        workflow.Started[0].Request.WorkflowSource.DefinitionActorId.Should().Be("workflow-actor-1");
        workflow.Started[0].Request.Headers["registration_id"].Should().Be("reg-1");
        dispatch.Envelopes.Should().BeEmpty();
        agent.State.Status.Should().Be(ChannelWorkflowDraftRunStatus.Started);

        await agent.HandleWorkflowFrameObservedAsync(BuildFrameObserved(
            request,
            new ChannelWorkflowDraftRunFrame
            {
                TextMessageContent = new ChannelWorkflowDraftRunTextMessageContentFrame
                {
                    Delta = "hello",
                },
            }));
        await agent.HandleWorkflowFrameObservedAsync(BuildFrameObserved(
            request,
            new ChannelWorkflowDraftRunFrame
            {
                TextMessageContent = new ChannelWorkflowDraftRunTextMessageContentFrame
                {
                    Delta = " world",
                },
            }));
        await agent.HandleWorkflowFrameObservedAsync(BuildFrameObserved(
            request,
            new ChannelWorkflowDraftRunFrame
            {
                RunFinished = new ChannelWorkflowDraftRunFinishedFrame(),
            }));
        await agent.HandleWorkflowInteractionCompletedAsync(new ChannelWorkflowDraftRunInteractionCompleted
        {
            Request = request.Clone(),
            Succeeded = true,
            Completed = true,
        });

        dispatch.Envelopes.Should().HaveCount(3);
        dispatch.Envelopes[0].ActorId.Should().Be("conversation-actor-1");
        var firstChunk = dispatch.Envelopes[0].Envelope.Payload.Unpack<LlmReplyStreamChunkEvent>();
        firstChunk.AccumulatedText.Should().Be("hello");
        firstChunk.ReplyToken.Should().Be("reply-token-1");

        var secondChunk = dispatch.Envelopes[1].Envelope.Payload.Unpack<LlmReplyStreamChunkEvent>();
        secondChunk.AccumulatedText.Should().Be("hello world");

        var ready = dispatch.Envelopes[2].Envelope.Payload.Unpack<LlmReplyReadyEvent>();
        ready.RunId.Should().Be("workflow-draft-run-1");
        ready.TerminalState.Should().Be(LlmReplyTerminalState.Completed);
        ready.Outbound.Text.Should().Be("hello world");
        ready.ReplyToken.Should().BeEmpty();
        ready.RelayReplyTokenRef.Ref.Should().NotBeNullOrWhiteSpace();

        agent.State.Status.Should().Be(ChannelWorkflowDraftRunStatus.ReplyHandedOff);
        agent.State.RunId.Should().Be("workflow-draft-run-1");
        agent.State.AccumulatedText.Should().Be("hello world");
    }

    [Fact]
    public async Task WorkflowDraftRunGAgent_ShouldRepairTimeoutWithoutDispatchingDuplicateInFlightStart()
    {
        var workflow = new RecordingWorkflowDraftRunInteractionPort();
        var dispatch = new RecordingActorDispatchPort();
        var scheduler = new RecordingCallbackScheduler();
        var agent = await CreateWorkflowDraftRunAgentAsync(
            dispatch,
            workflow,
            callbackScheduler: scheduler,
            timeProvider: new FakeTimeProvider(WorkflowDraftRunNow));
        var request = BuildWorkflowRequest();

        await agent.HandleStartAsync(new ChannelWorkflowDraftRunStartRequested
        {
            Request = request.Clone(),
            RunId = request.RunId,
        });
        await agent.HandleStartAsync(new ChannelWorkflowDraftRunStartRequested
        {
            Request = request.Clone(),
            RunId = request.RunId,
        });

        workflow.Started.Should().ContainSingle();
        scheduler.Timeouts.Should().HaveCount(2);
        scheduler.Timeouts.Select(x => x.CallbackId).Distinct().Should().ContainSingle();
        dispatch.Envelopes.Should().BeEmpty();
        agent.State.Status.Should().Be(ChannelWorkflowDraftRunStatus.Started);
        agent.State.RunId.Should().Be("workflow-draft-run-1");
        agent.State.CorrelationId.Should().Be("msg-1");
    }

    [Fact]
    public async Task WorkflowDraftRunGAgent_ShouldFailRehydratedStartedRunAfterDurableRecoveryTimeout()
    {
        var eventStore = new InMemoryEventStore();
        var timeProvider = new FakeTimeProvider(WorkflowDraftRunNow);
        var firstScheduler = new RecordingCallbackScheduler();
        var firstWorkflow = new RecordingWorkflowDraftRunInteractionPort();
        var firstDispatch = new RecordingActorDispatchPort();
        var firstAgent = await CreateWorkflowDraftRunAgentAsync(
            firstDispatch,
            firstWorkflow,
            eventStore,
            firstScheduler,
            timeProvider);
        var request = BuildWorkflowRequest();
        request.Activity.TransportExtras = new TransportExtras
        {
            NyxUserAccessToken = "activity-user-token",
        };

        await firstAgent.HandleStartAsync(new ChannelWorkflowDraftRunStartRequested
        {
            Request = request.Clone(),
            RunId = request.RunId,
        });

        firstAgent.State.Status.Should().Be(ChannelWorkflowDraftRunStatus.Started);
        firstWorkflow.Started.Should().ContainSingle();
        firstScheduler.Timeouts.Should().ContainSingle();
        firstAgent.State.RecoveryRequest.ReplyToken.Should().BeEmpty();
        firstAgent.State.RecoveryRequest.ReplyTokenExpiresAtUnixMs.Should().Be(0);
        firstAgent.State.RecoveryRequest.NyxUserAccessToken.Should().BeEmpty();
        firstAgent.State.RecoveryRequest.Activity.TransportExtras.NyxUserAccessToken.Should().BeEmpty();

        var startedEvents = (await eventStore.GetEventsAsync(firstAgent.Id))
            .Where(x => x.EventData.Is(ChannelWorkflowDraftRunStartedEvent.Descriptor))
            .Select(x => x.EventData.Unpack<ChannelWorkflowDraftRunStartedEvent>())
            .ToArray();
        startedEvents.Should().ContainSingle();
        startedEvents[0].RecoveryRequest.ReplyToken.Should().BeEmpty();
        startedEvents[0].RecoveryRequest.NyxUserAccessToken.Should().BeEmpty();

        var rehydratedScheduler = new RecordingCallbackScheduler();
        var rehydratedWorkflow = new RecordingWorkflowDraftRunInteractionPort();
        var rehydratedDispatch = new RecordingActorDispatchPort();
        var rehydrated = await CreateWorkflowDraftRunAgentAsync(
            rehydratedDispatch,
            rehydratedWorkflow,
            eventStore,
            rehydratedScheduler,
            timeProvider);

        rehydrated.State.Status.Should().Be(ChannelWorkflowDraftRunStatus.Started);
        rehydratedWorkflow.Started.Should().BeEmpty();
        rehydratedScheduler.Timeouts.Should().ContainSingle();
        rehydratedScheduler.Timeouts[0].CallbackId.Should().Be(firstScheduler.Timeouts[0].CallbackId);
        var timeout = rehydratedScheduler.Timeouts[0]
            .TriggerEnvelope.Payload.Unpack<ChannelWorkflowDraftRunRecoveryTimeoutElapsed>();
        timeout.RunId.Should().Be(request.RunId);
        timeout.CorrelationId.Should().Be(request.CorrelationId);
        timeout.RecoveryDeadlineUnixMs.Should().Be(rehydrated.State.RecoveryDeadlineUnixMs);

        timeProvider.Advance(
            DateTimeOffset.FromUnixTimeMilliseconds(rehydrated.State.RecoveryDeadlineUnixMs) -
            timeProvider.GetUtcNow() +
            TimeSpan.FromMilliseconds(1));
        await rehydrated.HandleRecoveryTimeoutAsync(timeout);

        rehydratedDispatch.Envelopes.Should().ContainSingle();
        var terminalEnvelope = rehydratedDispatch.Envelopes[0].Envelope;
        terminalEnvelope.Id.Should().Be("workflow-draft-run-terminal:workflow-draft-run-1");
        terminalEnvelope.Runtime.DeliveryIdentity.OperationId.Should()
            .Be("workflow-draft-run-terminal:workflow-draft-run-1");
        var failure = terminalEnvelope.Payload.Unpack<LlmReplyReadyEvent>();
        failure.RunId.Should().Be(request.RunId);
        failure.TerminalState.Should().Be(LlmReplyTerminalState.Failed);
        failure.ErrorCode.Should().Be("workflow_draft_run_recovery_timeout");
        failure.ReplyToken.Should().BeEmpty();
        rehydrated.State.Status.Should().Be(ChannelWorkflowDraftRunStatus.Failed);
        rehydratedScheduler.PurgedActorIds.Should().Contain(rehydrated.Id);

        await rehydrated.HandleWorkflowInteractionCompletedAsync(new ChannelWorkflowDraftRunInteractionCompleted
        {
            Request = request.Clone(),
            Succeeded = true,
            Completed = true,
        });

        rehydratedDispatch.Envelopes.Should().ContainSingle();
        rehydratedWorkflow.Started.Should().BeEmpty();
    }

    [Fact]
    public async Task WorkflowDraftRunGAgent_ShouldReserveTerminalHandoffWindowBeforeReplyTokenExpires()
    {
        var eventStore = new InMemoryEventStore();
        var timeProvider = new FakeTimeProvider(WorkflowDraftRunNow);
        var secretClock = new ManualRuntimeSecretClock(WorkflowDraftRunNow.ToUnixTimeMilliseconds());
        var secretStore = new InMemoryRuntimeSecretStore(secretClock);
        var scheduler = new RecordingCallbackScheduler();
        var dispatch = new RecordingActorDispatchPort();
        var agent = await CreateWorkflowDraftRunAgentAsync(
            dispatch,
            new RecordingWorkflowDraftRunInteractionPort(),
            eventStore,
            scheduler,
            timeProvider,
            secretStore);
        var request = BuildWorkflowRequest();
        var replyTokenExpiresAt = WorkflowDraftRunNow.AddMinutes(30);
        request.ReplyTokenExpiresAtUnixMs = replyTokenExpiresAt.ToUnixTimeMilliseconds();

        await agent.HandleStartAsync(new ChannelWorkflowDraftRunStartRequested
        {
            Request = request.Clone(),
            RunId = request.RunId,
        });

        var expectedDeadline = replyTokenExpiresAt.AddMinutes(-1);
        DateTimeOffset.FromUnixTimeMilliseconds(agent.State.RecoveryDeadlineUnixMs)
            .Should().Be(expectedDeadline);
        scheduler.Timeouts.Should().ContainSingle();
        scheduler.Timeouts[0].DueTime.Should().Be(TimeSpan.FromMinutes(29));

        var timeout = scheduler.Timeouts[0]
            .TriggerEnvelope.Payload.Unpack<ChannelWorkflowDraftRunRecoveryTimeoutElapsed>();
        var elapsed = expectedDeadline - WorkflowDraftRunNow + TimeSpan.FromMilliseconds(1);
        timeProvider.Advance(elapsed);
        secretClock.Advance(elapsed);

        await agent.HandleRecoveryTimeoutAsync(timeout);

        var terminal = dispatch.Envelopes.Should().ContainSingle().Subject.Envelope.Payload
            .Unpack<LlmReplyReadyEvent>();
        terminal.RelayReplyTokenRef.ExpiresAtUnixMs.Should().Be(replyTokenExpiresAt.ToUnixTimeMilliseconds());
        var resolved = await secretStore.ResolveAsync(new ResolveRuntimeSecretRequest(
            terminal.RelayReplyTokenRef.Ref,
            "channel-relay-reply-token",
            request.RunId,
            request.CorrelationId,
            "Verify recovery terminal credential remains available at handoff."));
        resolved.Secret.Should().Be(request.ReplyToken);
    }

    [Fact]
    public async Task WorkflowDraftRunGAgent_ShouldReplayPersistedTerminalAfterDispatchAdmissionFailure()
    {
        var eventStore = new InMemoryEventStore();
        var timeProvider = new FakeTimeProvider(WorkflowDraftRunNow);
        var firstScheduler = new RecordingCallbackScheduler();
        var firstDispatch = new RecordingActorDispatchPort(failuresRemaining: 1);
        var firstAgent = await CreateWorkflowDraftRunAgentAsync(
            firstDispatch,
            new RecordingWorkflowDraftRunInteractionPort(),
            eventStore,
            firstScheduler,
            timeProvider);
        var request = BuildWorkflowRequest();

        await firstAgent.HandleStartAsync(new ChannelWorkflowDraftRunStartRequested
        {
            Request = request.Clone(),
            RunId = request.RunId,
        });
        await firstAgent.HandleWorkflowInteractionCompletedAsync(new ChannelWorkflowDraftRunInteractionCompleted
        {
            Request = request.Clone(),
            Succeeded = true,
            Completed = true,
        });

        firstAgent.State.Status.Should().Be(ChannelWorkflowDraftRunStatus.TerminalProduced);
        firstAgent.State.ProducedTerminalReply.Should().NotBeNull();
        firstAgent.State.ProducedTerminalReply.ReplyToken.Should().BeEmpty();
        firstAgent.State.TerminalOperationId.Should().Be("workflow-draft-run-terminal:workflow-draft-run-1");
        firstScheduler.PurgedActorIds.Should().BeEmpty();
        var attemptedEnvelope = firstDispatch.Envelopes.Should().ContainSingle().Subject.Envelope;
        var persistedPayload = firstAgent.State.ProducedTerminalReply.ToByteArray();

        var rehydratedScheduler = new RecordingCallbackScheduler();
        var rehydratedDispatch = new RecordingActorDispatchPort();
        var rehydratedWorkflow = new RecordingWorkflowDraftRunInteractionPort();
        var rehydrated = await CreateWorkflowDraftRunAgentAsync(
            rehydratedDispatch,
            rehydratedWorkflow,
            eventStore,
            rehydratedScheduler,
            timeProvider);

        rehydrated.State.Status.Should().Be(ChannelWorkflowDraftRunStatus.TerminalProduced);
        rehydrated.State.ProducedTerminalReply.ToByteArray().Should().Equal(persistedPayload);
        rehydratedWorkflow.Started.Should().BeEmpty();
        var retryRequest = rehydratedScheduler.Timeouts
            .Select(x => x.TriggerEnvelope.Payload)
            .Single(x => x.Is(ChannelWorkflowDraftRunTerminalHandoffRetryElapsed.Descriptor))
            .Unpack<ChannelWorkflowDraftRunTerminalHandoffRetryElapsed>();

        await rehydrated.HandleTerminalHandoffRetryAsync(retryRequest);

        rehydratedDispatch.Envelopes.Should().ContainSingle();
        var replayedEnvelope = rehydratedDispatch.Envelopes[0].Envelope;
        replayedEnvelope.Id.Should().Be(attemptedEnvelope.Id);
        replayedEnvelope.Runtime.DeliveryIdentity.OperationId.Should()
            .Be(attemptedEnvelope.Runtime.DeliveryIdentity.OperationId);
        replayedEnvelope.Payload.Value.ToByteArray().Should().Equal(attemptedEnvelope.Payload.Value.ToByteArray());
        rehydrated.State.Status.Should().Be(ChannelWorkflowDraftRunStatus.ReplyHandedOff);
        rehydratedScheduler.PurgedActorIds.Should().Contain(rehydrated.Id);
    }

    [Fact]
    public async Task WorkflowDraftRunGAgent_ShouldReplayPersistedTerminalWhenFinalAppendFailsAfterAdmission()
    {
        var eventStore = new InMemoryEventStore();
        var timeProvider = new FakeTimeProvider(WorkflowDraftRunNow);
        var firstScheduler = new RecordingCallbackScheduler();
        var firstDispatch = new RecordingActorDispatchPort();
        var firstAgent = await CreateWorkflowDraftRunAgentAsync(
            firstDispatch,
            new RecordingWorkflowDraftRunInteractionPort(),
            eventStore,
            firstScheduler,
            timeProvider);
        var request = BuildWorkflowRequest();

        await firstAgent.HandleStartAsync(new ChannelWorkflowDraftRunStartRequested
        {
            Request = request.Clone(),
            RunId = request.RunId,
        });
        eventStore.FailNextAppend<ChannelWorkflowDraftRunReplyHandedOffEvent>();

        await firstAgent.HandleWorkflowInteractionCompletedAsync(new ChannelWorkflowDraftRunInteractionCompleted
        {
            Request = request.Clone(),
            Succeeded = true,
            Completed = true,
        });

        firstAgent.State.Status.Should().Be(ChannelWorkflowDraftRunStatus.TerminalProduced);
        firstScheduler.PurgedActorIds.Should().BeEmpty();
        var admittedEnvelope = firstDispatch.Envelopes.Should().ContainSingle().Subject.Envelope;

        var rehydratedScheduler = new RecordingCallbackScheduler();
        var rehydratedDispatch = new RecordingActorDispatchPort();
        var rehydrated = await CreateWorkflowDraftRunAgentAsync(
            rehydratedDispatch,
            new RecordingWorkflowDraftRunInteractionPort(),
            eventStore,
            rehydratedScheduler,
            timeProvider);
        var retryRequest = rehydratedScheduler.Timeouts
            .Select(x => x.TriggerEnvelope.Payload)
            .Single(x => x.Is(ChannelWorkflowDraftRunTerminalHandoffRetryElapsed.Descriptor))
            .Unpack<ChannelWorkflowDraftRunTerminalHandoffRetryElapsed>();

        await rehydrated.HandleTerminalHandoffRetryAsync(retryRequest);

        rehydratedDispatch.Envelopes.Should().ContainSingle();
        var replayedEnvelope = rehydratedDispatch.Envelopes[0].Envelope;
        replayedEnvelope.Id.Should().Be(admittedEnvelope.Id);
        replayedEnvelope.Payload.Value.ToByteArray().Should().Equal(admittedEnvelope.Payload.Value.ToByteArray());
        rehydrated.State.Status.Should().Be(ChannelWorkflowDraftRunStatus.ReplyHandedOff);
        rehydratedScheduler.PurgedActorIds.Should().Contain(rehydrated.Id);
    }

    [Fact]
    public async Task WorkflowDraftRunGAgent_ShouldFailClosedWhenLegacyStartedStateHasNoRecoveryContext()
    {
        const string actorId = "channel-workflow-draft-run:workflow-draft-run-1";
        var eventStore = new InMemoryEventStore();
        var now = new FakeTimeProvider(WorkflowDraftRunNow);
        await eventStore.AppendAsync(
            actorId,
            [new StateEvent
            {
                EventId = "legacy-workflow-draft-run-started",
                Timestamp = Timestamp.FromDateTimeOffset(now.GetUtcNow()),
                Version = 1,
                EventType = ChannelWorkflowDraftRunStartedEvent.Descriptor.FullName,
                EventData = Any.Pack(new ChannelWorkflowDraftRunStartedEvent
                {
                    RunId = "workflow-draft-run-1",
                    CorrelationId = "msg-1",
                    TargetActorId = "conversation-1",
                    StartedAtUnixMs = now.GetUtcNow().ToUnixTimeMilliseconds(),
                }),
                AgentId = actorId,
            }],
            expectedVersion: 0);
        var scheduler = new RecordingCallbackScheduler();
        var workflow = new RecordingWorkflowDraftRunInteractionPort();
        var dispatch = new RecordingActorDispatchPort();

        var agent = await CreateWorkflowDraftRunAgentAsync(
            dispatch,
            workflow,
            eventStore,
            scheduler,
            now);

        workflow.Started.Should().BeEmpty();
        scheduler.Timeouts.Should().ContainSingle();
        var timeout = scheduler.Timeouts[0]
            .TriggerEnvelope.Payload.Unpack<ChannelWorkflowDraftRunRecoveryTimeoutElapsed>();
        timeout.RecoveryDeadlineUnixMs.Should().Be(0);

        await agent.HandleRecoveryTimeoutAsync(timeout);

        dispatch.Envelopes.Should().ContainSingle();
        var failure = dispatch.Envelopes[0].Envelope.Payload.Unpack<LlmReplyReadyEvent>();
        failure.ErrorCode.Should().Be("workflow_draft_run_recovery_context_missing");
        agent.State.Status.Should().Be(ChannelWorkflowDraftRunStatus.Failed);
        scheduler.PurgedActorIds.Should().Contain(actorId);
    }

    [Fact]
    public async Task WorkflowDraftRunGAgent_ShouldIgnoreTerminalStartWithoutReopeningRun()
    {
        var workflow = new RecordingWorkflowDraftRunInteractionPort();
        var dispatch = new RecordingActorDispatchPort();
        var agent = await CreateWorkflowDraftRunAgentAsync(dispatch, workflow);
        var request = BuildWorkflowRequest();

        await agent.HandleStartAsync(new ChannelWorkflowDraftRunStartRequested
        {
            Request = request,
            RunId = request.RunId,
        });
        await agent.HandleWorkflowFrameObservedAsync(BuildFrameObserved(
            request,
            new ChannelWorkflowDraftRunFrame
            {
                RunFinished = new ChannelWorkflowDraftRunFinishedFrame
                {
                    ResultOutput = "done",
                },
            }));
        await agent.HandleStartAsync(new ChannelWorkflowDraftRunStartRequested
        {
            Request = request.Clone(),
            RunId = request.RunId,
        });

        workflow.Started.Should().ContainSingle();
        dispatch.Envelopes.Should().ContainSingle();
        agent.State.Status.Should().Be(ChannelWorkflowDraftRunStatus.ReplyHandedOff);
    }

    [Fact]
    public async Task WorkflowDraftRunGAgent_ShouldDropMismatchedRequestRunIdBeforeStateOrDispatch()
    {
        var workflow = new RecordingWorkflowDraftRunInteractionPort();
        var dispatch = new RecordingActorDispatchPort();
        var agent = await CreateWorkflowDraftRunAgentAsync(dispatch, workflow);
        var request = BuildWorkflowRequest();
        request.RunId = "workflow-draft-run-other";

        await agent.HandleStartAsync(new ChannelWorkflowDraftRunStartRequested
        {
            Request = request,
            RunId = "workflow-draft-run-1",
        });

        workflow.Started.Should().BeEmpty();
        dispatch.Envelopes.Should().BeEmpty();
        agent.State.Status.Should().Be(ChannelWorkflowDraftRunStatus.Unspecified);
        agent.State.RunId.Should().BeEmpty();
    }

    [Fact]
    public async Task WorkflowDraftRunGAgent_ShouldDispatchTerminalFailure_WhenWorkflowPortMissing()
    {
        var dispatch = new RecordingActorDispatchPort();
        var agent = await CreateWorkflowDraftRunAgentAsync(dispatch, workflowInteractionPort: null);
        var request = BuildWorkflowRequest();

        await agent.HandleStartAsync(new ChannelWorkflowDraftRunStartRequested
        {
            Request = request,
            RunId = request.RunId,
        });

        dispatch.Envelopes.Should().ContainSingle();
        var ready = dispatch.Envelopes[0].Envelope.Payload.Unpack<LlmReplyReadyEvent>();
        ready.TerminalState.Should().Be(LlmReplyTerminalState.Failed);
        ready.ErrorCode.Should().Be("workflow_interaction_port_unavailable");
        agent.State.Status.Should().Be(ChannelWorkflowDraftRunStatus.Failed);
        agent.State.ErrorCode.Should().Be("workflow_interaction_port_unavailable");
    }

    [Fact]
    public async Task WorkflowDraftRunGAgent_ShouldDispatchTerminalFailure_WhenWorkflowStartFails()
    {
        var workflow = new RecordingWorkflowDraftRunInteractionPort();
        var dispatch = new RecordingActorDispatchPort();
        var agent = await CreateWorkflowDraftRunAgentAsync(dispatch, workflow);
        var request = BuildWorkflowRequest();

        await agent.HandleStartAsync(new ChannelWorkflowDraftRunStartRequested
        {
            Request = request,
            RunId = "workflow-draft-run-1",
        });
        await agent.HandleWorkflowInteractionCompletedAsync(new ChannelWorkflowDraftRunInteractionCompleted
        {
            Request = request.Clone(),
            Succeeded = false,
            Completed = false,
            ErrorCode = "workflow_start_failed:WorkflowNotFound",
            ErrorSummary = "Workflow start failed: WorkflowNotFound",
        });

        dispatch.Envelopes.Should().ContainSingle();
        var ready = dispatch.Envelopes[0].Envelope.Payload.Unpack<LlmReplyReadyEvent>();
        ready.TerminalState.Should().Be(LlmReplyTerminalState.Failed);
        ready.ErrorCode.Should().Be("workflow_start_failed:WorkflowNotFound");
        agent.State.Status.Should().Be(ChannelWorkflowDraftRunStatus.Failed);
    }

    [Fact]
    public async Task WorkflowDraftRunGAgent_ShouldDispatchTerminalFailure_WhenFinalizeIsNotTerminal()
    {
        var workflow = new RecordingWorkflowDraftRunInteractionPort();
        var dispatch = new RecordingActorDispatchPort();
        var agent = await CreateWorkflowDraftRunAgentAsync(dispatch, workflow);
        var request = BuildWorkflowRequest();

        await agent.HandleStartAsync(new ChannelWorkflowDraftRunStartRequested
        {
            Request = request,
            RunId = "workflow-draft-run-1",
        });
        await agent.HandleWorkflowInteractionCompletedAsync(new ChannelWorkflowDraftRunInteractionCompleted
        {
            Request = request.Clone(),
            Succeeded = true,
            Completed = false,
        });

        dispatch.Envelopes.Should().ContainSingle();
        var ready = dispatch.Envelopes[0].Envelope.Payload.Unpack<LlmReplyReadyEvent>();
        ready.TerminalState.Should().Be(LlmReplyTerminalState.Failed);
        ready.ErrorCode.Should().Be("workflow_completion_unknown");
        agent.State.Status.Should().Be(ChannelWorkflowDraftRunStatus.Failed);
    }

    [Fact]
    public async Task WorkflowDraftRunGAgent_ShouldDispatchTerminalFailure_WhenWorkflowThrows()
    {
        var workflow = new RecordingWorkflowDraftRunInteractionPort();
        var dispatch = new RecordingActorDispatchPort();
        var agent = await CreateWorkflowDraftRunAgentAsync(dispatch, workflow);
        var request = BuildWorkflowRequest();

        await agent.HandleStartAsync(new ChannelWorkflowDraftRunStartRequested
        {
            Request = request,
            RunId = "workflow-draft-run-1",
        });
        await agent.HandleWorkflowInteractionCompletedAsync(new ChannelWorkflowDraftRunInteractionCompleted
        {
            Request = request.Clone(),
            Succeeded = false,
            Completed = false,
            ErrorCode = "workflow_draft_run_exception",
            ErrorSummary = "Workflow draft-run failed.",
        });

        dispatch.Envelopes.Should().ContainSingle();
        var ready = dispatch.Envelopes[0].Envelope.Payload.Unpack<LlmReplyReadyEvent>();
        ready.TerminalState.Should().Be(LlmReplyTerminalState.Failed);
        ready.ErrorCode.Should().Be("workflow_draft_run_exception");
        agent.State.Status.Should().Be(ChannelWorkflowDraftRunStatus.Failed);
    }

    [Fact]
    public void ReplyRenderer_ShouldRenderWorkflowFailuresAsTerminalReadyText()
    {
        var renderer = new WorkflowDraftRunReplyRenderer();

        var rendered = renderer.Render(new ChannelWorkflowDraftRunFrame
        {
            RunError = new ChannelWorkflowDraftRunErrorFrame
            {
                Message = "boom",
                Code = "bad_input",
            },
        }, "partial");

        rendered.Should().NotBeNull();
        rendered!.IsTerminal.Should().BeTrue();
        rendered.IsFailure.Should().BeTrue();
        rendered.ErrorCode.Should().Be("bad_input");
        rendered.Text.Should().Contain("boom");
    }

    [Fact]
    public void ReplyRenderer_ShouldRenderFinishedResultOutput_WhenNoTextWasAccumulated()
    {
        var renderer = new WorkflowDraftRunReplyRenderer();

        var rendered = renderer.Render(new ChannelWorkflowDraftRunFrame
        {
            RunFinished = new ChannelWorkflowDraftRunFinishedFrame
            {
                ResultOutput = "result output",
            },
        }, string.Empty);

        rendered.Should().NotBeNull();
        rendered!.IsTerminal.Should().BeTrue();
        rendered.IsFailure.Should().BeFalse();
        rendered.Text.Should().Be("result output");
    }

    [Fact]
    public void ReplyRenderer_ShouldRenderStoppedRunsAsTerminalFailures()
    {
        var renderer = new WorkflowDraftRunReplyRenderer();

        var rendered = renderer.Render(new ChannelWorkflowDraftRunFrame
        {
            RunStopped = new ChannelWorkflowDraftRunStoppedFrame { Reason = "user canceled" },
        }, "partial");

        rendered.Should().NotBeNull();
        rendered!.IsTerminal.Should().BeTrue();
        rendered.IsFailure.Should().BeTrue();
        rendered.ErrorCode.Should().Be("workflow_run_stopped");
        rendered.Text.Should().Contain("user canceled");
    }

    private static NeedsWorkflowDraftRunEvent BuildWorkflowRequest() =>
        new()
        {
            CorrelationId = "msg-1",
            TargetActorId = "conversation-actor-1",
            RegistrationId = "reg-1",
            Activity = new ChatActivity
            {
                Id = "msg-1",
                Content = new MessageContent { Text = "/workflow run daily-greeting" },
            },
            WorkflowSource = new ChannelWorkflowDraftRunSource
            {
                Kind = ChannelWorkflowDraftRunSourceKind.DefinitionActor,
                ScopeId = "scope-1",
                WorkflowId = "daily-greeting",
                WorkflowName = "daily-greeting",
                DefinitionActorId = "workflow-actor-1",
            },
            Prompt = "/workflow run daily-greeting",
            RequestedAtUnixMs = 1,
            RunId = "workflow-draft-run-1",
            ReplyToken = "reply-token-1",
            ReplyTokenExpiresAtUnixMs = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds(),
            NyxUserAccessToken = "user-token-1",
            Headers =
            {
                ["registration_id"] = "reg-1",
            },
        };

    private static NeedsWorkflowDraftRunEvent BuildWorkflowRequestWithLarkAttachments()
    {
        var request = BuildWorkflowRequest();
        request.Activity.ChannelId = ChannelId.From("lark");
        request.Activity.TransportExtras = new TransportExtras
        {
            NyxPlatform = "lark",
            NyxPlatformMessageId = "om_123",
            NyxUserAccessToken = "activity-token-should-not-win",
        };
        request.Activity.Content.Attachments.Add(new AttachmentRef
        {
            AttachmentId = "img_v3_1",
            Kind = AttachmentKind.Image,
            Name = "image-from-relay",
            ContentType = "image",
        });
        request.Activity.Content.Attachments.Add(new AttachmentRef
        {
            AttachmentId = "file_v3_1",
            Kind = AttachmentKind.File,
            Name = "file-from-relay",
            ContentType = "file",
        });
        return request;
    }

    private static ChatActivity BuildWorkflowActivity(string? scopeId, string? userToken) =>
        new()
        {
            Id = "msg-1",
            Content = new MessageContent { Text = "/workflow run daily-greeting" },
            TransportExtras = new TransportExtras
            {
                NyxRegistrationScopeId = scopeId ?? string.Empty,
                NyxUserAccessToken = userToken ?? string.Empty,
            },
        };

    private static ScopeWorkflowSummary BuildWorkflowSummary(
        string scopeId,
        string workflowId,
        string actorId = "actor-daily-greeting") =>
        new(
            scopeId,
            workflowId,
            $"Display {workflowId}",
            $"service-key-{workflowId}",
            workflowId,
            actorId,
            "rev-active",
            "deployment-1",
            "active",
            DateTimeOffset.Parse("2026-05-25T00:00:00Z"));

    private static ChannelWorkflowDraftRunFrameObserved BuildFrameObserved(
        NeedsWorkflowDraftRunEvent request,
        ChannelWorkflowDraftRunFrame frame) =>
        new()
        {
            Request = request.Clone(),
            Frame = frame,
            ObservedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

    private static ChannelWorkflowDraftRunInteractionPort CreateInteractionPort(
        RecordingActorDispatchPort dispatch,
        IWorkflowChatRunInteractionPort workflowInteractionPort,
        ILarkNyxClient? larkClient = null,
        IFileArtifactIngressPort? fileIngressPort = null,
        ILarkOutboundClientFactory? outboundClientFactory = null) =>
        new(
            new RecordingActorRuntime(),
            dispatch,
            NullLogger<ChannelWorkflowDraftRunInteractionPort>.Instance,
            workflowInteractionPort,
            TimeProvider.System,
            larkClient,
            fileIngressPort,
            outboundClientFactory);

    private static async Task<ChannelWorkflowDraftRunGAgent> CreateWorkflowDraftRunAgentAsync(
        IActorDispatchPort dispatchPort,
        IChannelWorkflowDraftRunInteractionPort? workflowInteractionPort,
        IEventStore? eventStore = null,
        IActorRuntimeCallbackScheduler? callbackScheduler = null,
        TimeProvider? timeProvider = null,
        IRuntimeSecretStore? runtimeSecretStore = null)
    {
        eventStore ??= new InMemoryEventStore();
        callbackScheduler ??= new RecordingCallbackScheduler();
        runtimeSecretStore ??= new InMemoryRuntimeSecretStore();
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection()
            .AddSingleton(eventStore)
            .AddSingleton(callbackScheduler)
            .AddSingleton(runtimeSecretStore)
            .AddSingleton<EventSourcingRuntimeOptions>()
            .AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>))
            .BuildServiceProvider();

        var agent = new ChannelWorkflowDraftRunGAgent(
            dispatchPort,
            new WorkflowDraftRunReplyRenderer(),
            NullLogger<ChannelWorkflowDraftRunGAgent>.Instance,
            workflowInteractionPort,
            timeProvider ?? TimeProvider.System)
        {
            Services = services,
            EventSourcingBehaviorFactory =
                services.GetRequiredService<IEventSourcingBehaviorFactory<ChannelWorkflowDraftRunGAgentState>>(),
        };
        typeof(Aevatar.Foundation.Core.GAgentBase)
            .GetMethod("SetId", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(agent, ["channel-workflow-draft-run:workflow-draft-run-1"]);
        await agent.ActivateAsync();
        return agent;
    }

    private static ChannelSlashCommandRegistry BuildWorkflowSlashRegistry() =>
        new([new ChannelWorkflowDraftRunSlashCommandHandler()]);

    private sealed class RecordingWorkflowDraftRunInteractionPort : IChannelWorkflowDraftRunInteractionPort
    {
        public List<NeedsWorkflowDraftRunEvent> Dispatched { get; } = [];
        public List<(string RunActorId, NeedsWorkflowDraftRunEvent Request)> Started { get; } = [];

        public Task DispatchAsync(NeedsWorkflowDraftRunEvent request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Dispatched.Add(request.Clone());
            return Task.CompletedTask;
        }

        public Task StartWorkflowInteractionAsync(string runActorId, NeedsWorkflowDraftRunEvent request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Started.Add((runActorId, request.Clone()));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingWorkflowChatRunInteractionPort : IWorkflowChatRunInteractionPort
    {
        private readonly TaskCompletionSource<WorkflowChatRunRequest> _request =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<WorkflowRunEventEnvelope> Frames { get; init; } = [];

        public Exception? Exception { get; init; }

        public WorkflowChatRunInteractionResult? Result { get; init; }

        public bool HasRequest => _request.Task.IsCompletedSuccessfully;

        public async Task<WorkflowChatRunRequest> WaitForRequestAsync() =>
            await _request.Task.WaitAsync(TimeSpan.FromSeconds(5));

        public async Task<WorkflowChatRunInteractionResult> ExecuteAsync(
            WorkflowChatRunRequest request,
            Func<WorkflowRunEventEnvelope, CancellationToken, ValueTask> emitAsync,
            Func<WorkflowChatInteractionAcceptedReceipt, CancellationToken, ValueTask>? onAcceptedAsync = null,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _request.TrySetResult(request);
            if (Exception is not null)
                throw Exception;

            foreach (var frame in Frames)
            {
                await emitAsync(frame, ct);
            }

            if (Result is not null)
                return Result;

            var receipt = new WorkflowChatRunAcceptedReceipt(
                request.Source.ActorId ?? "workflow-actor-1",
                request.Source.WorkflowName ?? "daily-greeting",
                request.CommandIdSeed ?? "workflow-draft-run-1",
                request.CorrelationIdSeed ?? "msg-1");
            return WorkflowChatRunInteractionResult
                .Success(
                    receipt,
                    new CommandInteractionFinalizeResult<WorkflowProjectionCompletionStatus>(
                        WorkflowProjectionCompletionStatus.Completed,
                        true));
        }
    }

    private sealed class RecordingWorkflowFileIngressPort : IFileArtifactIngressPort
    {
        public List<FileArtifactIngressRequest> Requests { get; } = [];

        public ValueTask<FileArtifactIngressResult> IngestAsync(
            FileArtifactIngressRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            var index = Requests.Count;
            return ValueTask.FromResult(new FileArtifactIngressResult(new FileArtifactRef
            {
                FileId = $"wf-file-{index}",
                ArtifactId = $"workflow-file://wf-file-{index}",
                SourceKind = request.SourceKind,
                SourceMessageId = request.SourceMessageId,
                SourceResourceKey = request.SourceResourceKey,
                FileName = request.FileName,
                MediaType = request.MediaType,
                SizeBytes = request.Content.Length,
                Sha256 = $"sha-{index}",
                CreatedAtUnixMs = 10 + index,
                ExpiresAtUnixMs = 100 + index,
            }));
        }
    }

    private sealed class StubScopeWorkflowQueryPort(ScopeWorkflowSummary? workflow) : Aevatar.GAgentService.Abstractions.Ports.IScopeWorkflowQueryPort
    {
        public Task<IReadOnlyList<ScopeWorkflowSummary>> ListAsync(string scopeId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ScopeWorkflowSummary>>(workflow is null ? [] : [workflow]);

        public Task<ScopeWorkflowLookupResult> LookupByWorkflowIdAsync(
            string scopeId,
            string workflowId,
            CancellationToken ct = default)
        {
            var summary = workflow is not null &&
                          string.Equals(workflow.ScopeId, scopeId, StringComparison.Ordinal) &&
                          string.Equals(workflow.WorkflowId, workflowId, StringComparison.Ordinal)
                ? workflow
                : null;
            return Task.FromResult(summary switch
            {
                null => new ScopeWorkflowLookupResult(ScopeWorkflowLookupStatus.NotFound, null, "test_not_found"),
                { ActorId.Length: 0 } => new ScopeWorkflowLookupResult(ScopeWorkflowLookupStatus.NotReady, null, "test_not_ready"),
                _ => new ScopeWorkflowLookupResult(ScopeWorkflowLookupStatus.Runnable, summary, "test_runnable"),
            });
        }

        public Task<ScopeWorkflowSummary?> GetByWorkflowIdAsync(
            string scopeId,
            string workflowId,
            CancellationToken ct = default) =>
            Task.FromResult(workflow is not null &&
                            string.Equals(workflow.ScopeId, scopeId, StringComparison.Ordinal) &&
                            string.Equals(workflow.WorkflowId, workflowId, StringComparison.Ordinal)
                ? workflow
                : null);

        public Task<ScopeWorkflowSummary?> GetByActorIdAsync(
            string scopeId,
            string actorId,
            CancellationToken ct = default) =>
            Task.FromResult(workflow is not null &&
                            string.Equals(workflow.ScopeId, scopeId, StringComparison.Ordinal) &&
                            string.Equals(workflow.ActorId, actorId, StringComparison.Ordinal)
                ? workflow
                : null);
    }

    private sealed class RecordingActorRuntime : IActorRuntime
    {
        public List<string> Created { get; } = [];

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent
        {
            var actor = Substitute.For<IActor>();
            actor.Id.Returns(id ?? Guid.NewGuid().ToString("N"));
            Created.Add(actor.Id);
            return Task.FromResult(actor);
        }

        public Task<IActor> CreateAsync(System.Type agentType, string? id = null, CancellationToken ct = default) =>
            CreateAsync<ChannelWorkflowDraftRunGAgent>(id, ct);

        public Task DestroyAsync(string id, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IActor?> GetAsync(string id) => Task.FromResult<IActor?>(null);

        public Task<bool> ExistsAsync(string id) => Task.FromResult(false);

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) => Task.CompletedTask;

        public Task UnlinkAsync(string childId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecordingCallbackScheduler : IActorRuntimeCallbackScheduler
    {
        public List<RuntimeCallbackTimeoutRequest> Timeouts { get; } = [];
        public List<string> PurgedActorIds { get; } = [];

        public Task<RuntimeCallbackLease> ScheduleTimeoutAsync(
            RuntimeCallbackTimeoutRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Timeouts.Add(request);
            return Task.FromResult(new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                Timeouts.Count,
                RuntimeCallbackBackend.InMemory));
        }

        public Task<RuntimeCallbackLease> ScheduleTimerAsync(
            RuntimeCallbackTimerRequest request,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task CancelAsync(RuntimeCallbackLease lease, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task PurgeActorAsync(string actorId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            PurgedActorIds.Add(actorId);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingActorDispatchPort(int failuresRemaining = 0) : IActorDispatchPort
    {
        private readonly object _gate = new();
        private readonly Dictionary<int, TaskCompletionSource<IReadOnlyList<(string ActorId, EventEnvelope Envelope)>>> _countWaiters = [];
        private int _failuresRemaining = failuresRemaining;

        public List<(string ActorId, EventEnvelope Envelope)> Envelopes { get; } = [];

        public Task<DispatchAdmission> DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            lock (_gate)
            {
                Envelopes.Add((actorId, envelope.Clone()));
                foreach (var waiter in _countWaiters.Where(x => Envelopes.Count >= x.Key).ToArray())
                {
                    _countWaiters.Remove(waiter.Key);
                    waiter.Value.TrySetResult(Envelopes.Select(CloneDispatch).ToList());
                }
            }

            if (Interlocked.Decrement(ref _failuresRemaining) >= 0)
                throw new InvalidOperationException("Simulated actor dispatch admission failure.");

            return Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
        }

        public async Task<T> WaitForSinglePayloadAsync<T>()
            where T : IMessage<T>, new()
        {
            var envelopes = await WaitForEnvelopeCountAsync(1);
            return envelopes[0].Envelope.Payload.Unpack<T>();
        }

        public async Task<IReadOnlyList<(string ActorId, EventEnvelope Envelope)>> WaitForEnvelopeCountAsync(int count)
        {
            Task<IReadOnlyList<(string ActorId, EventEnvelope Envelope)>> task;
            lock (_gate)
            {
                if (Envelopes.Count >= count)
                    task = Task.FromResult<IReadOnlyList<(string ActorId, EventEnvelope Envelope)>>(
                        Envelopes.Select(CloneDispatch).ToList());
                else
                {
                    var waiter = new TaskCompletionSource<IReadOnlyList<(string ActorId, EventEnvelope Envelope)>>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    _countWaiters[count] = waiter;
                    task = waiter.Task;
                }
            }

            return await task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        private static (string ActorId, EventEnvelope Envelope) CloneDispatch((string ActorId, EventEnvelope Envelope) value) =>
            (value.ActorId, value.Envelope.Clone());
    }

    private sealed class InMemoryEventStore : IEventStore
    {
        private readonly Dictionary<string, List<StateEvent>> _events = new(StringComparer.Ordinal);
        private string? _failNextEventType;

        public void FailNextAppend<TEvent>()
            where TEvent : IMessage<TEvent>, new() =>
            _failNextEventType = new TEvent().Descriptor.FullName;

        public Task<EventStoreCommitResult> AppendAsync(
            string agentId,
            IEnumerable<StateEvent> events,
            long expectedVersion,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (!_events.TryGetValue(agentId, out var stream))
            {
                stream = [];
                _events[agentId] = stream;
            }

            var currentVersion = stream.Count == 0 ? 0 : stream[^1].Version;
            if (currentVersion != expectedVersion)
                throw new EventStoreOptimisticConcurrencyException(agentId, expectedVersion, currentVersion);

            var appended = events.Select(x => x.Clone()).ToList();
            if (_failNextEventType is { } failedType && appended.Any(x => x.EventType == failedType))
            {
                _failNextEventType = null;
                throw new InvalidOperationException($"Simulated event-store append failure for '{failedType}'.");
            }

            stream.AddRange(appended);
            return Task.FromResult(new EventStoreCommitResult
            {
                AgentId = agentId,
                LatestVersion = stream.Count == 0 ? 0 : stream[^1].Version,
                CommittedEvents = { appended.Select(x => x.Clone()) },
            });
        }

        public Task<IReadOnlyList<StateEvent>> GetEventsAsync(
            string agentId,
            long? fromVersion = null,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (!_events.TryGetValue(agentId, out var stream))
                return Task.FromResult<IReadOnlyList<StateEvent>>([]);

            IReadOnlyList<StateEvent> result = fromVersion.HasValue
                ? stream.Where(x => x.Version > fromVersion.Value).Select(x => x.Clone()).ToList()
                : stream.Select(x => x.Clone()).ToList();
            return Task.FromResult(result);
        }

        public Task<long> GetVersionAsync(string agentId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (!_events.TryGetValue(agentId, out var stream) || stream.Count == 0)
                return Task.FromResult(0L);
            return Task.FromResult(stream[^1].Version);
        }

        public Task<long> DeleteEventsUpToAsync(string agentId, long toVersion, CancellationToken ct = default) =>
            Task.FromResult(0L);
    }
}

using Aevatar.Workflow.Abstractions;
using Aevatar.Foundation.Abstractions.Interactions;
using FluentAssertions;
using Google.Protobuf;

namespace Aevatar.Workflow.Host.Api.Tests;

public class WorkflowAbstractionsProtoCoverageTests
{
    [Fact]
    public void StartWorkflowEvent_ShouldCloneAndRoundtrip()
    {
        var evt = new StartWorkflowEvent
        {
            WorkflowName = "wf",
            Input = "{\"a\":1}",
            ForkSeed = new WorkflowRunForkSeed
            {
                SourceRunId = "run-source",
                StartAtStepId = "step-b",
                Attempt = 2,
            },
        };
        evt.Parameters["k"] = "v";
        evt.ForkSeed.Variables["step-a"] = "alpha";
        evt.InputFileRefs.Add(BuildWorkflowFileRef("file-1"));

        var clone = evt.Clone();
        clone.Should().BeEquivalentTo(evt);

        var parsed = StartWorkflowEvent.Parser.ParseFrom(evt.ToByteArray());
        parsed.WorkflowName.Should().Be("wf");
        parsed.Input.Should().Contain("a");
        parsed.Parameters["k"].Should().Be("v");
        parsed.ForkSeed.SourceRunId.Should().Be("run-source");
        parsed.ForkSeed.StartAtStepId.Should().Be("step-b");
        parsed.ForkSeed.Attempt.Should().Be(2);
        parsed.ForkSeed.Variables["step-a"].Should().Be("alpha");
        parsed.InputFileRefs.Should().ContainSingle().Which.FileId.Should().Be("file-1");
    }

    [Fact]
    public void WorkflowRunForkSeedAndForkRequestedEvent_ShouldRoundtrip()
    {
        var seed = new WorkflowRunForkSeed
        {
            SourceRunId = "run-source",
            StartAtStepId = "step-b",
            Attempt = 2,
        };
        seed.Variables["step-a"] = "alpha";

        var parsedSeed = WorkflowRunForkSeed.Parser.ParseFrom(seed.ToByteArray());
        parsedSeed.Should().BeEquivalentTo(seed);

        var requested = new WorkflowRunForkRequestedEvent
        {
            SourceRunId = "run-source",
            StartAtStepId = "step-b",
            Attempt = 2,
            ScopeId = "scope-1",
        };
        var parsedRequested = WorkflowRunForkRequestedEvent.Parser.ParseFrom(requested.ToByteArray());
        parsedRequested.Should().BeEquivalentTo(requested);
        ((IMessage)parsedSeed).Descriptor.Name.Should().Be(nameof(WorkflowRunForkSeed));
        ((IMessage)parsedRequested).Descriptor.Name.Should().Be(nameof(WorkflowRunForkRequestedEvent));
    }

    [Fact]
    public void WorkflowChatInputPartPayload_ShouldRoundtripTypedFileRef()
    {
        WorkflowChatInputPartPayload.Descriptor.Fields.InDeclarationOrder()
            .Should().Contain(field => field.FieldNumber == 7 && field.Name == "file_ref");

        var part = new WorkflowChatInputPartPayload
        {
            Kind = WorkflowChatInputPartKind.Image,
            MediaType = "image/png",
            Name = "invoice.png",
            FileRef = new WorkflowFileRef
            {
                FileId = "file-1",
                ArtifactId = "artifact-1",
                SourceKind = WorkflowFileSourceKind.ConnectedServiceResource,
                SourceMessageId = "om_1",
                SourceResourceKey = "image_key_1",
                FileName = "invoice.png",
                MediaType = "image/png",
                Sha256 = "abc",
                CreatedAtUnixMs = 1710000000000,
                ExpiresAtUnixMs = 1710003600000,
            },
        };

        var parsed = WorkflowChatInputPartPayload.Parser.ParseFrom(part.ToByteArray());

        parsed.FileRef.FileId.Should().Be("file-1");
        parsed.FileRef.ArtifactId.Should().Be("artifact-1");
        parsed.FileRef.SourceKind.Should().Be(WorkflowFileSourceKind.ConnectedServiceResource);
        parsed.FileRef.SourceMessageId.Should().Be("om_1");
        parsed.FileRef.SourceResourceKey.Should().Be("image_key_1");
        parsed.FileRef.FileName.Should().Be("invoice.png");
        parsed.FileRef.MediaType.Should().Be("image/png");
        parsed.FileRef.Sha256.Should().Be("abc");
        parsed.FileRef.CreatedAtUnixMs.Should().Be(1710000000000);
        parsed.FileRef.ExpiresAtUnixMs.Should().Be(1710003600000);
        WorkflowExecutionMessagesReflection.Descriptor.MessageTypes.Select(x => x.Name)
            .Should().Contain(nameof(WorkflowFileRef));
    }

    [Fact]
    public void WorkflowChatRequestEvent_ShouldRoundtripExternalIngress()
    {
        WorkflowChatRequestEvent.Descriptor.Fields.InDeclarationOrder()
            .Should().Contain(field => field.FieldNumber == 12 && field.Name == "external_ingress");

        var request = new WorkflowChatRequestEvent
        {
            Prompt = "hello",
            SessionId = "session-1",
            ExternalIngress = new WorkflowExternalIngressContext
            {
                RouteKey = "invoice",
                SourceId = "lark",
                DeliveryId = "delivery-1",
                ReceivedAtUnixMs = 1710000000000,
                ContentType = "application/json",
                PayloadFingerprint = "abc",
                AuthScheme = "hmac-sha256",
                PrincipalSubject = "lark",
            },
        };

        var parsed = WorkflowChatRequestEvent.Parser.ParseFrom(request.ToByteArray());

        parsed.ExternalIngress.RouteKey.Should().Be("invoice");
        parsed.ExternalIngress.SourceId.Should().Be("lark");
        parsed.ExternalIngress.DeliveryId.Should().Be("delivery-1");
        parsed.ExternalIngress.ReceivedAtUnixMs.Should().Be(1710000000000);
        parsed.ExternalIngress.ContentType.Should().Be("application/json");
        parsed.ExternalIngress.PayloadFingerprint.Should().Be("abc");
        parsed.ExternalIngress.AuthScheme.Should().Be("hmac-sha256");
        parsed.ExternalIngress.PrincipalSubject.Should().Be("lark");
        WorkflowExecutionMessagesReflection.Descriptor.MessageTypes.Select(x => x.Name)
            .Should().Contain(nameof(WorkflowExternalIngressContext));
    }

    [Fact]
    public void WorkflowWebhookReplayRecord_ShouldRoundtrip()
    {
        var record = new WorkflowWebhookReplayRecord
        {
            RouteKey = "invoice",
            SourceId = "lark",
            DeliveryId = "delivery-1",
            PayloadFingerprint = "abc",
            ReceivedAtUnixMs = 1710000000000,
            CommandId = "cmd-1",
            CorrelationId = "corr-1",
            Completed = true,
        };

        var parsed = WorkflowWebhookReplayRecord.Parser.ParseFrom(record.ToByteArray());

        parsed.Should().BeEquivalentTo(record);
        WorkflowExecutionMessagesReflection.Descriptor.MessageTypes.Select(x => x.Name)
            .Should().Contain(nameof(WorkflowWebhookReplayRecord));
    }

    [Fact]
    public void WorkflowCompletedEvent_ShouldMergeAndCompare()
    {
        var source = new WorkflowCompletedEvent
        {
            WorkflowName = "wf",
            Success = true,
            Output = "ok",
            Error = "",
        };

        var target = new WorkflowCompletedEvent();
        target.MergeFrom(source);

        target.Should().BeEquivalentTo(source);
        target.Equals(new WorkflowCompletedEvent { WorkflowName = "wf-2" }).Should().BeFalse();
    }

    [Fact]
    public void StepRequestAndCompletedEvents_ShouldRoundtripAndKeepMaps()
    {
        var request = new StepRequestEvent
        {
            StepId = "s1",
            StepType = "llm_call",
            Input = "hello",
            TargetRole = "assistant",
        };
        request.InputFileRefs.Add(BuildWorkflowFileRef("file-step"));
        request.Parameters["temperature"] = "0.1";
        request.StepParameters.InteractionSpec = new InteractionSpec
        {
            Title = "Review",
            Body = "Approve?",
        };
        request.StepParameters.InteractionSpec.Actions.Add(new InteractionAction
        {
            Kind = InteractionActionKind.Button,
            ActionId = "approve",
            Label = "Approve",
            Style = InteractionActionStyle.Primary,
        });
        request.StepParameters.InteractionTemplateSpec = new InteractionTemplateSpec
        {
            TemplateId = "tpl-review",
        };
        request.StepParameters.InteractionTemplateSpec.TemplateVariable["run"] = "run-1";
        request.StepParameters.DeliveryTargetId = "agent-1";

        var completed = new StepCompletedEvent
        {
            StepId = "s1",
            Success = true,
            Output = "world",
            Error = "",
            WorkerId = "worker-1",
        };
        completed.Annotations["latency_ms"] = "12";

        var parsedRequest = StepRequestEvent.Parser.ParseFrom(request.ToByteArray());
        parsedRequest.StepType.Should().Be("llm_call");
        parsedRequest.InputFileRefs.Should().ContainSingle().Which.FileId.Should().Be("file-step");
        parsedRequest.Parameters["temperature"].Should().Be("0.1");
        parsedRequest.StepParameters.Parameters["temperature"].Should().Be("0.1");
        parsedRequest.StepParameters.InteractionSpec.Title.Should().Be("Review");
        parsedRequest.StepParameters.InteractionSpec.Actions[0].Style.Should().Be(InteractionActionStyle.Primary);
        parsedRequest.StepParameters.InteractionTemplateSpec.TemplateId.Should().Be("tpl-review");
        parsedRequest.StepParameters.InteractionTemplateSpec.TemplateVariable["run"].Should().Be("run-1");
        parsedRequest.StepParameters.DeliveryTargetId.Should().Be("agent-1");

        var parsedCompleted = StepCompletedEvent.Parser.ParseFrom(completed.ToByteArray());
        parsedCompleted.WorkerId.Should().Be("worker-1");
        parsedCompleted.Annotations["latency_ms"].Should().Be("12");

        parsedCompleted.Clone().Should().BeEquivalentTo(parsedCompleted);
    }

    [Fact]
    public void StepRequestEvent_ShouldExposeTypedStepParametersOnFieldEight()
    {
        StepRequestEvent.Descriptor.Fields.InDeclarationOrder()
            .Should().Contain(field => field.FieldNumber == 8 && field.Name == "step_parameters");
        WorkflowStepParameters.Descriptor.Fields.InDeclarationOrder()
            .Should().Contain(field => field.FieldNumber == 6 && field.Name == "delivery_target_id");
        WorkflowStepParameters.Descriptor.Fields.InDeclarationOrder()
            .Should().Contain(field => field.FieldNumber == 7 && field.Name == "transform_operation");
        StepRequestEvent.Descriptor.Fields.InDeclarationOrder()
            .Should().NotContain(field => field.FieldNumber == 5);
        StepRequestEvent.Descriptor.Fields.InDeclarationOrder()
            .Should().Contain(field => field.FieldNumber == 9 && field.Name == "input_file_refs");

        var request = new StepRequestEvent
        {
            StepId = "s-typed",
            StepType = "transform",
            StepParameters = new WorkflowStepParameters(),
        };
        request.StepParameters.Parameters["op"] = "trim";
        request.StepParameters.DeliveryTargetId = "agent-typed";
        request.StepParameters.TransformOperation = new TransformOperationSpec
        {
            Kind = TransformOperationKind.Round,
            Precision = 2,
        };
        request.Parameters["target"] = "result";
        request.StepParameters.InteractionSpec = new InteractionSpec { Body = "Continue?" };

        var parsed = StepRequestEvent.Parser.ParseFrom(request.ToByteArray());
        parsed.StepParameters.Parameters.Should().Contain(new KeyValuePair<string, string>("op", "trim"));
        parsed.Parameters.Should().Contain(new KeyValuePair<string, string>("target", "result"));
        parsed.StepParameters.DeliveryTargetId.Should().Be("agent-typed");
        parsed.StepParameters.TransformOperation.Kind.Should().Be(TransformOperationKind.Round);
        parsed.StepParameters.TransformOperation.Precision.Should().Be(2);
        parsed.StepParameters.InteractionSpec.Body.Should().Be("Continue?");
        parsed.ToString().Should().Contain("stepParameters");
        ((IMessage)parsed.StepParameters).Descriptor.Name.Should().Be(nameof(WorkflowStepParameters));
    }

    [Fact]
    public void WorkflowRunExecutionStartedEvent_ShouldRoundtripInputFileRefs()
    {
        WorkflowRunExecutionStartedEvent.Descriptor.Fields.InDeclarationOrder()
            .Should().Contain(field => field.FieldNumber == 8 && field.Name == "input_file_refs");

        var evt = new WorkflowRunExecutionStartedEvent
        {
            RunId = "run-1",
            WorkflowName = "wf",
            Input = "hello",
        };
        evt.InputFileRefs.Add(BuildWorkflowFileRef("file-started"));

        var parsed = WorkflowRunExecutionStartedEvent.Parser.ParseFrom(evt.ToByteArray());

        parsed.InputFileRefs.Should().ContainSingle().Which.FileId.Should().Be("file-started");
    }

    [Fact]
    public void WorkflowLlmExecutionIntent_ShouldRoundtripInputFileRefs()
    {
        WorkflowLlmExecutionIntent.Descriptor.Fields.InDeclarationOrder()
            .Should().Contain(field => field.FieldNumber == 16 && field.Name == "input_file_refs");

        var intent = new WorkflowLlmExecutionIntent
        {
            RunId = "run-1",
            StepId = "step-1",
            SessionId = "session-1",
            Prompt = "hello",
        };
        intent.InputFileRefs.Add(BuildWorkflowFileRef("file-llm"));

        var parsed = WorkflowLlmExecutionIntent.Parser.ParseFrom(intent.ToByteArray());

        parsed.InputFileRefs.Should().ContainSingle().Which.FileId.Should().Be("file-llm");
    }

    [Fact]
    public void WorkflowInteractionNotificationEvent_ShouldRoundtripTypedPayloads()
    {
        var interactionEvent = new WorkflowInteractionNotificationEvent
        {
            RunId = "run-1",
            StepId = "notify-1",
            DeliveryTargetId = "agent-1",
            Interaction = new InteractionSpec
            {
                Title = "Status",
                Body = "Accepted",
            },
        };
        var templateEvent = new WorkflowInteractionNotificationEvent
        {
            RunId = "run-2",
            StepId = "notify-2",
            DeliveryTargetId = "agent-2",
            InteractionTemplate = new InteractionTemplateSpec
            {
                TemplateId = "tpl-1",
            },
        };
        templateEvent.InteractionTemplate.TemplateVariable["title"] = "Deploy";

        var parsedInteraction = WorkflowInteractionNotificationEvent.Parser.ParseFrom(interactionEvent.ToByteArray());
        var parsedTemplate = WorkflowInteractionNotificationEvent.Parser.ParseFrom(templateEvent.ToByteArray());

        parsedInteraction.PayloadCase.Should().Be(WorkflowInteractionNotificationEvent.PayloadOneofCase.Interaction);
        parsedInteraction.Interaction.Title.Should().Be("Status");
        parsedTemplate.PayloadCase.Should().Be(WorkflowInteractionNotificationEvent.PayloadOneofCase.InteractionTemplate);
        parsedTemplate.InteractionTemplate.TemplateId.Should().Be("tpl-1");
        parsedTemplate.InteractionTemplate.TemplateVariable["title"].Should().Be("Deploy");
        WorkflowExecutionMessagesReflection.Descriptor.MessageTypes.Select(x => x.Name)
            .Should().Contain(nameof(WorkflowInteractionNotificationEvent));
    }

    [Fact]
    public void WorkflowSuspendedEvent_ShouldRoundtripTypedInteractionOnFieldThirteen()
    {
        WorkflowSuspendedEvent.Descriptor.Fields.InDeclarationOrder()
            .Should().Contain(field => field.FieldNumber == 13 && field.Name == "interaction");

        var suspended = new WorkflowSuspendedEvent
        {
            RunId = "run-hitl",
            StepId = "approval-hitl",
            SuspensionType = "human_approval",
            DeliveryTargetId = "agent-hitl",
            Interaction = new InteractionSpec
            {
                Title = "Approve release",
                Body = "Release v2",
                Disposition = InteractionDisposition.Ephemeral,
            },
        };
        suspended.Interaction.Actions.Add(new InteractionAction
        {
            Kind = InteractionActionKind.FormSubmit,
            ActionId = "approve",
            Label = "Approve",
            Style = InteractionActionStyle.Primary,
        });

        var parsed = WorkflowSuspendedEvent.Parser.ParseFrom(suspended.ToByteArray());

        parsed.Interaction.Title.Should().Be("Approve release");
        parsed.Interaction.Body.Should().Be("Release v2");
        parsed.Interaction.Disposition.Should().Be(InteractionDisposition.Ephemeral);
        parsed.Interaction.Actions.Should().ContainSingle();
        parsed.Interaction.Actions[0].Kind.Should().Be(InteractionActionKind.FormSubmit);
        parsed.Interaction.Actions[0].ActionId.Should().Be("approve");
    }

    [Fact]
    public void WorkflowEvents_ShouldSupportMergeHashToStringAndDescriptor()
    {
        var start = new StartWorkflowEvent
        {
            WorkflowName = "wf-merge",
            Input = "in",
        };
        start.Parameters["k"] = "v";

        var mergedStart = new StartWorkflowEvent();
        mergedStart.MergeFrom(start);
        mergedStart.Should().BeEquivalentTo(start);
        mergedStart.GetHashCode().Should().Be(start.GetHashCode());
        mergedStart.ToString().Should().Contain("workflowName");
        ((IMessage)mergedStart).Descriptor.Name.Should().Be(nameof(StartWorkflowEvent));

        var completed = new WorkflowCompletedEvent
        {
            WorkflowName = "wf-merge",
            Success = false,
            Output = "",
            Error = "boom",
        };
        var mergedCompleted = new WorkflowCompletedEvent();
        mergedCompleted.MergeFrom(completed);
        mergedCompleted.Should().BeEquivalentTo(completed);
        mergedCompleted.CalculateSize().Should().BeGreaterThan(0);
        mergedCompleted.ToString().Should().Contain("workflowName");
        ((IMessage)mergedCompleted).Descriptor.Name.Should().Be(nameof(WorkflowCompletedEvent));
        completed.Equals((object?)null).Should().BeFalse();

        var request = new StepRequestEvent
        {
            StepId = "s2",
            StepType = "transform",
            Input = "x",
            TargetRole = "worker",
        };
        request.Parameters["op"] = "uppercase";
        var mergedRequest = new StepRequestEvent();
        mergedRequest.MergeFrom(request);
        mergedRequest.Should().BeEquivalentTo(request);
        mergedRequest.StepParameters.Parameters["op"].Should().Be("uppercase");
        mergedRequest.GetHashCode().Should().Be(request.GetHashCode());
        mergedRequest.ToString().Should().Contain("stepId");
        ((IMessage)mergedRequest).Descriptor.Name.Should().Be(nameof(StepRequestEvent));
        request.Equals((object?)null).Should().BeFalse();

        var stepCompleted = new StepCompletedEvent
        {
            StepId = "s2",
            Success = false,
            Output = "",
            Error = "err",
            WorkerId = "w2",
        };
        stepCompleted.Annotations["m"] = "n";
        var mergedStepCompleted = new StepCompletedEvent();
        mergedStepCompleted.MergeFrom(stepCompleted);
        mergedStepCompleted.Should().BeEquivalentTo(stepCompleted);
        mergedStepCompleted.CalculateSize().Should().BeGreaterThan(0);
        mergedStepCompleted.ToString().Should().Contain("stepId");
        ((IMessage)mergedStepCompleted).Descriptor.Name.Should().Be(nameof(StepCompletedEvent));
        stepCompleted.Equals((object?)null).Should().BeFalse();
    }

    [Fact]
    public void WorkflowEvents_ShouldValidateNullAssignments()
    {
        var start = new StartWorkflowEvent();
        var completed = new WorkflowCompletedEvent();
        var request = new StepRequestEvent();
        var stepCompleted = new StepCompletedEvent();

        Action setStartWorkflowName = () => start.WorkflowName = null!;
        Action setCompletedOutput = () => completed.Output = null!;
        Action setRequestStepId = () => request.StepId = null!;
        Action setStepCompletedWorker = () => stepCompleted.WorkerId = null!;

        setStartWorkflowName.Should().Throw<ArgumentNullException>();
        setCompletedOutput.Should().Throw<ArgumentNullException>();
        setRequestStepId.Should().Throw<ArgumentNullException>();
        setStepCompletedWorker.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void SecureValueCapturedEvent_ShouldRoundtrip()
    {
        var evt = new SecureValueCapturedEvent
        {
            RunId = "run-secure",
            StepId = "step-secure",
            Variable = "api_key",
            Value = "secret-value",
        };

        var parsed = SecureValueCapturedEvent.Parser.ParseFrom(evt.ToByteArray());
        parsed.Should().BeEquivalentTo(evt);
        parsed.Clone().Should().BeEquivalentTo(evt);
        ((IMessage)parsed).Descriptor.Name.Should().Be(nameof(SecureValueCapturedEvent));
    }

    [Fact]
    public void SubWorkflowEvents_ShouldRoundtripAndSupportReflection()
    {
        var invoke = new SubWorkflowInvokeRequestedEvent
        {
            InvocationId = "invoke-1",
            ParentRunId = "run-parent",
            ParentStepId = "step-a",
            WorkflowName = "sub_flow",
            Input = "payload",
            Lifecycle = "singleton",
            RequestedByActorId = "actor-parent",
        };
        var parsedInvoke = SubWorkflowInvokeRequestedEvent.Parser.ParseFrom(invoke.ToByteArray());
        parsedInvoke.Should().BeEquivalentTo(invoke);

        var registered = new SubWorkflowInvocationRegisteredEvent
        {
            InvocationId = "invoke-1",
            ParentRunId = "run-parent",
            ParentStepId = "step-a",
            WorkflowName = "sub_flow",
            ChildActorId = "actor-child",
            ChildRunId = "run-child",
            Lifecycle = "singleton",
            DefinitionActorId = "definition-child",
            DefinitionVersion = 3,
            Input = "payload",
            HandoffPhase = 4,
            DefinitionYaml = "name: sub_flow",
            ScopeId = "scope-a",
        };
        registered.InlineWorkflowYamls["sub_flow"] = "name: sub_flow";
        var parsedRegistered = SubWorkflowInvocationRegisteredEvent.Parser.ParseFrom(registered.ToByteArray());
        parsedRegistered.Should().BeEquivalentTo(registered);

        var advanced = new SubWorkflowInvocationHandoffAdvancedEvent
        {
            InvocationId = "invoke-1",
            ChildRunId = "run-child",
            HandoffPhase = 4,
        };
        var parsedAdvanced = SubWorkflowInvocationHandoffAdvancedEvent.Parser.ParseFrom(advanced.ToByteArray());
        parsedAdvanced.Should().BeEquivalentTo(advanced);

        var completed = new SubWorkflowInvocationCompletedEvent
        {
            InvocationId = "invoke-1",
            ChildRunId = "run-child",
            Success = true,
            Output = "ok",
            Error = "",
        };
        var binding = new SubWorkflowBindingUpsertedEvent
        {
            WorkflowName = "sub_flow",
            ChildActorId = "actor-child",
            Lifecycle = "singleton",
        };

        ((IMessage)registered).Descriptor.Name.Should().Be(nameof(SubWorkflowInvocationRegisteredEvent));
        ((IMessage)advanced).Descriptor.Name.Should().Be(nameof(SubWorkflowInvocationHandoffAdvancedEvent));
        ((IMessage)completed).Descriptor.Name.Should().Be(nameof(SubWorkflowInvocationCompletedEvent));
        ((IMessage)binding).Descriptor.Name.Should().Be(nameof(SubWorkflowBindingUpsertedEvent));
    }

    [Fact]
    public void WorkflowAbstractionsReflection_ShouldExposeAllMessages()
    {
        WorkflowExecutionMessagesReflection.Descriptor.Should().NotBeNull();
        WorkflowExecutionMessagesReflection.Descriptor.MessageTypes.Should().Contain(x => x.Name == nameof(StartWorkflowEvent));
        WorkflowExecutionMessagesReflection.Descriptor.MessageTypes.Should().Contain(x => x.Name == nameof(WorkflowRunForkSeed));
        WorkflowExecutionMessagesReflection.Descriptor.MessageTypes.Should().Contain(x => x.Name == nameof(WorkflowRunForkRequestedEvent));
        WorkflowExecutionMessagesReflection.Descriptor.MessageTypes.Should().Contain(x => x.Name == nameof(WorkflowCompletedEvent));
        WorkflowExecutionMessagesReflection.Descriptor.MessageTypes.Should().Contain(x => x.Name == nameof(StepRequestEvent));
        WorkflowExecutionMessagesReflection.Descriptor.MessageTypes.Should().Contain(x => x.Name == nameof(StepCompletedEvent));
        WorkflowExecutionMessagesReflection.Descriptor.MessageTypes.Should().Contain(x => x.Name == nameof(SubWorkflowInvokeRequestedEvent));
        WorkflowExecutionMessagesReflection.Descriptor.MessageTypes.Should().Contain(x => x.Name == nameof(SubWorkflowBindingUpsertedEvent));
        WorkflowExecutionMessagesReflection.Descriptor.MessageTypes.Should().Contain(x => x.Name == nameof(SubWorkflowInvocationRegisteredEvent));
        WorkflowExecutionMessagesReflection.Descriptor.MessageTypes.Should().Contain(x => x.Name == nameof(SubWorkflowInvocationHandoffAdvancedEvent));
        WorkflowExecutionMessagesReflection.Descriptor.MessageTypes.Should().Contain(x => x.Name == nameof(SecureValueCapturedEvent));
        WorkflowExecutionMessagesReflection.Descriptor.MessageTypes.Should().Contain(x => x.Name == nameof(SubWorkflowInvocationCompletedEvent));
    }

    private static WorkflowFileRef BuildWorkflowFileRef(string fileId) =>
        new()
        {
            FileId = fileId,
            ArtifactId = $"workflow-file://{fileId}",
            SourceKind = WorkflowFileSourceKind.ConnectedServiceResource,
            SourceMessageId = "om_1",
            SourceResourceKey = "image_key_1",
            FileName = $"{fileId}.png",
            MediaType = "image/png",
            SizeBytes = 3,
            Sha256 = $"sha-{fileId}",
            CreatedAtUnixMs = 1710000000000,
            ExpiresAtUnixMs = 1710003600000,
        };
}

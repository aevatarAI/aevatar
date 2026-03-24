using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Runs;
using Aevatar.Workflow.Infrastructure.CapabilityApi;
using FluentAssertions;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class WorkflowCapabilityEndpointsCoverageTests
{
    [Fact]
    public void ChatRunRequestNormalizer_ShouldPreserveWorkflowName_WhenInlineWorkflowBundleIsProvided()
    {
        var input = new ChatInput
        {
            Prompt = "hello",
            Workflow = "auto",
            AgentId = " actor-1 ",
            SessionId = " session-1 ",
            WorkflowYamls = ["name: inline"],
        };

        var result = ChatRunRequestNormalizer.Normalize(input);

        result.Succeeded.Should().BeTrue();
        result.Request.Should().BeEquivalentTo(
            new WorkflowChatRunRequest(
                "hello",
                "auto",
                "actor-1",
                SessionId: "session-1",
                WorkflowYamls: ["name: inline"],
                Metadata: new Dictionary<string, string>()));
    }

    [Fact]
    public void ChatRunRequestNormalizer_ShouldAcceptLegacyWorkflowYamlAlias()
    {
        var input = new ChatInput
        {
            Prompt = "hello",
            AgentId = " actor-1 ",
            WorkflowYaml = "name: inline",
        };

        var result = ChatRunRequestNormalizer.Normalize(input);

        result.Succeeded.Should().BeTrue();
        result.Request.Should().BeEquivalentTo(
            new WorkflowChatRunRequest(
                "hello",
                null,
                "actor-1",
                SessionId: null,
                WorkflowYamls: ["name: inline"],
                Metadata: new Dictionary<string, string>()));
    }

    [Fact]
    public void ChatRunRequestNormalizer_ShouldRejectBlankLegacyWorkflowYaml()
    {
        var input = new ChatInput
        {
            Prompt = "hello",
            WorkflowYaml = "   ",
        };

        var result = ChatRunRequestNormalizer.Normalize(input);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(WorkflowChatRunStartError.InvalidWorkflowYaml);
    }

    [Fact]
    public void ChatRunRequestNormalizer_ShouldRejectMixedLegacyAndBundleWorkflowYaml()
    {
        var input = new ChatInput
        {
            Prompt = "hello",
            WorkflowYaml = "name: legacy",
            WorkflowYamls = ["name: bundle"],
        };

        var result = ChatRunRequestNormalizer.Normalize(input);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(WorkflowChatRunStartError.InvalidWorkflowYaml);
    }

    [Fact]
    public void ChatRunRequestNormalizer_ShouldLeaveWorkflowUnset_WhenCreatingNewRun()
    {
        var input = new ChatInput
        {
            Prompt = "hello",
        };

        var result = ChatRunRequestNormalizer.Normalize(input);

        result.Succeeded.Should().BeTrue();
        result.Request.Should().BeEquivalentTo(
            new WorkflowChatRunRequest(
                "hello",
                null,
                null,
                null,
                Metadata: new Dictionary<string, string>()));
    }

    [Fact]
    public void ChatRunRequestNormalizer_ShouldDerivePromptAndInputParts_FromMultimodalInput()
    {
        var input = new ChatInput
        {
            InputParts =
            [
                new ChatInputContentPart
                {
                    Type = "text",
                    Text = "describe this",
                },
                new ChatInputContentPart
                {
                    Type = "image",
                    Uri = "https://example.com/cat.png",
                    MediaType = "image/png",
                    Name = "cat",
                },
            ],
        };

        var result = ChatRunRequestNormalizer.Normalize(input);

        result.Succeeded.Should().BeTrue();
        result.Request.Should().BeEquivalentTo(
            new WorkflowChatRunRequest(
                "describe this",
                null,
                null,
                null,
                InputParts:
                [
                    new WorkflowChatInputPart
                    {
                        Kind = WorkflowChatInputPartKind.Text,
                        Text = "describe this",
                    },
                    new WorkflowChatInputPart
                    {
                        Kind = WorkflowChatInputPartKind.Image,
                        Uri = "https://example.com/cat.png",
                        MediaType = "image/png",
                        Name = "cat",
                    },
                ],
                Metadata: new Dictionary<string, string>()));
    }

    [Fact]
    public void ChatRunRequestNormalizer_ShouldDerivePlaceholderPrompt_FromMediaOnlyInput()
    {
        var input = new ChatInput
        {
            InputParts =
            [
                new ChatInputContentPart
                {
                    Type = "image",
                    Uri = "https://example.com/cat.png",
                },
                new ChatInputContentPart
                {
                    Type = "audio",
                    Uri = "https://example.com/cat.wav",
                },
            ],
        };

        var result = ChatRunRequestNormalizer.Normalize(input);

        result.Succeeded.Should().BeTrue();
        result.Request!.Prompt.Should().Be("[image], [audio]");
        result.Request.InputParts.Should().HaveCount(2);
    }

    [Fact]
    public void ChatRunRequestNormalizer_ShouldAcceptPdfInputWithoutPrompt()
    {
        var input = new ChatInput
        {
            InputParts =
            [
                new ChatInputContentPart
                {
                    Type = "pdf",
                    Uri = "https://example.com/spec.pdf",
                    MediaType = "application/pdf",
                    Name = "spec",
                },
            ],
        };

        var result = ChatRunRequestNormalizer.Normalize(input);

        result.Succeeded.Should().BeTrue();
        result.Request!.Prompt.Should().Be("[pdf]");
        result.Request.InputParts.Should().ContainSingle();
        result.Request.InputParts![0].Kind.Should().Be(WorkflowChatInputPartKind.Pdf);
        result.Request.InputParts[0].Uri.Should().Be("https://example.com/spec.pdf");
        result.Request.InputParts[0].MediaType.Should().Be("application/pdf");
    }

    [Fact]
    public void ChatRunRequestNormalizer_ShouldRejectBlankPrompt_WhenNoMultimodalInput()
    {
        var input = new ChatInput
        {
            Prompt = "   ",
        };

        var result = ChatRunRequestNormalizer.Normalize(input);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(WorkflowChatRunStartError.PromptRequired);
    }

    [Fact]
    public void ChatRunRequestNormalizer_ShouldRejectUnsupportedOnlyInputParts_WhenPromptMissing()
    {
        var input = new ChatInput
        {
            InputParts =
            [
                new ChatInputContentPart
                {
                    Type = "foo",
                },
            ],
        };

        var result = ChatRunRequestNormalizer.Normalize(input);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(WorkflowChatRunStartError.PromptRequired);
    }

    [Fact]
    public void ChatRunRequestNormalizer_ShouldExtractLegacyScopeIdIntoTypedField()
    {
        var input = new ChatInput
        {
            Prompt = "hello",
            Metadata = new Dictionary<string, string>
            {
                ["scope_id"] = "user-1",
            },
        };

        var result = ChatRunRequestNormalizer.Normalize(input);

        result.Succeeded.Should().BeTrue();
        result.Request!.ScopeId.Should().Be("user-1");
        result.Request.Metadata.Should().NotContainKey(WorkflowRunCommandMetadataKeys.ScopeId);
        result.Request.Metadata.Should().NotContainKey("scope_id");
    }

    [Fact]
    public void ChatRunRequestNormalizer_ShouldExtractScopeIdCaseInsensitively()
    {
        var input = new ChatInput
        {
            Prompt = "hello",
            Metadata = new Dictionary<string, string>
            {
                ["Scope_Id"] = "user-1",
                ["WORKFLOW.SCOPE_ID"] = "user-1",
            },
        };

        var result = ChatRunRequestNormalizer.Normalize(input);

        result.Succeeded.Should().BeTrue();
        result.Request!.ScopeId.Should().Be("user-1");
        result.Request.Metadata.Should().NotContainKey("Scope_Id");
        result.Request.Metadata.Should().NotContainKey("WORKFLOW.SCOPE_ID");
        result.Request.Metadata.Should().BeEmpty();
    }

    [Fact]
    public void ChatRunRequestNormalizer_ShouldRejectConflictingScopeIds()
    {
        var input = new ChatInput
        {
            Prompt = "hello",
            ScopeId = "scope-a",
            Metadata = new Dictionary<string, string>
            {
                ["Scope_Id"] = "scope-b",
            },
        };

        var result = ChatRunRequestNormalizer.Normalize(input);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(WorkflowChatRunStartError.ConflictingScopeId);
    }

    [Fact]
    public void CapabilityTraceContext_CreateAcceptedPayload_ShouldUseReceiptValues()
    {
        var receipt = new WorkflowChatRunAcceptedReceipt("actor-1", "direct", "cmd-1", "corr-1");

        var payload = CapabilityTraceContext.CreateAcceptedPayload(receipt);

        payload.CommandId.Should().Be("cmd-1");
        payload.CorrelationId.Should().Be("corr-1");
        payload.ActorId.Should().Be("actor-1");
    }

    [Fact]
    public void CapabilityTraceContext_ResolveCorrelationId_ShouldFallbackToCommandId()
    {
        var correlationId = CapabilityTraceContext.ResolveCorrelationId("", "cmd-1");

        correlationId.Should().Be("cmd-1");
    }
}

using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Infrastructure.CapabilityApi;
using FluentAssertions;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class ChatRunRequestNormalizerEmptyInputTests
{
    [Fact]
    public void Normalize_ShouldRejectEmptyInputForTypedDefinitionActorSourceWithoutResolvedMemberWorkflow()
    {
        var result = ChatRunRequestNormalizer.Normalize(new ChatInput
        {
            Prompt = "   ",
            Source = new WorkflowChatSourceInput
            {
                Kind = "definition_actor",
                DefinitionActor = new WorkflowChatDefinitionActorSourceInput
                {
                    ActorId = " actor-bound-member ",
                    WorkflowName = " status-report ",
                },
            },
        });

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(WorkflowChatRunStartError.PromptRequired);
    }

    [Fact]
    public void Normalize_ShouldAllowEmptyInputForResolvedMemberWorkflow()
    {
        var result = ChatRunRequestNormalizer.Normalize(
            new ChatInput
            {
                Prompt = "   ",
                Source = new WorkflowChatSourceInput
                {
                    Kind = "definition_actor",
                    DefinitionActor = new WorkflowChatDefinitionActorSourceInput
                    {
                        ActorId = " actor-bound-member ",
                        WorkflowName = " status-report ",
                    },
                },
            },
            allowEmptyInputForResolvedMemberWorkflow: true);

        result.Succeeded.Should().BeTrue();
        result.Request!.Prompt.Should().BeEmpty();
        result.Request.Source.DefinitionActorSource.Should()
            .Be(new WorkflowChatDefinitionActorSource("actor-bound-member", "status-report"));
    }

    [Fact]
    public void NormalizeHttp_ShouldAllowEmptyResolvedMemberInputAndPreserveCallerCommandId()
    {
        var result = ChatRunRequestNormalizer.Normalize(
            BuildEmptyResolvedMemberHttpInput(),
            allowEmptyInputForResolvedMemberWorkflow: true);

        result.Succeeded.Should().BeTrue();
        result.Request!.Prompt.Should().BeEmpty();
        result.Request.CommandIdSeed.Should().Be("caller-command-1");
        result.Request.Source.DefinitionActorSource.Should()
            .Be(new WorkflowChatDefinitionActorSource("actor-bound-member", "status-report"));
    }

    [Fact]
    public async Task NormalizeHttpAsync_ShouldAllowEmptyResolvedMemberInputAndPreserveCallerCommandId()
    {
        var result = await ChatRunRequestNormalizer.NormalizeAsync(
            BuildEmptyResolvedMemberHttpInput(),
            fileIngressPort: null,
            allowEmptyInputForResolvedMemberWorkflow: true);

        result.Succeeded.Should().BeTrue();
        result.Request!.Prompt.Should().BeEmpty();
        result.Request.CommandIdSeed.Should().Be("caller-command-1");
        result.Request.Source.DefinitionActorSource.Should()
            .Be(new WorkflowChatDefinitionActorSource("actor-bound-member", "status-report"));
    }

    [Fact]
    public void Normalize_ShouldRejectEmptyInputForTypedCatalogWorkflowSource()
    {
        var result = ChatRunRequestNormalizer.Normalize(new ChatInput
        {
            Prompt = "   ",
            Source = new WorkflowChatSourceInput
            {
                Kind = "catalog_workflow",
                CatalogName = new WorkflowChatCatalogNameSourceInput
                {
                    WorkflowName = " auto ",
                },
            },
        });

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(WorkflowChatRunStartError.PromptRequired);
    }

    private static HttpChatInput BuildEmptyResolvedMemberHttpInput() =>
        new()
        {
            Prompt = "   ",
            CommandId = " caller-command-1 ",
            Source = new WorkflowChatSourceInput
            {
                Kind = "definition_actor",
                DefinitionActor = new WorkflowChatDefinitionActorSourceInput
                {
                    ActorId = " actor-bound-member ",
                    WorkflowName = " status-report ",
                },
            },
        };
}

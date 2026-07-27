using Aevatar.AI.ToolProviders.Skills;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Mainnet.Host.Api.Skills;
using Aevatar.Studio.Application.Provisioning;
using Aevatar.Workflow.Application.Abstractions.Runs;
using FluentAssertions;

namespace Aevatar.Capabilities.Tests;

public sealed class UserSkillRunServiceTests
{
    [Fact]
    public async Task InvokeOnceAsync_ShouldUseNormalizedBearerAndPreserveCompleteCallerCredential()
    {
        var fetcher = new RecordingRemoteSkillFetcher(WorkflowSkill());
        var dispatch = new RecordingWorkflowChatDispatch();
        var service = new UserSkillRunService(fetcher, dispatch, new UnusedScheduleProvisioningPort());
        var callerCredential = new WorkflowCallerCredential(
            "  caller-token  ",
            new WorkflowCallerNyxIdAuthority(
                "nyxid",
                string.Empty,
                "nyx-user-alpha",
                "proxy",
                "binding-alpha"));

        var outcome = await service.InvokeOnceAsync(
            "skill-alpha",
            callerCredential,
            "scope-alpha",
            "run the check",
            CancellationToken.None);

        outcome.Succeeded.Should().BeTrue();
        fetcher.AccessToken.Should().Be("caller-token");
        fetcher.SkillGuid.Should().Be("skill-alpha");
        dispatch.Request.Should().NotBeNull();
        dispatch.Request!.ScopeId.Should().Be("scope-alpha");
        dispatch.Request.CallerCredential.Should().BeSameAs(callerCredential);
        dispatch.Request.CallerCredential!.NyxIdAuthority!.ExternalUserId.Should().Be("nyx-user-alpha");
        dispatch.Request.CallerCredential.NyxIdAuthority.BindingId.Should().Be("binding-alpha");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("token with spaces")]
    public async Task InvokeOnceAsync_WhenBearerIsInvalid_ShouldFailBeforeExternalCalls(string? bearerToken)
    {
        var fetcher = new RecordingRemoteSkillFetcher(WorkflowSkill());
        var dispatch = new RecordingWorkflowChatDispatch();
        var service = new UserSkillRunService(fetcher, dispatch, new UnusedScheduleProvisioningPort());

        var outcome = await service.InvokeOnceAsync(
            "skill-alpha",
            new WorkflowCallerCredential(bearerToken),
            "scope-alpha",
            "run the check",
            CancellationToken.None);

        outcome.Should().Be(SkillRunOutcome.Failed(
            "invalid_caller_credential",
            "Caller credential is invalid."));
        fetcher.InvocationCount.Should().Be(0);
        dispatch.Request.Should().BeNull();
    }

    private static SkillDefinition WorkflowSkill() =>
        new()
        {
            Name = "codex-check",
            Description = "Run a managed Codex check.",
            Instructions = "Return CODEX_EXEC_READY.",
            Source = SkillSource.Remote,
            Workflows =
            [
                new SkillWorkflowDescriptor
                {
                    WorkflowId = "codex-check",
                    WorkflowYamls = ["name: codex-check\nsteps: []\n"],
                },
            ],
        };

    private sealed class RecordingRemoteSkillFetcher(SkillDefinition skill) : IRemoteSkillFetcher
    {
        public int InvocationCount { get; private set; }

        public string? AccessToken { get; private set; }

        public string? SkillGuid { get; private set; }

        public Task<SkillDefinition?> FetchSkillAsync(
            string accessToken,
            string nameOrId,
            CancellationToken ct = default)
        {
            InvocationCount++;
            AccessToken = accessToken;
            SkillGuid = nameOrId;
            return Task.FromResult<SkillDefinition?>(skill);
        }
    }

    private sealed class RecordingWorkflowChatDispatch :
        ICommandDispatchService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>
    {
        public WorkflowChatRunRequest? Request { get; private set; }

        public Task<CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>> DispatchAsync(
            WorkflowChatRunRequest command,
            CancellationToken ct = default)
        {
            Request = command;
            return Task.FromResult(CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>.Success(
                new WorkflowChatRunAcceptedReceipt(
                    "run-alpha",
                    "codex-check",
                    "command-alpha",
                    "correlation-alpha")));
        }
    }

    private sealed class UnusedScheduleProvisioningPort : IWorkflowScheduleProvisioningPort
    {
        public Task<WorkflowScheduleProvisioningResult> ProvisionAsync(
            WorkflowScheduleProvisioningRequest request,
            CancellationToken ct = default) =>
            throw new NotSupportedException("This test exercises one-shot skill invocation only.");
    }
}

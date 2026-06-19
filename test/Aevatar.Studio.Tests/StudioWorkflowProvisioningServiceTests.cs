using Aevatar.AI.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Application.Studio.Services;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Tests;

/// <summary>
/// Unit tests for the one-call workflow provisioning service (C1). The service
/// is a pure composition over <see cref="Aevatar.Studio.Application.Studio.Abstractions.IStudioMemberService"/>
/// and <see cref="IServiceInvocationPort"/>, so these tests assert the
/// orchestration contract:
/// <list type="bullet">
///   <item>happy path = create → bind → poll-until-succeeded → invoke, threading
///   the scope through every call, returning the run id + links;</item>
///   <item>bind timeout = pending status, no invoke, no run id;</item>
///   <item>terminal bind failure = surfaced as an exception;</item>
///   <item><c>runImmediately = false</c> = bound, but no invoke.</item>
/// </list>
/// Time is driven by a <see cref="FakeTimeProvider"/> so the bounded poll is
/// deterministic — no wall-clock sleeps, no <c>Task.Delay</c> magic numbers.
/// </summary>
public sealed class StudioWorkflowProvisioningServiceTests
{
    private const string ScopeId = "scope-1";
    private const string OtherScopeId = "scope-2";
    private const string MemberId = "member-1";
    private const string PublishedServiceId = "published-svc-1";
    private const string RevisionId = "rev-1";

    [Fact]
    public async Task ProvisionAsync_HappyPath_CreatesBindsPollsAndInvokes()
    {
        var member = NewRecordingMemberService(
            bindRunStatuses:
            [
                StudioMemberBindingRunStatusNames.AdmissionPending,
                StudioMemberBindingRunStatusNames.Succeeded,
            ]);
        var invocation = new RecordingInvocationPort
        {
            Receipt = new ServiceInvocationAcceptedReceipt { RunId = "run-123" },
        };
        var sut = NewService(member, invocation);

        var response = await sut.ProvisionAsync(
            ScopeId,
            new ProvisionWorkflowRequest(
                DisplayName: "Monitor",
                WorkflowYaml: "name: monitor",
                Prompt: "go"));

        response.BindingStatus.Should().Be(ProvisionWorkflowBindingStatusNames.Succeeded);
        response.RunId.Should().Be("run-123");
        response.MemberId.Should().Be(MemberId);
        response.ScopeId.Should().Be(ScopeId);
        response.PublishedServiceId.Should().Be(PublishedServiceId);
        response.RevisionId.Should().Be(RevisionId);
        response.ObservatoryUrl.Should().Be("/workflow/observatory");

        // create → bind → invoke, all carrying the caller scope.
        member.CreateScopeId.Should().Be(ScopeId);
        member.CreateRequest!.ImplementationKind.Should().Be(MemberImplementationKindNames.Workflow);
        member.BindScopeId.Should().Be(ScopeId);
        member.BindRequest!.Workflow!.WorkflowYamls.Should().ContainSingle().Which.Should().Be("name: monitor");
        member.BindRequest.Workflow.WorkflowId.Should().NotBeNullOrWhiteSpace();
        member.GetBindingRunCallCount.Should().Be(2);

        invocation.Invoked.Should().BeTrue();
        invocation.Request!.EndpointId.Should().Be("chat");
        invocation.Request.Identity.TenantId.Should().Be(ScopeId);
        invocation.Request.Identity.ServiceId.Should().Be(PublishedServiceId);
        var chat = invocation.Request.Payload.Unpack<ChatRequestEvent>();
        chat.Prompt.Should().Be("go");
        chat.ScopeId.Should().Be(ScopeId);
    }

    [Fact]
    public async Task ProvisionAsync_BindTimesOut_ReturnsPendingAndDoesNotInvoke()
    {
        // The binding run never reaches a terminal state; the poll must give up
        // at the deadline and return pending without invoking.
        var member = NewRecordingMemberService(
            bindRunStatuses: [StudioMemberBindingRunStatusNames.PlatformBindingPending],
            repeatLastStatus: true);
        var invocation = new RecordingInvocationPort();
        var sut = NewService(member, invocation, out var time);

        // Advance the clock past the deadline on each poll so the bounded loop exits.
        member.OnGetBindingRun = () => time.Advance(TimeSpan.FromSeconds(1));

        var response = await sut.ProvisionAsync(
            ScopeId,
            new ProvisionWorkflowRequest(
                DisplayName: "Monitor",
                WorkflowYaml: "name: monitor",
                Prompt: "go",
                BindTimeoutSeconds: 2));

        response.BindingStatus.Should().Be(ProvisionWorkflowBindingStatusNames.Pending);
        response.RunId.Should().BeNull();
        response.PublishedServiceId.Should().BeNull();
        response.MemberId.Should().Be(MemberId);
        invocation.Invoked.Should().BeFalse();
    }

    [Fact]
    public async Task ProvisionAsync_BindFails_SurfacesError()
    {
        var member = NewRecordingMemberService(
            bindRunStatuses: [StudioMemberBindingRunStatusNames.Failed],
            failure: new StudioMemberBindingFailureResponse(
                "PLATFORM_BIND_FAILED",
                "workflow yaml rejected",
                DateTimeOffset.UtcNow));
        var invocation = new RecordingInvocationPort();
        var sut = NewService(member, invocation);

        var act = async () => await sut.ProvisionAsync(
            ScopeId,
            new ProvisionWorkflowRequest(
                DisplayName: "Monitor",
                WorkflowYaml: "name: monitor"));

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("workflow yaml rejected");
        invocation.Invoked.Should().BeFalse();
    }

    [Fact]
    public async Task ProvisionAsync_BindingRunNotFoundInitially_KeepsPollingUntilMaterialized()
    {
        // Regression (live 500): the binding-run read model is eventually consistent.
        // The first polls throw StudioMemberBindingRunNotFoundException before the run
        // record materializes. The bounded poll must treat that not-found window as
        // "not ready yet" and keep polling — NOT surface it as a failure.
        var member = NewRecordingMemberService(
            bindRunStatuses: [StudioMemberBindingRunStatusNames.Succeeded]);
        member.NotFoundThrowsRemaining = 2;
        var invocation = new RecordingInvocationPort
        {
            Receipt = new ServiceInvocationAcceptedReceipt { RunId = "run-late" },
        };
        var sut = NewService(member, invocation);

        var response = await sut.ProvisionAsync(
            ScopeId,
            new ProvisionWorkflowRequest(DisplayName: "Monitor", WorkflowYaml: "name: monitor", Prompt: "go"));

        response.BindingStatus.Should().Be(ProvisionWorkflowBindingStatusNames.Succeeded);
        response.RunId.Should().Be("run-late");
        invocation.Invoked.Should().BeTrue();
    }

    [Fact]
    public async Task ProvisionAsync_BindingRunNotFoundUntilTimeout_ReturnsPendingNotError()
    {
        // If the binding run never materializes within the timeout, the service
        // returns pending (202) — never a 500 from an uncaught not-found.
        var member = NewRecordingMemberService(
            bindRunStatuses: [StudioMemberBindingRunStatusNames.Succeeded]);
        member.NotFoundThrowsRemaining = int.MaxValue;
        var invocation = new RecordingInvocationPort();
        var sut = NewService(member, invocation, out var time);
        member.OnGetBindingRun = () => time.Advance(TimeSpan.FromSeconds(1));

        var response = await sut.ProvisionAsync(
            ScopeId,
            new ProvisionWorkflowRequest(
                DisplayName: "Monitor", WorkflowYaml: "name: monitor", Prompt: "go", BindTimeoutSeconds: 2));

        response.BindingStatus.Should().Be(ProvisionWorkflowBindingStatusNames.Pending);
        response.RunId.Should().BeNull();
        invocation.Invoked.Should().BeFalse();
    }

    [Fact]
    public async Task ProvisionAsync_RunImmediatelyFalse_BindsButDoesNotInvoke()
    {
        var member = NewRecordingMemberService(
            bindRunStatuses: [StudioMemberBindingRunStatusNames.Succeeded]);
        var invocation = new RecordingInvocationPort();
        var sut = NewService(member, invocation);

        var response = await sut.ProvisionAsync(
            ScopeId,
            new ProvisionWorkflowRequest(
                DisplayName: "Monitor",
                WorkflowYaml: "name: monitor",
                RunImmediately: false));

        response.BindingStatus.Should().Be(ProvisionWorkflowBindingStatusNames.Succeeded);
        response.RunId.Should().BeNull();
        response.PublishedServiceId.Should().Be(PublishedServiceId);
        invocation.Invoked.Should().BeFalse();
    }

    [Fact]
    public async Task ProvisionAsync_FallsBackToCommandId_WhenReceiptHasNoRunId()
    {
        var member = NewRecordingMemberService(
            bindRunStatuses: [StudioMemberBindingRunStatusNames.Succeeded]);
        var invocation = new RecordingInvocationPort
        {
            // Dispatcher echoes the command id as the run id when none is set.
            ReceiptFactory = request => new ServiceInvocationAcceptedReceipt
            {
                CommandId = request.CommandId,
            },
        };
        var sut = NewService(member, invocation);

        var response = await sut.ProvisionAsync(
            ScopeId,
            new ProvisionWorkflowRequest(
                DisplayName: "Monitor",
                WorkflowYaml: "name: monitor"));

        response.RunId.Should().NotBeNullOrWhiteSpace();
        response.RunId.Should().Be(invocation.Request!.CommandId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ProvisionAsync_RejectsMissingWorkflowYaml(string yaml)
    {
        var member = NewRecordingMemberService(
            bindRunStatuses: [StudioMemberBindingRunStatusNames.Succeeded]);
        var sut = NewService(member, new RecordingInvocationPort());

        var act = async () => await sut.ProvisionAsync(
            ScopeId,
            new ProvisionWorkflowRequest(DisplayName: "Monitor", WorkflowYaml: yaml));

        await act.Should().ThrowAsync<InvalidOperationException>();
        member.CreateInvoked.Should().BeFalse();
    }

    [Fact]
    public async Task ProvisionAsync_ThreadsCallerScope_NotAmbient()
    {
        // The scope is an input parameter; nothing in the service should reach
        // for an ambient context. Passing OtherScopeId must thread it everywhere.
        var member = NewRecordingMemberService(
            bindRunStatuses: [StudioMemberBindingRunStatusNames.Succeeded]);
        var invocation = new RecordingInvocationPort
        {
            Receipt = new ServiceInvocationAcceptedReceipt { RunId = "run-x" },
        };
        var sut = NewService(member, invocation);

        await sut.ProvisionAsync(
            OtherScopeId,
            new ProvisionWorkflowRequest(DisplayName: "Monitor", WorkflowYaml: "name: monitor", Prompt: "p"));

        member.CreateScopeId.Should().Be(OtherScopeId);
        member.BindScopeId.Should().Be(OtherScopeId);
        member.GetBindingRunScopeId.Should().Be(OtherScopeId);
        invocation.Request!.Identity.TenantId.Should().Be(OtherScopeId);
        invocation.Request.Payload.Unpack<ChatRequestEvent>().ScopeId.Should().Be(OtherScopeId);
    }

    private static StudioWorkflowProvisioningService NewService(
        RecordingMemberService member,
        RecordingInvocationPort invocation) =>
        NewService(member, invocation, out _);

    private static StudioWorkflowProvisioningService NewService(
        RecordingMemberService member,
        RecordingInvocationPort invocation,
        out FakeTimeProvider time)
    {
        time = new FakeTimeProvider();
        return new StudioWorkflowProvisioningService(member, invocation, time);
    }

    private static RecordingMemberService NewRecordingMemberService(
        IReadOnlyList<string> bindRunStatuses,
        bool repeatLastStatus = false,
        StudioMemberBindingFailureResponse? failure = null) =>
        new()
        {
            BindRunStatuses = bindRunStatuses,
            RepeatLastStatus = repeatLastStatus,
            Failure = failure,
            PublishedServiceId = PublishedServiceId,
            RevisionId = RevisionId,
            MemberId = MemberId,
        };

    /// <summary>
    /// Hand-rolled spy implementing only the members the provisioning service
    /// uses (create / bind / get-binding-run). Returns a scripted sequence of
    /// binding-run statuses so a test can simulate "pending then succeeded",
    /// "always pending" (timeout), or "failed".
    /// </summary>
    private sealed class RecordingMemberService : Application.Studio.Abstractions.IStudioMemberService
    {
        public IReadOnlyList<string> BindRunStatuses { get; set; } = [];
        public bool RepeatLastStatus { get; set; }
        public StudioMemberBindingFailureResponse? Failure { get; set; }
        public string PublishedServiceId { get; set; } = "published-svc-1";
        public string RevisionId { get; set; } = "rev-1";
        public string MemberId { get; set; } = "member-1";
        public string? TeamId { get; set; }
        public Action? OnGetBindingRun { get; set; }
        public int NotFoundThrowsRemaining { get; set; }

        public bool CreateInvoked { get; private set; }
        public string? CreateScopeId { get; private set; }
        public CreateStudioMemberRequest? CreateRequest { get; private set; }
        public string? BindScopeId { get; private set; }
        public UpdateStudioMemberBindingRequest? BindRequest { get; private set; }
        public string? GetBindingRunScopeId { get; private set; }
        public int GetBindingRunCallCount { get; private set; }

        public Task<StudioMemberSummaryResponse> CreateAsync(
            string scopeId, CreateStudioMemberRequest request, CancellationToken ct = default)
        {
            CreateInvoked = true;
            CreateScopeId = scopeId;
            CreateRequest = request;
            return Task.FromResult(new StudioMemberSummaryResponse(
                MemberId: MemberId,
                ScopeId: scopeId,
                DisplayName: request.DisplayName,
                Description: string.Empty,
                ImplementationKind: request.ImplementationKind,
                LifecycleStage: MemberLifecycleStageNames.Created,
                PublishedServiceId: string.Empty,
                LastBoundRevisionId: null,
                CreatedAt: DateTimeOffset.UtcNow,
                UpdatedAt: DateTimeOffset.UtcNow)
            {
                TeamId = TeamId,
            });
        }

        public Task<StudioMemberBindingAcceptedResponse> BindAsync(
            string scopeId, string memberId, UpdateStudioMemberBindingRequest request, CancellationToken ct = default)
        {
            BindScopeId = scopeId;
            BindRequest = request;
            return Task.FromResult(new StudioMemberBindingAcceptedResponse(
                Status: StudioMemberBindingRunStatusNames.Accepted,
                BindingRunId: "bind-run-1",
                ScopeId: scopeId,
                MemberId: memberId));
        }

        public Task<StudioMemberBindingRunStatusResponse> GetBindingRunAsync(
            string scopeId, string memberId, string bindingRunId, CancellationToken ct = default)
        {
            OnGetBindingRun?.Invoke();
            GetBindingRunScopeId = scopeId;
            if (NotFoundThrowsRemaining > 0)
            {
                NotFoundThrowsRemaining--;
                throw new Application.Studio.Abstractions.StudioMemberBindingRunNotFoundException(
                    scopeId, memberId, bindingRunId);
            }
            var index = GetBindingRunCallCount;
            GetBindingRunCallCount++;

            string status;
            if (index < BindRunStatuses.Count)
                status = BindRunStatuses[index];
            else if (RepeatLastStatus && BindRunStatuses.Count > 0)
                status = BindRunStatuses[^1];
            else
                status = StudioMemberBindingRunStatusNames.Succeeded;

            var succeeded = string.Equals(status, StudioMemberBindingRunStatusNames.Succeeded, StringComparison.Ordinal);
            return Task.FromResult(new StudioMemberBindingRunStatusResponse(
                BindingRunId: bindingRunId,
                ScopeId: scopeId,
                MemberId: memberId,
                Status: status,
                StateVersion: index + 1,
                Failure: Failure,
                UpdatedAt: DateTimeOffset.UtcNow)
            {
                Result = succeeded
                    ? new StudioMemberBindingRunResultResponse(
                        PublishedServiceId: PublishedServiceId,
                        RevisionId: RevisionId,
                        ImplementationKind: MemberImplementationKindNames.Workflow)
                    : null,
            });
        }

        // ---- Unused members (the provisioning service never calls these) ----

        public Task<StudioMemberRosterResponse> ListAsync(
            string scopeId, StudioMemberRosterPageRequest? page = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<StudioMemberDetailResponse> GetAsync(
            string scopeId, string memberId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<StudioMemberBindingViewResponse> GetBindingAsync(
            string scopeId, string memberId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<StudioMemberEndpointContractResponse?> GetEndpointContractAsync(
            string scopeId, string memberId, string endpointId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<StudioMemberBindingActivationResponse> ActivateBindingRevisionAsync(
            string scopeId, string memberId, string revisionId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<StudioMemberBindingRevisionActionResponse> RetireBindingRevisionAsync(
            string scopeId, string memberId, string revisionId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<StudioMemberCommandResponse> UpdateAsync(
            string scopeId, string memberId, UpdateStudioMemberRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingInvocationPort : IServiceInvocationPort
    {
        public ServiceInvocationAcceptedReceipt? Receipt { get; set; }
        public Func<ServiceInvocationRequest, ServiceInvocationAcceptedReceipt>? ReceiptFactory { get; set; }
        public bool Invoked { get; private set; }
        public ServiceInvocationRequest? Request { get; private set; }

        public Task<ServiceInvocationAcceptedReceipt> InvokeAsync(
            ServiceInvocationRequest request, CancellationToken ct = default)
        {
            Invoked = true;
            Request = request;
            var receipt = ReceiptFactory?.Invoke(request)
                ?? Receipt
                ?? new ServiceInvocationAcceptedReceipt { RunId = "run-default" };
            return Task.FromResult(receipt);
        }
    }

    /// <summary>
    /// Minimal manual-advance time provider. Returns immediately-completed
    /// timers so the service's bounded poll never sleeps wall-clock — the test
    /// controls the clock via <see cref="Advance"/>. The service delays via the
    /// injected provider, so test code never sleeps and carries no polling-wait
    /// magic numbers (test-stability guard).
    /// </summary>
    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now += delta;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            // Fire once immediately so an awaited delay scheduled on this provider
            // completes without a real wall-clock wait; the poll loop then
            // re-checks the clock against its deadline.
            callback(state);
            return new ImmediateTimer();
        }

        private sealed class ImmediateTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}

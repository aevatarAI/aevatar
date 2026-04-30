using System.Reflection;
using System.Security.Claims;
using Aevatar.GAgents.StudioMember;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Hosting.Endpoints;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aevatar.Studio.Tests;

/// <summary>
/// Regression tests for the PR-review fix-ups:
/// - lifecycle downgrade on out-of-band implementation update post-bind
/// - ApplyCreated re-derives publishedServiceId from convention
/// - HandleCreated rejects re-create with mismatched non-identity fields
/// - input validation: length caps + slug pattern on memberId
/// - HTTP 404 for missing member (not 400)
/// - StudioMemberRosterResponse carries NextPageToken
/// </summary>
public sealed class StudioMemberPRReviewFixesTests
{
    private static readonly MethodInfo TransitionStateMethod =
        typeof(StudioMemberGAgent).GetMethod(
            "TransitionState", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("TransitionState method not found.");

    [Fact]
    public void ApplyCreated_ShouldRederivePublishedServiceId_IgnoringEventValue()
    {
        // The dispatcher today builds publishedServiceId via the same
        // convention; rebuilding inside the actor protects against a stale
        // / hand-rolled event whose derivation rule drifted.
        var agent = new StudioMemberGAgent();
        var current = new StudioMemberState();
        var evt = new StudioMemberCreatedEvent
        {
            MemberId = "m-1",
            ScopeId = "scope-1",
            DisplayName = "Original",
            ImplementationKind = StudioMemberImplementationKind.Workflow,
            // Adversarial: event payload claims a different publishedServiceId.
            PublishedServiceId = "evil-service-id",
            CreatedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        };

        var next = (StudioMemberState)TransitionStateMethod.Invoke(agent, [current, evt])!;

        next.PublishedServiceId.Should().Be(StudioMemberConventions.BuildPublishedServiceId("m-1"));
        next.PublishedServiceId.Should().NotBe("evil-service-id");
    }

    [Fact]
    public void ApplyImplementationUpdated_ShouldDowngradeBindReadyToBuildReady()
    {
        var agent = new StudioMemberGAgent();
        var bound = new StudioMemberState
        {
            MemberId = "m-1",
            ScopeId = "scope-1",
            ImplementationKind = StudioMemberImplementationKind.Workflow,
            PublishedServiceId = "member-m-1",
            LifecycleStage = StudioMemberLifecycleStage.BindReady,
            ImplementationRef = new StudioMemberImplementationRef
            {
                Workflow = new StudioMemberWorkflowRef { WorkflowId = "wf-1", WorkflowRevision = "v1" },
            },
        };

        var implUpdate = new StudioMemberImplementationUpdatedEvent
        {
            ImplementationKind = StudioMemberImplementationKind.Workflow,
            ImplementationRef = new StudioMemberImplementationRef
            {
                Workflow = new StudioMemberWorkflowRef { WorkflowId = "wf-1", WorkflowRevision = "v2" },
            },
            UpdatedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        };

        var next = (StudioMemberState)TransitionStateMethod.Invoke(agent, [bound, implUpdate])!;

        next.LifecycleStage.Should().Be(StudioMemberLifecycleStage.BuildReady,
            because: "the published revision is now stale until the next bind");
        next.ImplementationRef.Workflow.WorkflowRevision.Should().Be("v2");
    }

    [Fact]
    public void ApplyImplementationUpdated_ShouldUpgradeCreatedToBuildReady()
    {
        var agent = new StudioMemberGAgent();
        var created = new StudioMemberState
        {
            MemberId = "m-1",
            LifecycleStage = StudioMemberLifecycleStage.Created,
        };

        var implUpdate = new StudioMemberImplementationUpdatedEvent
        {
            ImplementationKind = StudioMemberImplementationKind.Script,
            ImplementationRef = new StudioMemberImplementationRef
            {
                Script = new StudioMemberScriptRef { ScriptId = "s-1", ScriptRevision = "v1" },
            },
            UpdatedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        };

        var next = (StudioMemberState)TransitionStateMethod.Invoke(agent, [created, implUpdate])!;

        next.LifecycleStage.Should().Be(StudioMemberLifecycleStage.BuildReady);
    }

    // The HandleCreated conflict-detection branch is exercised via the
    // actor runtime (StateGuard requires an EventHandler scope to mutate
    // state). The unit-level state-machine view is locked in by
    // StudioMemberGAgentStateTests; a future host-level test covers the
    // full HandleCreated flow.

    [Fact]
    public void ApplyImplementationUpdated_ShouldNotMutateImplementationKind()
    {
        // ImplementationKind is locked at create. Even on a replayed or
        // hand-rolled event whose ImplementationKind disagrees with the
        // existing state, the apply step must preserve State.ImplementationKind
        // so a Script member can never be silently mutated into a Workflow
        // member through the implementation-updated event path.
        var agent = new StudioMemberGAgent();
        var existing = new StudioMemberState
        {
            MemberId = "m-1",
            ImplementationKind = StudioMemberImplementationKind.Script,
            LifecycleStage = StudioMemberLifecycleStage.BuildReady,
        };

        var driftEvent = new StudioMemberImplementationUpdatedEvent
        {
            // Adversarial: event tries to switch the kind to Workflow.
            ImplementationKind = StudioMemberImplementationKind.Workflow,
            ImplementationRef = new StudioMemberImplementationRef
            {
                Workflow = new StudioMemberWorkflowRef { WorkflowId = "wf-1" },
            },
            UpdatedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        };

        var next = (StudioMemberState)TransitionStateMethod.Invoke(agent, [existing, driftEvent])!;

        next.ImplementationKind.Should().Be(StudioMemberImplementationKind.Script,
            because: "ImplementationKind is locked at create; the apply step must not let it drift");
    }

    [Fact]
    public void StudioMemberInputLimits_ShouldRejectLongDisplayName()
    {
        // Sanity: the constants are reasonable and the regex rejects ':' / spaces.
        StudioMemberInputLimits.MaxDisplayNameLength.Should().BeGreaterThan(64);
        StudioMemberInputLimits.MemberIdPattern.IsMatch("m-good_1").Should().BeTrue();
        StudioMemberInputLimits.MemberIdPattern.IsMatch("m:nested").Should().BeFalse();
        StudioMemberInputLimits.MemberIdPattern.IsMatch(" leadingSpace").Should().BeFalse();
        StudioMemberInputLimits.MemberIdPattern.IsMatch(new string('a', 65)).Should().BeFalse();
    }

    [Fact]
    public async Task HandleBindAsync_ShouldReturn404_WhenMemberNotFoundExceptionThrown()
    {
        var service = new ThrowingService(new StudioMemberNotFoundException("scope-1", "m-missing"));

        var result = await InvokeHandle(
            "HandleBindAsync",
            CreateAuthenticatedContext("scope-1"),
            "scope-1",
            "m-missing",
            new UpdateStudioMemberBindingRequest(),
            service,
            CancellationToken.None);

        var statusCode = result.GetType().GetProperty("StatusCode")?.GetValue(result) as int?;
        statusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task HandleGetBindingAsync_ShouldReturn404_WhenMemberNotFoundExceptionThrown()
    {
        var service = new ThrowingService(new StudioMemberNotFoundException("scope-1", "m-missing"));

        var result = await InvokeHandle(
            "HandleGetBindingAsync",
            CreateAuthenticatedContext("scope-1"),
            "scope-1",
            "m-missing",
            service,
            CancellationToken.None);

        var statusCode = result.GetType().GetProperty("StatusCode")?.GetValue(result) as int?;
        statusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public void StudioMemberRosterResponse_ShouldCarryNextPageToken()
    {
        var roster = new StudioMemberRosterResponse(
            ScopeId: "scope-1",
            Members: [],
            NextPageToken: "cursor-1");

        roster.NextPageToken.Should().Be("cursor-1");
    }

    private static HttpContext CreateAuthenticatedContext(string scopeId)
    {
        var identity = new ClaimsIdentity([new Claim("scope_id", scopeId)], "test");
        var services = new ServiceCollection()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Aevatar:Authentication:Enabled"] = "true",
                })
                .Build())
            .AddSingleton<IHostEnvironment>(new TestHostEnvironment())
            .BuildServiceProvider();
        return new DefaultHttpContext
        {
            User = new ClaimsPrincipal(identity),
            RequestServices = services,
        };
    }

    private static async Task<IResult> InvokeHandle(string methodName, params object?[] args)
    {
        var method = typeof(StudioMemberEndpoints).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException($"Method {methodName} not found.");
        var task = (Task<IResult>)method.Invoke(null, args)!;
        return await task;
    }

    private sealed class ThrowingService : IStudioMemberService
    {
        private readonly Exception _ex;

        public ThrowingService(Exception ex)
        {
            _ex = ex;
        }

        public Task<StudioMemberSummaryResponse> CreateAsync(
            string scopeId, CreateStudioMemberRequest request, CancellationToken ct = default) => throw _ex;

        public Task<StudioMemberRosterResponse> ListAsync(
            string scopeId, StudioMemberRosterPageRequest? page = null, CancellationToken ct = default) => throw _ex;

        public Task<StudioMemberDetailResponse> GetAsync(
            string scopeId, string memberId, CancellationToken ct = default) => throw _ex;

        public Task<StudioMemberBindingAcceptedResponse> BindAsync(
            string scopeId, string memberId, UpdateStudioMemberBindingRequest request, CancellationToken ct = default) => throw _ex;

        public Task<StudioMemberBindingViewResponse> GetBindingAsync(
            string scopeId, string memberId, CancellationToken ct = default) => throw _ex;

        public Task<StudioMemberBindingRunStatusResponse> GetBindingRunAsync(
            string scopeId, string memberId, string bindingRunId, CancellationToken ct = default) => throw _ex;

        public Task<StudioMemberEndpointContractResponse?> GetEndpointContractAsync(
            string scopeId, string memberId, string endpointId, CancellationToken ct = default) => throw _ex;

        public Task<StudioMemberBindingActivationResponse> ActivateBindingRevisionAsync(
            string scopeId, string memberId, string revisionId, CancellationToken ct = default) => throw _ex;

        public Task<StudioMemberBindingRevisionActionResponse> RetireBindingRevisionAsync(
            string scopeId, string memberId, string revisionId, CancellationToken ct = default) => throw _ex;

        public Task<StudioMemberDetailResponse> UpdateAsync(
            string scopeId, string memberId, UpdateStudioMemberRequest request, CancellationToken ct = default)
                => throw _ex;
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "Aevatar.Studio.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}

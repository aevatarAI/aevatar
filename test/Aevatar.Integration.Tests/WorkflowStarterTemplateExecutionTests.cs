using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Core.Modules;
using Aevatar.Workflow.Core.Primitives;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Integration.Tests;

[Trait("Category", "Integration")]
[Trait("Feature", "WorkflowStarterTemplates")]
public sealed class WorkflowStarterTemplateExecutionTests
{
    private const string RunId = "starter-template-approval-run";
    private const string LlmOutput = "Deterministic reviewed artifact.";
    private const string ApprovalNote = "Approved by the reviewer.";
    private const string RejectionFeedback = "Add the missing evidence before approval.";

    private static readonly IReadOnlyDictionary<string, ApprovalTemplateExpectation> ApprovalTemplates =
        new Dictionary<string, ApprovalTemplateExpectation>(StringComparer.Ordinal)
        {
            ["approval_gated_action"] = new(
                "approve_action_plan",
                "record_released_plan",
                "record_rejected_plan",
                "PLAN APPROVED FOR A SEPARATELY CONFIGURED EXECUTION STEP",
                "PLAN NOT APPROVED",
                3600),
            ["invoice_review_approval"] = new(
                "approve_invoice",
                "record_approved_invoice",
                "record_rejected_invoice",
                "APPROVED FOR PAYMENT PREPARATION ONLY",
                "NOT APPROVED",
                3600),
            ["resume_screening_review"] = new(
                "review_screening",
                "record_reviewed_screening",
                "record_unreviewed_screening",
                "REVIEWED FOR HUMAN FOLLOW-UP",
                "NOT CLEARED FOR FOLLOW-UP",
                3600),
            ["security_alert_triage"] = new(
                "approve_escalation",
                "record_approved_escalation",
                "record_rejected_escalation",
                "APPROVED FOR HUMAN ESCALATION",
                "NOT APPROVED FOR ESCALATION",
                1800),
        };

    public static TheoryData<string, string> ApprovalTemplateDecisions()
    {
        var data = new TheoryData<string, string>();
        foreach (var templateName in ApprovalTemplates.Keys.Order(StringComparer.Ordinal))
        {
            data.Add(templateName, "approve");
            data.Add(templateName, "reject");
            data.Add(templateName, "timeout");
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ApprovalTemplateDecisions))]
    public async Task ApprovalStarterTemplate_ShouldExecuteDecisionThroughKernelAndModules(
        string templateName,
        string decision)
    {
        var expectation = ApprovalTemplates[templateName];
        using var services = new ServiceCollection()
            .AddAevatarWorkflow()
            .BuildServiceProvider();
        var harness = new ApprovalTemplateExecutionHarness(ParseTemplate(templateName), services);

        await harness.StartAsync();

        var approvalRequest = harness.StepRequests
            .Should()
            .ContainSingle(request => request.StepId == expectation.ApprovalStepId)
            .Which;
        approvalRequest.Input.Should().Be(LlmOutput);
        approvalRequest.Parameters.Should().Contain("on_reject", "skip");
        approvalRequest.StepParameters.HumanApproval.TimeoutDefaultDecision.Should()
            .Be(WorkflowHumanApprovalTimeoutDefaultDecision.Reject);

        var suspension = harness.Suspensions.Should().ContainSingle().Which;
        suspension.RunId.Should().Be(RunId);
        suspension.StepId.Should().Be(expectation.ApprovalStepId);
        suspension.SuspensionType.Should().Be("human_approval");
        suspension.TimeoutSeconds.Should().Be(expectation.TimeoutSeconds);

        var timeout = harness.ScheduledCallbacks
            .Should()
            .ContainSingle(callback => callback.Event is WorkflowHumanApprovalTimeoutFiredEvent)
            .Which;
        timeout.DueTime.Should().Be(TimeSpan.FromSeconds(expectation.TimeoutSeconds));
        harness.Completions.Should().BeEmpty();

        await harness.ResolveAsync(decision, expectation.ApprovalStepId, timeout);

        var completion = harness.Completions.Should().ContainSingle().Which;
        completion.Success.Should().BeTrue();
        completion.RunId.Should().Be(RunId);

        var approved = string.Equals(decision, "approve", StringComparison.Ordinal);
        var expectedBranchStepId = approved
            ? expectation.ApprovedBranchStepId
            : expectation.RejectedBranchStepId;
        var skippedBranchStepId = approved
            ? expectation.RejectedBranchStepId
            : expectation.ApprovedBranchStepId;
        var expectedMarker = approved
            ? expectation.ApprovedOutputMarker
            : expectation.RejectedOutputMarker;

        harness.StepRequests.Select(request => request.StepId).Should().Contain(expectedBranchStepId);
        harness.StepRequests.Select(request => request.StepId).Should().NotContain(skippedBranchStepId);
        completion.Output.Should().Contain(expectedMarker);
        completion.Output.Should().Contain(LlmOutput);

        if (approved)
            completion.Output.Should().Contain(ApprovalNote);
        else if (string.Equals(decision, "reject", StringComparison.Ordinal))
            completion.Output.Should().Contain(RejectionFeedback);
    }

    private static WorkflowDefinition ParseTemplate(string templateName)
    {
        var path = Path.Combine(TemplateDirectory(), $"{templateName}.yaml");
        return new WorkflowParser().Parse(File.ReadAllText(path));
    }

    private static string TemplateDirectory() =>
        Path.Combine(FindRepositoryRoot(), "workflow-templates");

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "aevatar.slnx")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root.");
    }

    private static EventEnvelope Envelope(IMessage message) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(message),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication(
                "starter-template-execution-test",
                TopologyAudience.Self),
        };

    private sealed class ApprovalTemplateExecutionHarness
    {
        private const int MaxTransitions = 32;
        private readonly AssignModule _assignModule = new();
        private readonly HumanApprovalModule _approvalModule = new();
        private readonly TestEventHandlerContext _context;
        private readonly Queue<EventEnvelope> _inbox = new();
        private readonly WorkflowLoopModule _kernel = new();

        public ApprovalTemplateExecutionHarness(
            WorkflowDefinition workflow,
            IServiceProvider services)
        {
            _kernel.SetWorkflow(workflow);
            _context = new TestEventHandlerContext(
                services,
                new TestAgent("starter-template-execution-agent", RunId),
                NullLogger.Instance);
        }

        public List<StepRequestEvent> StepRequests { get; } = [];
        public List<WorkflowSuspendedEvent> Suspensions { get; } = [];
        public List<WorkflowCompletedEvent> Completions { get; } = [];
        public IReadOnlyList<ScheduledCallback> ScheduledCallbacks => _context.Scheduled;

        public async Task StartAsync()
        {
            _inbox.Enqueue(Envelope(new StartWorkflowEvent
            {
                RunId = RunId,
                Input = "Review this consequential request.",
            }));
            await PumpAsync();
        }

        public async Task ResolveAsync(
            string decision,
            string approvalStepId,
            ScheduledCallback timeout)
        {
            switch (decision)
            {
                case "approve":
                    _inbox.Enqueue(Envelope(new WorkflowResumedEvent
                    {
                        RunId = RunId,
                        StepId = approvalStepId,
                        Approved = true,
                        UserInput = ApprovalNote,
                    }));
                    break;
                case "reject":
                    _inbox.Enqueue(Envelope(new WorkflowResumedEvent
                    {
                        RunId = RunId,
                        StepId = approvalStepId,
                        Approved = false,
                        Feedback = RejectionFeedback,
                    }));
                    break;
                case "timeout":
                    _inbox.Enqueue(_context.CreateScheduledEnvelope(timeout));
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(decision), decision, "Unknown approval decision.");
            }

            await PumpAsync();
        }

        private async Task PumpAsync()
        {
            var transitions = 0;
            while (_inbox.TryDequeue(out var envelope))
            {
                transitions++;
                if (transitions > MaxTransitions)
                    throw new InvalidOperationException($"Template exceeded {MaxTransitions} execution transitions.");

                var payload = envelope.Payload;
                if (payload is null)
                    continue;

                if (payload.Is(StartWorkflowEvent.Descriptor) ||
                    payload.Is(StepCompletedEvent.Descriptor))
                {
                    await _kernel.HandleAsync(envelope, _context, CancellationToken.None);
                }
                else if (payload.Is(StepRequestEvent.Descriptor))
                {
                    await ExecuteStepAsync(envelope, payload.Unpack<StepRequestEvent>());
                }
                else if (payload.Is(WorkflowResumedEvent.Descriptor) ||
                         payload.Is(WorkflowHumanApprovalTimeoutFiredEvent.Descriptor))
                {
                    await _approvalModule.HandleAsync(envelope, _context, CancellationToken.None);
                }
                else if (payload.Is(WorkflowSuspendedEvent.Descriptor))
                {
                    Suspensions.Add(payload.Unpack<WorkflowSuspendedEvent>());
                }
                else if (payload.Is(WorkflowCompletedEvent.Descriptor))
                {
                    Completions.Add(payload.Unpack<WorkflowCompletedEvent>());
                }

                DrainSelfPublications();
            }
        }

        private async Task ExecuteStepAsync(EventEnvelope envelope, StepRequestEvent request)
        {
            StepRequests.Add(request.Clone());
            switch (request.StepType)
            {
                case "llm_call":
                    _inbox.Enqueue(Envelope(new StepCompletedEvent
                    {
                        StepId = request.StepId,
                        RunId = request.RunId,
                        ExecutionId = request.ExecutionId,
                        Success = true,
                        Output = LlmOutput,
                    }));
                    break;
                case "human_approval":
                    await _approvalModule.HandleAsync(envelope, _context, CancellationToken.None);
                    break;
                case "assign":
                    await _assignModule.HandleAsync(envelope, _context, CancellationToken.None);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Approval starter dispatched unsupported step type '{request.StepType}'.");
            }
        }

        private void DrainSelfPublications()
        {
            var publications = _context.Published.ToList();
            _context.Published.Clear();
            foreach (var (message, audience) in publications)
            {
                if (audience == TopologyAudience.Self)
                    _inbox.Enqueue(Envelope(message));
            }
        }
    }

    private sealed record ApprovalTemplateExpectation(
        string ApprovalStepId,
        string ApprovedBranchStepId,
        string RejectedBranchStepId,
        string ApprovedOutputMarker,
        string RejectedOutputMarker,
        int TimeoutSeconds);
}

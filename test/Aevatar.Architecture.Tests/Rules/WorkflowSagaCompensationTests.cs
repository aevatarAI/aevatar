using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Aevatar.Architecture.Tests.Rules;

public sealed class WorkflowSagaCompensationTests
{
    private static readonly ImmutableArray<SagaSourceFile> SagaSourceFiles = ImmutableArray.Create(
        new SagaSourceFile(
            "src/workflow/Aevatar.Workflow.Core/Execution/WorkflowExecutionKernel.cs",
            RequiredMethods: ImmutableArray.Create(
                "HandleStepCompletedAsync",
                "HandleCompensationRequestAsync",
                "HandleCompensationTransitionAsync",
                "TryStartCompensationOrPublishTerminalFailureAsync",
                "DrainPendingCompensationContinuationAsync",
                "CompleteRunAndPublishAsync",
                "PublishCompensationRequestAsync",
                "DispatchStepAsync",
                "ScheduleStepTimeoutLeaseAsync",
                "ScheduleCompensationPhaseDeadlineAsync",
                "HandleCompensationPhaseDeadlineFiredAsync",
                "CleanupRunAsync",
                "ResolveStepTimeoutMs"),
            RequiredMethodSignatures: ImmutableArray<string>.Empty),
        new SagaSourceFile(
            "src/workflow/Aevatar.Workflow.Core/WorkflowRunGAgent.cs",
            RequiredMethods: ImmutableArray.Create("ContinueCommittedCompensationCompletionAsync"),
            RequiredMethodSignatures: ImmutableArray.Create(
                "IWorkflowExecutionStateHost.RecordCompensationStepCompletionAsync",
                "IWorkflowExecutionStateHost.RecordCompensationPhaseDeadlineExceededAsync")),
        new SagaSourceFile(
            "src/workflow/Aevatar.Workflow.Core/Validation/WorkflowValidator.cs",
            RequiredMethods: ImmutableArray.Create("Validate"),
            RequiredMethodSignatures: ImmutableArray<string>.Empty));

    [Fact]
    public void WorkflowSagaCompensation_ProductionSources_ShouldSatisfyContract()
    {
        var sources = SagaSourceSet.LoadProductionSources(SagaSourceFiles);

        AssertSagaCompensationContract(sources);
    }

    [Theory]
    [MemberData(nameof(MutationFixtures))]
    public void WorkflowSagaCompensation_MutationFixtures_ShouldFail(
        string name,
        Action<MutableSagaSources> mutate,
        string expectedViolation)
    {
        _ = name;
        var sources = SagaSourceSet.LoadProductionSources(SagaSourceFiles).ToMutable();
        mutate(sources);

        var violations = CollectSagaCompensationViolations(new SagaSourceSet(sources.ToImmutableDictionary()));

        Assert.Contains(
            violations,
            violation => violation.Contains(expectedViolation, StringComparison.Ordinal));
    }

    public static TheoryData<string, Action<MutableSagaSources>, string> MutationFixtures()
    {
        return new TheoryData<string, Action<MutableSagaSources>, string>
        {
            {
                "terminal failure publishes completion directly",
                sources => sources.ReplaceIn(
                    "src/workflow/Aevatar.Workflow.Core/Execution/WorkflowExecutionKernel.cs",
                    "await TryStartCompensationOrPublishTerminalFailureAsync(",
                    "await PublishPreparedWorkflowCompletionAsync("),
                "Terminal workflow failure must enter the compensation decision path"
            },
            {
                "compensation request inlines dispatch",
                sources => sources.ReplaceIn(
                    "src/workflow/Aevatar.Workflow.Core/Execution/WorkflowExecutionKernel.cs",
                    "return ctx.PublishAsync(new CompensationRequestEvent",
                    "DispatchStepAsync(new StepDefinition(), string.Empty, [], new WorkflowExecutionKernelState(), WorkflowStepDispatchKind.Compensation, ctx, ct); return ctx.PublishAsync(new CompensationRequestEvent"),
                "Compensation request publication paths must not inline-advance"
            },
            {
                "sibling timeout terminal failure bypasses compensation decision",
                sources => sources.ReplaceInMethod(
                    "src/workflow/Aevatar.Workflow.Core/Execution/WorkflowExecutionKernel.cs",
                    "HandleTimeoutFiredAsync",
                    "ctx.Logger.LogWarning(\"workflow_loop: step={StepId} timed out after {Ms}ms\", stepId, evt.TimeoutMs);",
                    """
                    ctx.Logger.LogWarning("workflow_loop: step={StepId} timed out after {Ms}ms", stepId, evt.TimeoutMs);
                    await CompleteRunAndPublishAsync(
                        state,
                        new WorkflowCompletedEvent
                        {
                            WorkflowName = _workflow.Name,
                            RunId = runId,
                            Success = false,
                            Error = $"TIMEOUT after {evt.TimeoutMs}ms",
                        },
                        ctx,
                        ct);
                    """),
                "Failed WorkflowCompletedEvent may only be published directly"
            },
            {
                "compensation continuation completes run outside the terminal continuation case",
                sources => sources.ReplaceInMethod(
                    "src/workflow/Aevatar.Workflow.Core/Execution/WorkflowExecutionKernel.cs",
                    "DrainPendingCompensationContinuationAsync",
                    "case WorkflowPendingCompensationOutcomeState.ContinuationOneofCase.CompletedWithoutContinuation:",
                    """
                    case WorkflowPendingCompensationOutcomeState.ContinuationOneofCase.CompletedWithoutContinuation:
                                    await CompleteRunAndPublishAsync(
                                        state,
                                        pending.TerminalCompletion.Clone(),
                                        ctx,
                                        ct,
                                        preserveCurrentStepInputVariable: true);
                    """),
                "Failed WorkflowCompletedEvent may only be published directly"
            },
            {
                "sibling compensation request publication inlines dispatch",
                sources => sources.ReplaceInMethod(
                    "src/workflow/Aevatar.Workflow.Core/WorkflowRunGAgent.cs",
                    "ResumeCompensationAsync",
                    "await PublishAsync(new CompensationRequestEvent",
                    "DispatchStepAsync(); await PublishAsync(new CompensationRequestEvent"),
                "Compensation request publication paths must not inline-advance"
            },
            {
                "validator stops rejecting missing compensation targets",
                sources => sources.ReplaceIn(
                    "src/workflow/Aevatar.Workflow.Core/Validation/WorkflowValidator.cs",
                    "            if (!string.IsNullOrWhiteSpace(step.Compensation) && !stepIds.Contains(step.Compensation))",
                    "            if (!string.IsNullOrWhiteSpace(step.Compensation) && stepIds.Count < 0)"),
                "WorkflowValidator must reject compensation declarations"
            },
            {
                "failed compensation is logged as ordinary step completion",
                sources => sources.ReplaceIn(
                    "src/workflow/Aevatar.Workflow.Core/WorkflowRunGAgent.cs",
                    "await PersistDomainEventAsync(new WorkflowCompensationFailedEvent",
                    "await PersistDomainEventAsync(new CompensationStepCompletedEvent"),
                "Failed compensation completion must persist WorkflowCompensationFailedEvent"
            },
            {
                "dead-letter transition drops terminal workflow completion",
                sources => sources.RemoveStatementInSwitchCaseContaining(
                    "src/workflow/Aevatar.Workflow.Core/Execution/WorkflowExecutionKernel.cs",
                    "HandleCompensationTransitionAsync",
                    "WorkflowCompensationTransitionStatus.CompensationDeadLettered",
                    "CompleteRunAndPublishAsync"),
                "Dead-lettered compensation transition must publish failed WorkflowCompletedEvent"
            },
            {
                "compensation start dead-letter drops terminal workflow completion",
                sources => sources.RemoveStatementInSwitchCaseContaining(
                    "src/workflow/Aevatar.Workflow.Core/Execution/WorkflowExecutionKernel.cs",
                    "TryStartCompensationOrPublishTerminalFailureAsync",
                    "WorkflowCompensationTransitionStatus.CompensationDeadLettered",
                    "CompleteRunAndPublishAsync"),
                "Dead-lettered compensation start result must publish failed WorkflowCompletedEvent"
            },
            {
                "completion helper drops durable preparation",
                sources => sources.ReplaceInMethod(
                    "src/workflow/Aevatar.Workflow.Core/Execution/WorkflowExecutionKernel.cs",
                    "CompleteRunAndPublishAsync",
                    "await PrepareWorkflowCompletionAsync(state, completion, ctx, ct);",
                    "await Task.CompletedTask;"),
                "Prepared workflow completion must be persisted before publication"
            },
            {
                "terminal branch publishes without the completion helper",
                sources => sources.ReplaceInMethod(
                    "src/workflow/Aevatar.Workflow.Core/Execution/WorkflowExecutionKernel.cs",
                    "TryStartCompensationOrPublishTerminalFailureAsync",
                    "await CompleteRunAndPublishAsync(",
                    "await PublishPreparedWorkflowCompletionAsync("),
                "Prepared workflow completion must be persisted before publication"
            },
            {
                "compensation completion stops flowing through the committed hand-off",
                sources => sources.ReplaceInMethod(
                    "src/workflow/Aevatar.Workflow.Core/WorkflowRunGAgent.cs",
                    "RecordCompensationStepCompletionAsync",
                    "return await ContinueCommittedCompensationCompletionAsync(completion, ct);",
                    "return EmptyCompensationResult(WorkflowCompensationTransitionStatus.RejectedStaleOrDuplicate);"),
                "Compensation step completion must continue through the committed completion hand-off"
            },
            {
                "compensation default timeout is renamed",
                sources => sources.ReplaceIn(
                    "src/workflow/Aevatar.Workflow.Core/Execution/WorkflowExecutionKernel.cs",
                    "DefaultCompensationTimeoutMs",
                    "DefaultSagaTimeoutMs"),
                "Compensation dispatch must define the 30s default timeout constant"
            },
            {
                "compensation phase deadline start is dropped",
                sources => sources.ReplaceInMethod(
                    "src/workflow/Aevatar.Workflow.Core/Execution/WorkflowExecutionKernel.cs",
                    "TryStartCompensationOrPublishTerminalFailureAsync",
                    "await EnsureCompensationPhaseDeadlineAsync(state, ctx, ct);",
                    "await Task.CompletedTask;"),
                "Compensation phase start must schedule a durable phase deadline"
            },
            {
                "terminal cleanup stops capturing compensation deadline lease",
                sources => sources.ReplaceInMethod(
                    "src/workflow/Aevatar.Workflow.Core/Execution/WorkflowExecutionKernel.cs",
                    "CleanupRunAsync",
                    "var compensationPhaseDeadlineLease = state.CompensationPhaseDeadlineLease?.Clone();",
                    "var compensationPhaseDeadlineLease = (WorkflowRuntimeCallbackLeaseState?)null;"),
                "Terminal run cleanup must capture the compensation phase deadline lease"
            },
            {
                "deadline dead-letter contract method is renamed",
                sources => sources.ReplaceIn(
                    "src/workflow/Aevatar.Workflow.Core/WorkflowRunGAgent.cs",
                    "IWorkflowExecutionStateHost.RecordCompensationPhaseDeadlineExceededAsync",
                    "IWorkflowExecutionStateHost.RecordCompensationPhaseBudgetExceededAsync"),
                "Unable to locate saga compensation method"
            },
            {
                "deadline failure drops remaining uncompensated count",
                sources => sources.ReplaceInMethod(
                    "src/workflow/Aevatar.Workflow.Core/WorkflowRunGAgent.cs",
                    "RecordCompensationPhaseDeadlineExceededAsync",
                    "RemainingUncompensated = CalculateRemainingUncompensated(",
                    "RemainingUncompensated = Math.Max("),
                "Compensation phase deadline must persist WorkflowCompensationFailedEvent with remaining count from run state"
            },
            {
                "deadline failure returns non dead-letter status",
                sources => sources.ReplaceInMethod(
                    "src/workflow/Aevatar.Workflow.Core/WorkflowRunGAgent.cs",
                    "RecordCompensationPhaseDeadlineExceededAsync",
                    "WorkflowCompensationTransitionStatus.CompensationDeadLettered",
                    "WorkflowCompensationTransitionStatus.NoCompensableLedger"),
                "Compensation phase deadline must return the dead-letter transition"
            },
        };
    }

    private static void AssertSagaCompensationContract(SagaSourceSet sources)
    {
        var violations = CollectSagaCompensationViolations(sources);

        Assert.True(
            violations.Count == 0,
            "Workflow saga compensation contract violations:\n" + string.Join("\n", violations));
    }

    private static IReadOnlyList<string> CollectSagaCompensationViolations(SagaSourceSet sources)
    {
        var violations = new List<string>();
        var index = SagaSyntaxIndex.Create(sources);

        foreach (var source in SagaSourceFiles)
        {
            if (!sources.Contains(source.RelativePath))
            {
                violations.Add($"Missing required saga compensation contract file: {source.RelativePath}");
                continue;
            }

            foreach (var method in source.RequiredMethods)
            {
                if (!index.TryGetMethod(source.RelativePath, method, out _))
                    violations.Add($"Unable to locate saga compensation method: {source.RelativePath}:{method}");
            }

            foreach (var signature in source.RequiredMethodSignatures)
            {
                if (!index.TryGetMethodByQualifiedSignature(source.RelativePath, signature, out _))
                    violations.Add($"Unable to locate saga compensation method: {source.RelativePath}:{signature}");
            }
        }

        if (violations.Count > 0)
            return violations;

        var kernelFile = "src/workflow/Aevatar.Workflow.Core/Execution/WorkflowExecutionKernel.cs";
        var runAgentFile = "src/workflow/Aevatar.Workflow.Core/WorkflowRunGAgent.cs";
        var validatorFile = "src/workflow/Aevatar.Workflow.Core/Validation/WorkflowValidator.cs";
        var kernelRoot = index.GetRoot(kernelFile);
        var runAgentRoot = index.GetRoot(runAgentFile);

        var stepCompleted = index.GetMethod(kernelFile, "HandleStepCompletedAsync");
        if (!SagaSyntaxQueries.Invokes(stepCompleted, "TryStartCompensationOrPublishTerminalFailureAsync"))
        {
            violations.Add(
                "Terminal workflow failure must enter the compensation decision path before publishing failed WorkflowCompletedEvent.");
        }

        var tryStart = index.GetMethod(kernelFile, "TryStartCompensationOrPublishTerminalFailureAsync");
        if (!SagaSyntaxQueries.Invokes(tryStart, "TryStartCompensationAsync"))
            violations.Add("Terminal workflow failure must ask the state host to start compensation.");

        if (!SagaSyntaxQueries.SwitchCaseInvokes(
                tryStart,
                "WorkflowCompensationTransitionStatus.Started",
                "PublishCompensationRequestAsync") ||
            !SagaSyntaxQueries.SwitchCaseInvokes(
                tryStart,
                "WorkflowCompensationTransitionStatus.AlreadyCompensating",
                "PublishCompensationRequestAsync") ||
            !SagaSyntaxQueries.SwitchCaseInvokes(
                tryStart,
                "WorkflowCompensationTransitionStatus.AdvancedAndRequestedNext",
                "PublishCompensationRequestAsync"))
        {
            violations.Add(
                "Compensable terminal failure must publish a compensation request instead of completing failed directly.");
        }

        if (!SagaSyntaxQueries.SwitchCaseCompletesRunDurably(
                tryStart,
                "WorkflowCompensationTransitionStatus.NoCompensableLedger"))
        {
            violations.Add(
                "Failed WorkflowCompletedEvent may only be published directly when no compensable ledger exists.");
        }

        var compensationTransition = index.GetMethod(kernelFile, "HandleCompensationTransitionAsync");
        if (!SagaSyntaxQueries.SwitchCaseCompletesRunDurably(
                compensationTransition,
                "WorkflowCompensationTransitionStatus.CompensationDeadLettered"))
        {
            violations.Add(
                "Dead-lettered compensation transition must publish failed WorkflowCompletedEvent to notify callers.");
        }

        if (!SagaSyntaxQueries.SwitchCaseCompletesRunDurably(
                tryStart,
                "WorkflowCompensationTransitionStatus.CompensationDeadLettered"))
        {
            violations.Add(
                "Dead-lettered compensation start result must publish failed WorkflowCompletedEvent to notify callers.");
        }

        var pendingCompensationDrain = index.GetMethod(kernelFile, "DrainPendingCompensationContinuationAsync");
        if (!SagaSyntaxQueries.SwitchCaseCompletesRunDurably(
                pendingCompensationDrain,
                "ContinuationOneofCase.TerminalCompletion"))
        {
            violations.Add(
                "Persisted terminal compensation continuation must complete the run and publish failed WorkflowCompletedEvent.");
        }

        var completeRunAndPublish = index.GetMethod(kernelFile, "CompleteRunAndPublishAsync");
        if (!SagaSyntaxQueries.Invokes(completeRunAndPublish, "PrepareWorkflowCompletionAsync") ||
            !SagaSyntaxQueries.Invokes(completeRunAndPublish, "PublishPreparedWorkflowCompletionAsync"))
        {
            violations.Add(
                "Prepared workflow completion must be persisted before publication by the run completion helper.");
        }

        var unsafeFailedCompletionSites = SagaSyntaxQueries.WorkflowCompletionSites(kernelFile, kernelRoot)
            .Where(site => SagaSyntaxQueries.WorkflowCompletedSuccessLiteral(site.Invocation) != true)
            .Where(site => !IsAllowedFailedWorkflowCompletionPublication(site))
            .ToList();
        if (unsafeFailedCompletionSites.Count > 0)
        {
            violations.Add(
                "Failed WorkflowCompletedEvent may only be published directly from start-rejection/no-step or compensation terminal contexts. Unexpected sites: " +
                string.Join(", ", unsafeFailedCompletionSites.Select(SagaSyntaxQueries.SiteDisplayName)));
        }

        var completionPublicationsWithoutPreparation = SagaSyntaxQueries.PreparedWorkflowCompletionPublicationSites(
                kernelFile,
                kernelRoot)
            .Where(site => !IsDurableCompletionRecoveryPublication(site))
            .Where(site => !SagaSyntaxQueries.HasPriorInvocationInSameControlFlowBranch(
                site.Invocation,
                "PrepareWorkflowCompletionAsync"))
            .ToList();
        if (completionPublicationsWithoutPreparation.Count > 0)
        {
            violations.Add(
                "Prepared workflow completion must be persisted before publication in the same terminal branch. Unexpected sites: " +
                string.Join(", ", completionPublicationsWithoutPreparation.Select(SagaSyntaxQueries.SiteDisplayName)));
        }

        var compensationRequestSites = SagaSyntaxQueries.EventCreationInvocationSites(
                "CompensationRequestEvent",
                (kernelFile, kernelRoot),
                (runAgentFile, runAgentRoot))
            .Concat(SagaSyntaxQueries.MemberArgumentInvocationSites(
                "NextCompensationRequest",
                (kernelFile, kernelRoot),
                (runAgentFile, runAgentRoot)))
            .ToList();
        var expectedCompensationRequestSites = ImmutableHashSet.Create(
            StringComparer.Ordinal,
            $"{kernelFile}:PublishCompensationRequestAsync:PublishAsync",
            $"{kernelFile}:DrainPendingCompensationContinuationAsync:PublishAsync",
            $"{runAgentFile}:IWorkflowExecutionStateHost.TryStartCompensationAsync:PersistDomainEventAsync",
            $"{runAgentFile}:ContinueCommittedCompensationCompletionAsync:PersistDomainEventAsync",
            $"{runAgentFile}:ResumeCompensationAsync:PublishAsync");
        var actualCompensationRequestSites = compensationRequestSites
            .Select(SagaSyntaxQueries.SiteKey)
            .ToImmutableHashSet(StringComparer.Ordinal);
        if (compensationRequestSites.Count != expectedCompensationRequestSites.Count ||
            !expectedCompensationRequestSites.SetEquals(actualCompensationRequestSites))
        {
            violations.Add(
                "Compensation request publication/persistence sites must stay explicitly enumerated. Missing sites: " +
                string.Join(", ", expectedCompensationRequestSites.Except(actualCompensationRequestSites)) +
                "; unexpected sites: " +
                string.Join(", ", actualCompensationRequestSites.Except(expectedCompensationRequestSites)));
        }

        var nonSelfCompensationRequestPublications = compensationRequestSites
            .Where(site => site.InvocationName == "PublishAsync")
            .Where(site => !SagaSyntaxQueries.InvocationTargetsSelf(site.Invocation))
            .ToList();
        if (nonSelfCompensationRequestPublications.Count > 0)
        {
            violations.Add(
                "Compensation request dispatch must use self-continuation at every publication site. Unexpected sites: " +
                string.Join(", ", nonSelfCompensationRequestPublications.Select(SagaSyntaxQueries.SiteDisplayName)));
        }

        var inlineCompensationRequestPublications = compensationRequestSites
            .Where(site => SagaSyntaxQueries.InvokesAny(
                site.Method,
                "DispatchStepAsync",
                "HandleCompensationRequestAsync",
                "HandleStepCompletedAsync",
                "Run"))
            .ToList();
        if (inlineCompensationRequestPublications.Count > 0)
        {
            violations.Add(
                "Compensation request publication paths must not inline-advance or use callback-thread execution. Unexpected sites: " +
                string.Join(", ", inlineCompensationRequestPublications.Select(SagaSyntaxQueries.SiteDisplayName)));
        }

        if (!SagaSyntaxQueries.HasConstantValue(kernelRoot, "DefaultCompensationTimeoutMs", "30_000"))
            violations.Add("Compensation dispatch must define the 30s default timeout constant.");

        if (!SagaSyntaxQueries.HasConstantValue(kernelRoot, "CompensationPhaseDeadlineMs", "300_000"))
            violations.Add("Compensation phase must define the 5 minute durable deadline constant.");

        var dispatchStep = index.GetMethod(kernelFile, "DispatchStepAsync");
        if (!SagaSyntaxQueries.Invokes(dispatchStep, "ResolveStepTimeoutMs"))
            violations.Add("Step dispatch must resolve timeout from dispatch kind before scheduling.");

        var scheduleStepTimeout = index.GetMethod(kernelFile, "ScheduleStepTimeoutLeaseAsync");
        if (!SagaSyntaxQueries.InvocationContainsAll(
                scheduleStepTimeout,
                "Clamp",
                "effectiveTimeoutMs",
                "100",
                "600_000"))
        {
            violations.Add("Step timeout lease scheduling must clamp the effective timeout.");
        }

        var resolveStepTimeout = index.GetMethod(kernelFile, "ResolveStepTimeoutMs");
        if (!SagaSyntaxQueries.NodeTextContainsAll(
                resolveStepTimeout,
                "WorkflowStepDispatchKind.Compensation",
                "DefaultCompensationTimeoutMs"))
        {
            violations.Add("Compensation dispatch without TimeoutMs must apply DefaultCompensationTimeoutMs.");
        }

        var compensationRequest = index.GetMethod(kernelFile, "HandleCompensationRequestAsync");
        if (!SagaSyntaxQueries.Invokes(compensationRequest, "EnsureCompensationPhaseDeadlineAsync"))
            violations.Add("Compensation request handling must re-arm the phase deadline when needed.");

        if (!SagaSyntaxQueries.Invokes(tryStart, "EnsureCompensationPhaseDeadlineAsync"))
            violations.Add("Compensation phase start must schedule a durable phase deadline.");

        if (!SagaSyntaxQueries.Invokes(compensationTransition, "EnsureCompensationPhaseDeadlineAsync"))
            violations.Add("Compensation continuation must preserve the durable phase deadline.");

        var schedulePhaseDeadline = index.GetMethod(kernelFile, "ScheduleCompensationPhaseDeadlineAsync");
        if (!SagaSyntaxQueries.InvocationContainsAll(
                schedulePhaseDeadline,
                "ScheduleSelfDurableTimeoutAsync",
                "CompensationPhaseDeadlineMs",
                "WorkflowCompensationPhaseDeadlineFiredEvent"))
        {
            violations.Add("Compensation phase deadline must be a durable self-timeout event.");
        }

        var cleanupRun = index.GetMethod(kernelFile, "CleanupRunAsync");
        if (!SagaSyntaxQueries.LocalDeclarationContains(
                cleanupRun,
                "compensationPhaseDeadlineLease",
                "state.CompensationPhaseDeadlineLease?.Clone()"))
        {
            violations.Add("Terminal run cleanup must capture the compensation phase deadline lease.");
        }

        if (!SagaSyntaxQueries.AssignsMemberTo(cleanupRun, "CompensationPhaseDeadlineCallbackId", "string.Empty") ||
            !SagaSyntaxQueries.AssignsMemberTo(cleanupRun, "CompensationPhaseDeadlineLease", "null") ||
            !SagaSyntaxQueries.InvocationContainsAll(cleanupRun, "TryCancelAsync", "compensationPhaseDeadlineLease"))
        {
            violations.Add("Terminal run cleanup must clear and cancel the compensation phase deadline lease.");
        }

        if (!index.GetMethods(validatorFile, "Validate")
                .Any(SagaSyntaxQueries.ContainsMissingCompensationTargetGuard))
        {
            violations.Add(
                "WorkflowValidator must reject compensation declarations that do not resolve to an existing step id.");
        }

        var recordCompletion = index.GetMethodByQualifiedSignature(
            runAgentFile,
            "IWorkflowExecutionStateHost.RecordCompensationStepCompletionAsync");
        if (!SagaSyntaxQueries.Invokes(recordCompletion, "ContinueCommittedCompensationCompletionAsync"))
        {
            violations.Add(
                "Compensation step completion must continue through the committed completion hand-off.");
        }

        var continueCommittedCompletion = index.GetMethod(
            runAgentFile,
            "ContinueCommittedCompensationCompletionAsync");
        if (!SagaSyntaxQueries.IfConditionContains(continueCommittedCompletion, "!completion.Success") ||
            !SagaSyntaxQueries.IfBranchInvokesNew(
                continueCommittedCompletion,
                "!completion.Success",
                "PersistDomainEventAsync",
                "WorkflowCompensationFailedEvent"))
        {
            violations.Add("Failed compensation completion must persist WorkflowCompensationFailedEvent.");
        }

        if (!SagaSyntaxQueries.IfBranchReturnsStatus(
                continueCommittedCompletion,
                "!completion.Success",
                "WorkflowCompensationTransitionStatus.CompensationDeadLettered"))
        {
            violations.Add(
                "Failed compensation completion must return the dead-letter transition instead of logging and dropping.");
        }

        var recordDeadline = index.GetMethodByQualifiedSignature(
            runAgentFile,
            "IWorkflowExecutionStateHost.RecordCompensationPhaseDeadlineExceededAsync");
        if (!SagaSyntaxQueries.InvocationContainsNew(
                recordDeadline,
                "PersistDomainEventAsync",
                "WorkflowCompensationFailedEvent") ||
            !SagaSyntaxQueries.NodeTextContainsAll(recordDeadline, "CalculateRemainingUncompensated"))
        {
            violations.Add(
                "Compensation phase deadline must persist WorkflowCompensationFailedEvent with remaining count from run state.");
        }

        if (!SagaSyntaxQueries.NodeTextContainsAll(
                recordDeadline,
                "WorkflowCompensationTransitionStatus.CompensationDeadLettered"))
        {
            violations.Add("Compensation phase deadline must return the dead-letter transition.");
        }

        return violations;
    }

    private static bool IsAllowedFailedWorkflowCompletionPublication(SagaInvocationSite site)
    {
        var methodName = SagaSyntaxQueries.MethodDisplayName(site.Method);
        if (methodName == "HandleStartWorkflowAsync")
        {
            return SagaSyntaxQueries.HasAncestorIfCondition(site.Invocation, "state.Active") ||
                   SagaSyntaxQueries.HasAncestorIfCondition(site.Invocation, "entry == null");
        }

        if (methodName == "TryStartCompensationOrPublishTerminalFailureAsync")
        {
            return SagaSyntaxQueries.HasAncestorSwitchLabel(
                site.Invocation,
                "WorkflowCompensationTransitionStatus.NoCompensableLedger",
                "WorkflowCompensationTransitionStatus.CompletedAll",
                "WorkflowCompensationTransitionStatus.CompensationDeadLettered");
        }

        if (methodName == "HandleCompensationTransitionAsync")
        {
            return SagaSyntaxQueries.HasAncestorSwitchLabel(
                site.Invocation,
                "WorkflowCompensationTransitionStatus.CompletedAll",
                "WorkflowCompensationTransitionStatus.CompensationDeadLettered");
        }

        if (methodName == "DrainPendingCompensationContinuationAsync")
        {
            // The persisted terminal continuation is the only compensation outcome the
            // kernel may turn into a failed WorkflowCompletedEvent after the actor commit.
            return SagaSyntaxQueries.HasAncestorSwitchLabel(
                site.Invocation,
                "ContinuationOneofCase.TerminalCompletion");
        }

        if (methodName == "CompleteRunAndPublishAsync")
        {
            // The helper only forwards the caller-owned completion into durable preparation;
            // the caller site is what must sit in an allowed terminal context.
            return site.InvocationName == "PrepareWorkflowCompletionAsync";
        }

        return false;
    }

    private static bool IsDurableCompletionRecoveryPublication(SagaInvocationSite site)
    {
        return SagaSyntaxQueries.MethodDisplayName(site.Method) == "HandleAsync" &&
               SagaSyntaxQueries.HasAncestorIfCondition(site.Invocation, "PendingWorkflowCompletion != null");
    }

    private sealed record SagaSourceFile(
        string RelativePath,
        ImmutableArray<string> RequiredMethods,
        ImmutableArray<string> RequiredMethodSignatures);

    private sealed record SagaSourceSet(ImmutableDictionary<string, string> Sources)
    {
        public static SagaSourceSet LoadProductionSources(IEnumerable<SagaSourceFile> sourceFiles)
        {
            var builder = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
            foreach (var sourceFile in sourceFiles)
            {
                var absolutePath = Path.Combine(
                    ChannelSourceIndex.RepoRoot,
                    sourceFile.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(absolutePath))
                    builder[sourceFile.RelativePath] = File.ReadAllText(absolutePath);
            }

            return new SagaSourceSet(builder.ToImmutable());
        }

        public bool Contains(string relativePath) => Sources.ContainsKey(relativePath);

        public MutableSagaSources ToMutable() => new(Sources.ToDictionary(StringComparer.Ordinal));
    }

    public sealed class MutableSagaSources
    {
        private readonly Dictionary<string, string> _sources;

        public MutableSagaSources(Dictionary<string, string> sources)
        {
            _sources = sources;
        }

        public void ReplaceIn(string relativePath, string oldText, string newText)
        {
            if (!_sources.TryGetValue(relativePath, out var source))
                throw new InvalidOperationException($"Missing source fixture: {relativePath}");

            if (!source.Contains(oldText, StringComparison.Ordinal))
                throw new InvalidOperationException($"Fixture text not found in {relativePath}: {oldText}");

            _sources[relativePath] = source.Replace(oldText, newText, StringComparison.Ordinal);
        }

        public void ReplaceInMethod(string relativePath, string methodName, string oldText, string newText)
        {
            if (!_sources.TryGetValue(relativePath, out var source))
                throw new InvalidOperationException($"Missing source fixture: {relativePath}");

            var tree = CSharpSyntaxTree.ParseText(source, path: relativePath);
            var root = tree.GetRoot();
            var method = root.DescendantNodes()
                .OfType<MethodDeclarationSyntax>()
                .FirstOrDefault(x => x.Identifier.ValueText == methodName)
                ?? throw new InvalidOperationException($"Fixture method not found in {relativePath}: {methodName}");

            var methodText = method.ToFullString();
            if (!methodText.Contains(oldText, StringComparison.Ordinal))
                throw new InvalidOperationException($"Fixture text not found in {relativePath}:{methodName}: {oldText}");

            _sources[relativePath] = source.Replace(
                methodText,
                methodText.Replace(oldText, newText, StringComparison.Ordinal),
                StringComparison.Ordinal);
        }

        public void RemoveStatementInSwitchCaseContaining(
            string relativePath,
            string methodName,
            string labelText,
            string callText)
        {
            if (!_sources.TryGetValue(relativePath, out var source))
                throw new InvalidOperationException($"Missing source fixture: {relativePath}");

            var tree = CSharpSyntaxTree.ParseText(source, path: relativePath);
            var root = tree.GetRoot();
            var method = root.DescendantNodes()
                .OfType<MethodDeclarationSyntax>()
                .FirstOrDefault(x => x.Identifier.ValueText == methodName)
                ?? throw new InvalidOperationException($"Fixture method not found in {relativePath}: {methodName}");

            var switchSection = method.DescendantNodes()
                .OfType<SwitchSectionSyntax>()
                .FirstOrDefault(section => section.Labels.Any(label =>
                    label.ToString().Contains(labelText, StringComparison.Ordinal)))
                ?? throw new InvalidOperationException($"Fixture switch case not found in {relativePath}: {labelText}");

            var statement = switchSection.DescendantNodes()
                .OfType<StatementSyntax>()
                .Where(x => x.ToString().Contains(callText, StringComparison.Ordinal))
                .OrderBy(x => x.Span.Length)
                .FirstOrDefault()
                ?? throw new InvalidOperationException($"Fixture statement not found in {relativePath}: {labelText}:{callText}");

            var nextRoot = root.RemoveNode(statement, SyntaxRemoveOptions.KeepNoTrivia)
                ?? throw new InvalidOperationException($"Unable to remove fixture statement in {relativePath}: {callText}");
            _sources[relativePath] = nextRoot.ToFullString();
        }

        public ImmutableDictionary<string, string> ToImmutableDictionary() =>
            _sources.ToImmutableDictionary(StringComparer.Ordinal);
    }

    private sealed class SagaSyntaxIndex
    {
        private readonly Dictionary<string, CompilationUnitSyntax> _roots;

        private SagaSyntaxIndex(Dictionary<string, CompilationUnitSyntax> roots)
        {
            _roots = roots;
        }

        public static SagaSyntaxIndex Create(SagaSourceSet sources)
        {
            var roots = sources.Sources.ToDictionary(
                pair => pair.Key,
                pair => CSharpSyntaxTree.ParseText(pair.Value, path: pair.Key).GetCompilationUnitRoot(),
                StringComparer.Ordinal);
            return new SagaSyntaxIndex(roots);
        }

        public CompilationUnitSyntax GetRoot(string relativePath) =>
            _roots.TryGetValue(relativePath, out var root)
                ? root
                : throw new InvalidOperationException($"Source root not found: {relativePath}");

        public MethodDeclarationSyntax GetMethod(string relativePath, string methodName) =>
            TryGetMethod(relativePath, methodName, out var method)
                ? method
                : throw new InvalidOperationException($"Method not found: {relativePath}:{methodName}");

        public IReadOnlyList<MethodDeclarationSyntax> GetMethods(string relativePath, string methodName)
        {
            if (!_roots.TryGetValue(relativePath, out var root))
                return [];

            return root.DescendantNodes()
                .OfType<MethodDeclarationSyntax>()
                .Where(x => x.Identifier.ValueText == methodName)
                .ToList();
        }

        public bool TryGetMethod(
            string relativePath,
            string methodName,
            [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out MethodDeclarationSyntax? method)
        {
            method = null;
            if (!_roots.TryGetValue(relativePath, out var root))
                return false;

            method = root.DescendantNodes()
                .OfType<MethodDeclarationSyntax>()
                .FirstOrDefault(x => x.Identifier.ValueText == methodName);
            return method is not null;
        }

        public MethodDeclarationSyntax GetMethodByQualifiedSignature(string relativePath, string signature) =>
            TryGetMethodByQualifiedSignature(relativePath, signature, out var method)
                ? method
                : throw new InvalidOperationException($"Method not found: {relativePath}:{signature}");

        public bool TryGetMethodByQualifiedSignature(
            string relativePath,
            string signature,
            [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out MethodDeclarationSyntax? method)
        {
            method = null;
            if (!_roots.TryGetValue(relativePath, out var root))
                return false;

            method = root.DescendantNodes()
                .OfType<MethodDeclarationSyntax>()
                .FirstOrDefault(x => x.ExplicitInterfaceSpecifier is not null &&
                                     $"{x.ExplicitInterfaceSpecifier.Name}.{x.Identifier.ValueText}" == signature);
            return method is not null;
        }
    }

    private sealed record SagaInvocationSite(
        string RelativePath,
        MethodDeclarationSyntax Method,
        InvocationExpressionSyntax Invocation,
        string InvocationName);

    private static class SagaSyntaxQueries
    {
        public static bool Invokes(SyntaxNode node, string methodName) =>
            node.DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Any(x => GetInvocationName(x) == methodName);

        public static bool InvokesAny(SyntaxNode node, params string[] methodNames)
        {
            var set = methodNames.ToHashSet(StringComparer.Ordinal);
            return node.DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Any(x => set.Contains(GetInvocationName(x) ?? string.Empty));
        }

        public static bool SwitchCaseInvokes(SyntaxNode node, string labelText, string methodName) =>
            node.DescendantNodes()
                .OfType<SwitchSectionSyntax>()
                .Where(section => section.Labels.Any(label =>
                    label.ToString().Contains(labelText, StringComparison.Ordinal)))
                .Any(section => Invokes(section, methodName));

        // A terminal switch case completes the run durably either through the shared
        // CompleteRunAndPublishAsync helper or by preparing and then publishing inline.
        public static bool SwitchCaseCompletesRunDurably(SyntaxNode node, string labelText) =>
            SwitchCaseInvokes(node, labelText, "CompleteRunAndPublishAsync") ||
            (SwitchCaseInvokes(node, labelText, "PrepareWorkflowCompletionAsync") &&
             SwitchCaseInvokes(node, labelText, "PublishPreparedWorkflowCompletionAsync"));

        public static bool HasConstantValue(SyntaxNode node, string constantName, string valueText) =>
            node.DescendantNodes()
                .OfType<FieldDeclarationSyntax>()
                .Where(field => field.Modifiers.Any(SyntaxKind.ConstKeyword))
                .SelectMany(field => field.Declaration.Variables)
                .Any(variable =>
                    variable.Identifier.ValueText == constantName &&
                    string.Equals(variable.Initializer?.Value.ToString(), valueText, StringComparison.Ordinal));

        // Every site that hands a WorkflowCompletedEvent into durable preparation, either
        // directly or through the CompleteRunAndPublishAsync helper.
        public static IReadOnlyList<SagaInvocationSite> WorkflowCompletionSites(
            string relativePath,
            CompilationUnitSyntax root) =>
            InvocationSites(relativePath, root)
                .Where(site => site.InvocationName is "PrepareWorkflowCompletionAsync" or "CompleteRunAndPublishAsync")
                .ToList();

        public static IReadOnlyList<SagaInvocationSite> PreparedWorkflowCompletionPublicationSites(
            string relativePath,
            CompilationUnitSyntax root) =>
            InvocationSites(relativePath, root)
                .Where(site => site.InvocationName == "PublishPreparedWorkflowCompletionAsync")
                .ToList();

        public static bool HasPriorInvocationInSameControlFlowBranch(
            InvocationExpressionSyntax invocation,
            string methodName)
        {
            var scopes = invocation.Ancestors()
                .Where(node => node is BlockSyntax or SwitchSectionSyntax)
                .OrderBy(node => node.Span.Length);
            var scope = scopes.FirstOrDefault();
            return scope != null && scope.DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Any(candidate =>
                    candidate.SpanStart < invocation.SpanStart &&
                    GetInvocationName(candidate) == methodName);
        }

        public static IReadOnlyList<SagaInvocationSite> EventCreationInvocationSites(
            string eventType,
            params (string RelativePath, CompilationUnitSyntax Root)[] roots)
        {
            return roots
                .SelectMany(root => InvocationSites(root.RelativePath, root.Root))
                .Where(site => site.Invocation.ArgumentList.Arguments.Any(argument =>
                    argument.DescendantNodesAndSelf()
                        .OfType<ObjectCreationExpressionSyntax>()
                        .Any(creation => TypeName(creation.Type) == eventType)))
                .ToList();
        }

        // Invocation sites that pass a persisted typed member (for example a staged
        // continuation payload) as an argument instead of creating the event inline.
        public static IReadOnlyList<SagaInvocationSite> MemberArgumentInvocationSites(
            string memberName,
            params (string RelativePath, CompilationUnitSyntax Root)[] roots)
        {
            return roots
                .SelectMany(root => InvocationSites(root.RelativePath, root.Root))
                .Where(site => site.Invocation.ArgumentList.Arguments.Any(argument =>
                    argument.DescendantNodesAndSelf()
                        .OfType<MemberAccessExpressionSyntax>()
                        .Any(member => member.Name.Identifier.ValueText == memberName)))
                .ToList();
        }

        public static bool? WorkflowCompletedSuccessLiteral(InvocationExpressionSyntax invocation)
        {
            var creation = invocation.ArgumentList.Arguments
                .SelectMany(argument => argument.DescendantNodesAndSelf().OfType<ObjectCreationExpressionSyntax>())
                .FirstOrDefault(x => TypeName(x.Type) == "WorkflowCompletedEvent");
            if (creation?.Initializer == null)
                return null;

            foreach (var assignment in creation.Initializer.Expressions.OfType<AssignmentExpressionSyntax>())
            {
                if (assignment.Left.ToString() != "Success")
                    continue;

                if (assignment.Right.IsKind(SyntaxKind.TrueLiteralExpression))
                    return true;
                if (assignment.Right.IsKind(SyntaxKind.FalseLiteralExpression))
                    return false;
            }

            return null;
        }

        public static bool InvocationTargetsSelf(InvocationExpressionSyntax invocation) =>
            invocation.ArgumentList.Arguments
                .Skip(1)
                .Any(argument => argument.ToString().Contains("TopologyAudience.Self", StringComparison.Ordinal));

        public static bool HasAncestorSwitchLabel(SyntaxNode node, params string[] labelTexts)
        {
            var labels = labelTexts.ToHashSet(StringComparer.Ordinal);
            return node.Ancestors()
                .OfType<SwitchSectionSyntax>()
                .Any(section => section.Labels.Any(label => labels.Any(expected =>
                    label.ToString().Contains(expected, StringComparison.Ordinal))));
        }

        public static bool HasAncestorIfCondition(SyntaxNode node, string conditionText) =>
            node.Ancestors()
                .OfType<IfStatementSyntax>()
                .Any(ifStatement => ifStatement.Condition.ToString().Contains(conditionText, StringComparison.Ordinal));

        public static string SiteKey(SagaInvocationSite site) =>
            $"{site.RelativePath}:{MethodDisplayName(site.Method)}:{site.InvocationName}";

        public static string SiteDisplayName(SagaInvocationSite site) => SiteKey(site);

        public static string MethodDisplayName(MethodDeclarationSyntax method) =>
            method.ExplicitInterfaceSpecifier is null
                ? method.Identifier.ValueText
                : $"{method.ExplicitInterfaceSpecifier.Name}.{method.Identifier.ValueText}";

        public static bool InvocationContainsAll(SyntaxNode node, string invocationName, params string[] fragments) =>
            node.DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Where(invocation => GetInvocationName(invocation) == invocationName)
                .Any(invocation => TextContainsAll(invocation.ToString(), fragments));

        public static bool InvocationContainsNew(SyntaxNode node, string invocationName, string objectType) =>
            node.DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Any(invocation =>
                    GetInvocationName(invocation) == invocationName &&
                    invocation.ArgumentList.Arguments.Any(argument =>
                        argument.DescendantNodesAndSelf()
                            .OfType<ObjectCreationExpressionSyntax>()
                            .Any(creation => TypeName(creation.Type) == objectType)));

        public static bool NodeTextContainsAll(SyntaxNode node, params string[] fragments) =>
            TextContainsAll(node.ToString(), fragments);

        public static bool LocalDeclarationContains(SyntaxNode node, string variableName, string valueText) =>
            node.DescendantNodes()
                .OfType<LocalDeclarationStatementSyntax>()
                .SelectMany(statement => statement.Declaration.Variables)
                .Any(variable =>
                    variable.Identifier.ValueText == variableName &&
                    string.Equals(variable.Initializer?.Value.ToString(), valueText, StringComparison.Ordinal));

        public static bool AssignsMemberTo(SyntaxNode node, string memberName, string valueText) =>
            node.DescendantNodes()
                .OfType<AssignmentExpressionSyntax>()
                .Any(assignment =>
                    assignment.Left.ToString().EndsWith("." + memberName, StringComparison.Ordinal) &&
                    string.Equals(assignment.Right.ToString(), valueText, StringComparison.Ordinal));

        public static bool ContainsMissingCompensationTargetGuard(SyntaxNode node)
        {
            return node.DescendantNodes()
                .OfType<IfStatementSyntax>()
                .Any(ifStatement =>
                {
                    var condition = ifStatement.Condition.ToString();
                    return condition.Contains("!string.IsNullOrWhiteSpace(step.Compensation)", StringComparison.Ordinal) &&
                           condition.Contains("!stepIds.Contains(step.Compensation)", StringComparison.Ordinal);
                });
        }

        public static bool IfConditionContains(SyntaxNode node, string conditionText) =>
            node.DescendantNodes()
                .OfType<IfStatementSyntax>()
                .Any(ifStatement => ifStatement.Condition.ToString().Contains(conditionText, StringComparison.Ordinal));

        public static bool IfBranchInvokesNew(
            SyntaxNode node,
            string conditionText,
            string invocationName,
            string objectType)
        {
            return node.DescendantNodes()
                .OfType<IfStatementSyntax>()
                .Where(ifStatement => ifStatement.Condition.ToString().Contains(conditionText, StringComparison.Ordinal))
                .Any(ifStatement => ifStatement.Statement
                    .DescendantNodes()
                    .OfType<InvocationExpressionSyntax>()
                    .Any(invocation =>
                        GetInvocationName(invocation) == invocationName &&
                        invocation.ArgumentList.Arguments.Any(argument =>
                            argument.DescendantNodesAndSelf()
                                .OfType<ObjectCreationExpressionSyntax>()
                                .Any(creation => TypeName(creation.Type) == objectType))));
        }

        public static bool IfBranchReturnsStatus(SyntaxNode node, string conditionText, string statusText)
        {
            return node.DescendantNodes()
                .OfType<IfStatementSyntax>()
                .Where(ifStatement => ifStatement.Condition.ToString().Contains(conditionText, StringComparison.Ordinal))
                .Any(ifStatement => ifStatement.Statement
                    .DescendantNodes()
                    .OfType<ReturnStatementSyntax>()
                    .Any(returnStatement => returnStatement.ToString().Contains(statusText, StringComparison.Ordinal)));
        }

        private static IEnumerable<SagaInvocationSite> InvocationSites(
            string relativePath,
            CompilationUnitSyntax root)
        {
            foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                foreach (var invocation in method.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    var invocationName = GetInvocationName(invocation);
                    if (string.IsNullOrWhiteSpace(invocationName))
                        continue;

                    yield return new SagaInvocationSite(relativePath, method, invocation, invocationName);
                }
            }
        }

        private static string? GetInvocationName(InvocationExpressionSyntax invocation)
        {
            return invocation.Expression switch
            {
                IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
                MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
                _ => null,
            };
        }

        private static bool TextContainsAll(string text, params string[] fragments) =>
            fragments.All(fragment => text.Contains(fragment, StringComparison.Ordinal));

        private static string TypeName(TypeSyntax type) =>
            type switch
            {
                IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
                QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
                AliasQualifiedNameSyntax aliasQualified => aliasQualified.Name.Identifier.ValueText,
                _ => type.ToString(),
            };
    }
}

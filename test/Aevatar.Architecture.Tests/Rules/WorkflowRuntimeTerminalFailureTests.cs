using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Aevatar.Architecture.Tests.Rules;

public sealed class WorkflowRuntimeTerminalFailureTests
{
    private const string KernelFile = "src/workflow/Aevatar.Workflow.Core/Execution/WorkflowExecutionKernel.cs";
    private const string BridgeFile = "src/workflow/Aevatar.Workflow.Core/Execution/WorkflowExecutionBridgeModule.cs";
    private const string RunAgentFile = "src/workflow/Aevatar.Workflow.Core/WorkflowRunGAgent.cs";

    [Fact]
    public void WorkflowRuntimeTerminalFailure_ProductionSources_ShouldSatisfyContract()
    {
        var sources = RuntimeFailureSourceSet.LoadProductionSources(KernelFile, BridgeFile, RunAgentFile);

        AssertRuntimeTerminalFailureContract(sources);
    }

    [Theory]
    [MemberData(nameof(MutationFixtures))]
    public void WorkflowRuntimeTerminalFailure_MutationFixtures_ShouldFail(
        string name,
        Action<MutableRuntimeFailureSources> mutate,
        string expectedViolation)
    {
        _ = name;
        var sources = RuntimeFailureSourceSet.LoadProductionSources(KernelFile, BridgeFile, RunAgentFile).ToMutable();
        mutate(sources);

        var violations = CollectRuntimeTerminalFailureViolations(new RuntimeFailureSourceSet(sources.ToImmutableDictionary()));

        Assert.Contains(
            violations,
            violation => violation.Contains(expectedViolation, StringComparison.Ordinal));
    }

    public static TheoryData<string, Action<MutableRuntimeFailureSources>, string> MutationFixtures()
    {
        return new TheoryData<string, Action<MutableRuntimeFailureSources>, string>
        {
            {
                "step dispatch failure handler is dropped",
                sources => sources.ReplaceInMethod(
                    KernelFile,
                    "DispatchStepAsync",
                    "await PublishStepDispatchTerminalFailureAsync(",
                    "await Task.CompletedTask; // "),
                "Step dispatch failures must publish terminal workflow failure through PublishStepDispatchTerminalFailureAsync"
            },
            {
                "compensable dispatch is recorded before executor receipt",
                sources => sources.ReplaceInMethod(
                    KernelFile,
                    "DispatchStepAsync",
                    "await ctx.PublishAsync(request, TopologyAudience.Self, ct);",
                    """
                    await RecordCompensableStepDispatchAsync(step, idempotency, ct);
                                await ctx.PublishAsync(request, TopologyAudience.Self, ct);
                    """),
                "Compensable step dispatch must be recorded only after StepRequestEvent publish succeeds"
            },
            {
                "dispatch terminal failure bypasses compensation decision",
                sources => sources.ReplaceInMethod(
                    KernelFile,
                    "PublishStepDispatchTerminalFailureAsync",
                    "await TryStartCompensationOrPublishTerminalFailureAsync(",
                    "await PublishWorkflowCompletedAsync("),
                "Step dispatch terminal failure must enter compensation decision path"
            },
            {
                "dispatch failure fabricates an empty run ledger",
                sources => sources.ReplaceInMethod(
                    KernelFile,
                    "PublishStepDispatchTerminalFailureAsync",
                    "terminalStep: null,\n                ct);",
                    "terminalStep: null,\n                ct,\n                knownNoCompensableLedger: true);"),
                "Step dispatch failure must query the run-level compensation ledger"
            },
            {
                "executor exception publishes no failed completion",
                sources => sources.ReplaceInMethod(
                    BridgeFile,
                    "HandleAsync",
                    "new StepCompletedEvent",
                    "new WorkflowCompletedEvent"),
                "Step executor exceptions must publish failed StepCompletedEvent"
            },
            {
                "executor exception does not mark outcome uncertain",
                sources => sources.ReplaceInMethod(
                    BridgeFile,
                    "HandleAsync",
                    "FailureOutcome = WorkflowStepFailureOutcome.OutcomeUncertain,",
                    string.Empty),
                "Step executor exceptions must mark failure outcome uncertain"
            },
            {
                "completion exception bypasses compensation decision",
                sources => sources.ReplaceInMethod(
                    KernelFile,
                    "HandleStepCompletedAsync",
                    "await TryStartCompensationOrPublishTerminalFailureAsync(",
                    "await PublishWorkflowCompletedAsync("),
                "Step completion handling failures must enter compensation decision path"
            },
            {
                "start publish bypasses terminalization helper",
                sources => sources.ReplaceIn(
                    RunAgentFile,
                    "await PublishStartWorkflowOrTerminalFailureAsync(start, request.SessionId, CancellationToken.None);",
                    "await PublishAsync(start, TopologyAudience.Self);"),
                "Workflow start dispatch must go through PublishStartWorkflowOrTerminalFailureAsync"
            },
            {
                "start terminalization stops committing workflow completion",
                sources => sources.ReplaceInMethod(
                    RunAgentFile,
                    "PublishStartWorkflowOrTerminalFailureAsync",
                    "await HandleWorkflowCompleted(terminal);",
                    "await PublishAsync(terminal, TopologyAudience.Self);"),
                "Workflow start dispatch failure must commit terminal completion through HandleWorkflowCompleted"
            },
        };
    }

    private static void AssertRuntimeTerminalFailureContract(RuntimeFailureSourceSet sources)
    {
        var violations = CollectRuntimeTerminalFailureViolations(sources);

        Assert.True(
            violations.Count == 0,
            "Workflow runtime terminal failure contract violations:\n" + string.Join("\n", violations));
    }

    private static IReadOnlyList<string> CollectRuntimeTerminalFailureViolations(RuntimeFailureSourceSet sources)
    {
        var violations = new List<string>();
        var index = RuntimeFailureSyntaxIndex.Create(sources);

        foreach (var requiredFile in new[] { KernelFile, BridgeFile, RunAgentFile })
        {
            if (!sources.Contains(requiredFile))
                violations.Add($"Missing required workflow runtime terminal failure contract file: {requiredFile}");
        }

        if (violations.Count > 0)
            return violations;

        var dispatchStep = index.GetMethod(KernelFile, "DispatchStepAsync");
        if (!RuntimeFailureSyntaxQueries.HasCatchInvoking(dispatchStep, "PublishStepDispatchTerminalFailureAsync"))
        {
            violations.Add(
                "Step dispatch failures must publish terminal workflow failure through PublishStepDispatchTerminalFailureAsync.");
        }

        if (!RuntimeFailureSyntaxQueries.StepRequestPublishBeforeCompensableRecord(dispatchStep))
        {
            violations.Add(
                "Compensable step dispatch must be recorded only after StepRequestEvent publish succeeds.");
        }

        var publishDispatchFailure = index.GetMethod(KernelFile, "PublishStepDispatchTerminalFailureAsync");
        if (!RuntimeFailureSyntaxQueries.Invokes(publishDispatchFailure, "TryStartCompensationOrPublishTerminalFailureAsync"))
        {
            violations.Add(
                "Step dispatch terminal failure must enter compensation decision path.");
        }

        if (!RuntimeFailureSyntaxQueries.CreatesFailedWorkflowCompletedEventUsing(
                publishDispatchFailure,
                "WorkflowRuntimeFailureMessages.StepDispatchFailed"))
        {
            violations.Add(
                "Step dispatch terminal failure must publish sanitized step_dispatch_failed WorkflowCompletedEvent.");
        }

        if (RuntimeFailureSyntaxQueries.HasArgumentNameWithLiteral(
                publishDispatchFailure,
                "knownNoCompensableLedger",
                "true"))
        {
            violations.Add(
                "Step dispatch failure must query the run-level compensation ledger instead of inferring it from the current step.");
        }

        var bridgeHandle = index.GetMethod(BridgeFile, "HandleAsync");
        if (!RuntimeFailureSyntaxQueries.HasCatchFilteredByDescriptor(bridgeHandle, "StepRequestEvent.Descriptor"))
        {
            violations.Add(
                "Step executor exceptions must be caught only at the StepRequestEvent boundary.");
        }

        if (!RuntimeFailureSyntaxQueries.CatchCreatesFailedStepCompletedEventUsing(
                bridgeHandle,
                "WorkflowRuntimeFailureMessages.StepExecutorFailed"))
        {
            violations.Add(
                "Step executor exceptions must publish failed StepCompletedEvent with sanitized step_executor_failed error.");
        }

        if (!RuntimeFailureSyntaxQueries.NodeTextContainsAll(
                bridgeHandle,
                "FailureOutcome = WorkflowStepFailureOutcome.OutcomeUncertain"))
        {
            violations.Add(
                "Step executor exceptions must mark failure outcome uncertain.");
        }

        var stepCompleted = index.GetMethod(KernelFile, "HandleStepCompletedAsync");
        if (!RuntimeFailureSyntaxQueries.HasCatchInvoking(stepCompleted, "TryStartCompensationOrPublishTerminalFailureAsync"))
        {
            violations.Add(
                "Step completion handling failures must enter compensation decision path.");
        }

        if (!RuntimeFailureSyntaxQueries.NodeTextContainsAll(
                stepCompleted,
                "WorkflowRuntimeFailureMessages.StepCompletionHandlingFailed"))
        {
            violations.Add(
                "Step completion handling failures must publish sanitized step_completion_handling_failed errors.");
        }

        var workflowChat = index.GetMethod(RunAgentFile, "HandleChatRequest");
        if (!RuntimeFailureSyntaxQueries.Invokes(workflowChat, "PublishStartWorkflowOrTerminalFailureAsync"))
        {
            violations.Add(
                "Workflow start dispatch must go through PublishStartWorkflowOrTerminalFailureAsync for chat requests.");
        }

        var executeWorkflow = index.GetMethod(RunAgentFile, "HandleReplaceWorkflowDefinitionAndExecute");
        if (!RuntimeFailureSyntaxQueries.Invokes(executeWorkflow, "PublishStartWorkflowOrTerminalFailureAsync"))
        {
            violations.Add(
                "Workflow start dispatch must go through PublishStartWorkflowOrTerminalFailureAsync for direct executions.");
        }

        var publishStartFailure = index.GetMethod(RunAgentFile, "PublishStartWorkflowOrTerminalFailureAsync");
        if (!RuntimeFailureSyntaxQueries.HasCatchInvoking(publishStartFailure, "HandleWorkflowCompleted"))
        {
            violations.Add(
                "Workflow start dispatch failure must commit terminal completion through HandleWorkflowCompleted.");
        }

        if (!RuntimeFailureSyntaxQueries.CreatesFailedWorkflowCompletedEventUsing(
                publishStartFailure,
                "WorkflowRuntimeFailureMessages.StartDispatchFailed"))
        {
            violations.Add(
                "Workflow start dispatch failure must create sanitized start_dispatch_failed WorkflowCompletedEvent.");
        }

        return violations;
    }

    private sealed record RuntimeFailureSourceSet(IReadOnlyDictionary<string, string> Sources)
    {
        public static RuntimeFailureSourceSet LoadProductionSources(params string[] relativePaths)
        {
            var sources = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var relativePath in relativePaths)
            {
                var absolutePath = Path.Combine(
                    ChannelSourceIndex.RepoRoot,
                    relativePath.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(absolutePath))
                    sources[relativePath] = File.ReadAllText(absolutePath);
            }

            return new RuntimeFailureSourceSet(sources);
        }

        public bool Contains(string relativePath) => Sources.ContainsKey(relativePath);

        public MutableRuntimeFailureSources ToMutable() => new(Sources.ToDictionary(StringComparer.Ordinal));
    }

    public sealed class MutableRuntimeFailureSources
    {
        private readonly Dictionary<string, string> _sources;

        public MutableRuntimeFailureSources(Dictionary<string, string> sources)
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

        public IReadOnlyDictionary<string, string> ToImmutableDictionary() =>
            _sources.ToDictionary(StringComparer.Ordinal);
    }

    private sealed class RuntimeFailureSyntaxIndex
    {
        private readonly Dictionary<string, CompilationUnitSyntax> _roots;

        private RuntimeFailureSyntaxIndex(Dictionary<string, CompilationUnitSyntax> roots)
        {
            _roots = roots;
        }

        public static RuntimeFailureSyntaxIndex Create(RuntimeFailureSourceSet sources)
        {
            var roots = sources.Sources.ToDictionary(
                pair => pair.Key,
                pair => CSharpSyntaxTree.ParseText(pair.Value, path: pair.Key).GetCompilationUnitRoot(),
                StringComparer.Ordinal);
            return new RuntimeFailureSyntaxIndex(roots);
        }

        public MethodDeclarationSyntax GetMethod(string relativePath, string methodName)
        {
            if (!_roots.TryGetValue(relativePath, out var root))
                throw new InvalidOperationException($"Source root not found: {relativePath}");

            return root.DescendantNodes()
                .OfType<MethodDeclarationSyntax>()
                .FirstOrDefault(x => x.Identifier.ValueText == methodName)
                ?? throw new InvalidOperationException($"Method not found: {relativePath}:{methodName}");
        }
    }

    private static class RuntimeFailureSyntaxQueries
    {
        public static bool Invokes(SyntaxNode node, string methodName) =>
            node.DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Any(invocation => GetInvocationName(invocation) == methodName);

        public static bool HasCatchInvoking(SyntaxNode node, string methodName) =>
            node.DescendantNodes()
                .OfType<CatchClauseSyntax>()
                .Any(catchClause => Invokes(catchClause, methodName));

        public static bool HasCatchFilteredByDescriptor(SyntaxNode node, string descriptorText) =>
            node.DescendantNodes()
                .OfType<CatchClauseSyntax>()
                .Any(catchClause => catchClause.Filter?.FilterExpression.ToString().Contains(descriptorText, StringComparison.Ordinal) == true);

        public static bool StepRequestPublishBeforeCompensableRecord(MethodDeclarationSyntax method)
        {
            var methodText = method.ToString();
            var publishIndex = methodText.IndexOf("ctx.PublishAsync(request, TopologyAudience.Self, ct)", StringComparison.Ordinal);
            var successIndex = methodText.IndexOf("requestPublishSucceeded = true", StringComparison.Ordinal);
            var recordIndex = methodText.IndexOf("RecordCompensableStepDispatchAsync(step, idempotency, ct)", StringComparison.Ordinal);

            return publishIndex >= 0 &&
                   successIndex > publishIndex &&
                   recordIndex > successIndex;
        }

        public static bool CreatesFailedWorkflowCompletedEventUsing(SyntaxNode node, string failureMessageCall)
        {
            return node.DescendantNodes()
                .OfType<ObjectCreationExpressionSyntax>()
                .Where(creation => TypeName(creation.Type) == "WorkflowCompletedEvent")
                .Any(creation =>
                    ObjectInitializerAssigns(creation, "Success", "false") &&
                    creation.ToString().Contains(failureMessageCall, StringComparison.Ordinal));
        }

        public static bool CatchCreatesFailedStepCompletedEventUsing(SyntaxNode node, string failureMessageCall)
        {
            return node.DescendantNodes()
                .OfType<CatchClauseSyntax>()
                .SelectMany(catchClause => catchClause.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
                .Where(creation => TypeName(creation.Type) == "StepCompletedEvent")
                .Any(creation =>
                    ObjectInitializerAssigns(creation, "Success", "false") &&
                    creation.ToString().Contains(failureMessageCall, StringComparison.Ordinal));
        }

        public static bool HasArgumentNameWithLiteral(SyntaxNode node, string argumentName, string literalText)
        {
            return node.DescendantNodes()
                .OfType<ArgumentSyntax>()
                .Any(argument =>
                    argument.NameColon?.Name.Identifier.ValueText == argumentName &&
                    argument.Expression.ToString() == literalText);
        }

        public static bool NodeTextContainsAll(SyntaxNode node, params string[] fragments) =>
            fragments.All(fragment => node.ToString().Contains(fragment, StringComparison.Ordinal));

        private static bool ObjectInitializerAssigns(
            ObjectCreationExpressionSyntax creation,
            string memberName,
            string valueText)
        {
            return creation.Initializer?.Expressions
                .OfType<AssignmentExpressionSyntax>()
                .Any(assignment =>
                    assignment.Left.ToString() == memberName &&
                    assignment.Right.ToString() == valueText) == true;
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

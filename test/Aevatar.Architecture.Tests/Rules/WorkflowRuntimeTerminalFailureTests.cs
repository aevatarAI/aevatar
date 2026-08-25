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
                    await RecordCompensableStepDispatchAsync(step, idempotency, state, ct);
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
                "durable definition start bypasses terminalization helper",
                sources => sources.ReplaceInMethod(
                    RunAgentFile,
                    "DrainDynamicDefinitionStartAsync",
                    "await PublishStartWorkflowOrTerminalFailureAsync(",
                    "await PublishAsync("),
                "Workflow dynamic definition start must top-level await PublishStartWorkflowOrTerminalFailureAsync"
            },
            {
                "durable definition entry stops draining its start continuation",
                sources => sources.ReplaceInMethod(
                    RunAgentFile,
                    "HandleReplaceWorkflowDefinitionAndExecute",
                    "ReplaceWorkflowDefinitionBypassingBindingAsync",
                    "ReplaceWorkflowDefinitionWithoutStartingAsync"),
                "Workflow direct execution entry must top-level await ReplaceWorkflowDefinitionBypassingBindingAsync"
            },
            {
                "durable definition entry hides replacement behind a conditional",
                sources => sources.NestTopLevelStatementContainingInvocation(
                    RunAgentFile,
                    "HandleReplaceWorkflowDefinitionAndExecute",
                    "ReplaceWorkflowDefinitionBypassingBindingAsync"),
                "Workflow direct execution entry must top-level await ReplaceWorkflowDefinitionBypassingBindingAsync"
            },
            {
                "durable definition continuation disables execution after bind",
                sources => sources.ReplaceInMethod(
                    RunAgentFile,
                    "ReplaceWorkflowDefinitionBypassingBindingAsync",
                    "executeAfterBind: true",
                    "executeAfterBind: false"),
                "Workflow dynamic definition replacement must build an explicit execute-after-bind continuation"
            },
            {
                "durable definition drains before continuation registration",
                sources => sources.SwapTopLevelStatementsContainingInvocations(
                    RunAgentFile,
                    "ReplaceWorkflowDefinitionBypassingBindingAsync",
                    "PersistDomainEventsAsync",
                    "DrainPendingDefinitionBindingContinuationAsync"),
                "Workflow dynamic definition replacement must persist its continuation before draining it"
            },
            {
                "durable definition continuation condition is inverted",
                sources => sources.ReplaceInMethod(
                    RunAgentFile,
                    "DrainPendingDefinitionBindingContinuationAsync",
                    "if (continuation.ExecuteAfterBind)",
                    "if (!continuation.ExecuteAfterBind)"),
                "Workflow definition binding continuation must gate dynamic start on continuation.ExecuteAfterBind"
            },
            {
                "durable definition start keeps a dummy helper call beside direct publish",
                sources =>
                {
                    sources.ReplaceInMethod(
                        RunAgentFile,
                        "DrainDynamicDefinitionStartAsync",
                        "await PublishStartWorkflowOrTerminalFailureAsync(",
                        "await PublishAsync(");
                    sources.ReplaceInMethod(
                        RunAgentFile,
                        "DrainDynamicDefinitionStartAsync",
                        "await EnsureAgentTreeAsync();",
                        """
                        await PublishStartWorkflowOrTerminalFailureAsync(
                                    new StartWorkflowEvent
                                    {
                                        WorkflowName = _compiledWorkflow.Name,
                                        Input = continuation.ExecutionInput,
                                        RunId = State.RunId,
                                        BindingGeneration = State.BindingGeneration,
                                    },
                                    sessionId: string.Empty,
                                    ct);
                                await EnsureAgentTreeAsync();
                        """);
                },
                "Workflow dynamic definition start must not invoke PublishAsync directly"
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
        if (!RuntimeFailureSyntaxQueries.HasSingleTopLevelAwaitedInvocation(
                executeWorkflow,
                "ReplaceWorkflowDefinitionBypassingBindingAsync"))
        {
            violations.Add(
                "Workflow direct execution entry must top-level await " +
                "ReplaceWorkflowDefinitionBypassingBindingAsync.");
        }

        var replaceDefinition = index.GetMethod(RunAgentFile, "ReplaceWorkflowDefinitionBypassingBindingAsync");
        if (!RuntimeFailureSyntaxQueries.HasSingleTopLevelInvocationWithNamedBooleanArgument(
                replaceDefinition,
                "BuildDefinitionBindingContinuation",
                "executeAfterBind",
                expectedValue: true))
        {
            violations.Add(
                "Workflow dynamic definition replacement must build an explicit execute-after-bind continuation.");
        }

        if (!RuntimeFailureSyntaxQueries.PersistsContinuationBeforeTopLevelDrain(replaceDefinition))
        {
            violations.Add(
                "Workflow dynamic definition replacement must persist its continuation before draining it.");
        }

        var drainDefinition = index.GetMethod(RunAgentFile, "DrainPendingDefinitionBindingContinuationAsync");
        if (!RuntimeFailureSyntaxQueries.HasSingleTopLevelConditionalAwaitedInvocation(
                drainDefinition,
                receiverName: "continuation",
                memberName: "ExecuteAfterBind",
                invocationName: "DrainDynamicDefinitionStartAsync"))
        {
            violations.Add(
                "Workflow definition binding continuation must gate dynamic start on " +
                "continuation.ExecuteAfterBind.");
        }

        var drainStart = index.GetMethod(RunAgentFile, "DrainDynamicDefinitionStartAsync");
        if (!RuntimeFailureSyntaxQueries.HasSingleTopLevelAwaitedInvocation(
                drainStart,
                "PublishStartWorkflowOrTerminalFailureAsync"))
        {
            violations.Add(
                "Workflow dynamic definition start must top-level await " +
                "PublishStartWorkflowOrTerminalFailureAsync.");
        }

        if (RuntimeFailureSyntaxQueries.Invokes(drainStart, "PublishAsync"))
        {
            violations.Add(
                "Workflow dynamic definition start must not invoke PublishAsync directly.");
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

        public void NestTopLevelStatementContainingInvocation(
            string relativePath,
            string methodName,
            string invocationName)
        {
            var (root, method) = ParseMethod(relativePath, methodName);
            var body = method.Body
                ?? throw new InvalidOperationException($"Fixture method has no body: {relativePath}:{methodName}");
            var statement = body.Statements.SingleOrDefault(candidate =>
                    ContainsInvocation(candidate, invocationName))
                ?? throw new InvalidOperationException(
                    $"Fixture top-level invocation not found in {relativePath}:{methodName}: {invocationName}");
            var nestedStatement = statement.WithoutLeadingTrivia().WithoutTrailingTrivia();
            var replacement = SyntaxFactory.IfStatement(
                    SyntaxFactory.LiteralExpression(SyntaxKind.TrueLiteralExpression),
                    SyntaxFactory.Block(nestedStatement))
                .WithLeadingTrivia(statement.GetLeadingTrivia())
                .WithTrailingTrivia(statement.GetTrailingTrivia());

            _sources[relativePath] = root.ReplaceNode(statement, replacement).ToFullString();
        }

        public void SwapTopLevelStatementsContainingInvocations(
            string relativePath,
            string methodName,
            string firstInvocationName,
            string secondInvocationName)
        {
            var (root, method) = ParseMethod(relativePath, methodName);
            var body = method.Body
                ?? throw new InvalidOperationException($"Fixture method has no body: {relativePath}:{methodName}");
            var statements = body.Statements.ToList();
            var firstIndex = statements.FindIndex(candidate => ContainsInvocation(candidate, firstInvocationName));
            var secondIndex = statements.FindIndex(candidate => ContainsInvocation(candidate, secondInvocationName));
            if (firstIndex < 0 || secondIndex < 0)
            {
                throw new InvalidOperationException(
                    $"Fixture top-level invocations not found in {relativePath}:{methodName}: " +
                    $"{firstInvocationName}, {secondInvocationName}");
            }

            (statements[firstIndex], statements[secondIndex]) = (statements[secondIndex], statements[firstIndex]);
            var replacement = method.WithBody(body.WithStatements(SyntaxFactory.List(statements)));
            _sources[relativePath] = root.ReplaceNode(method, replacement).ToFullString();
        }

        private (CompilationUnitSyntax Root, MethodDeclarationSyntax Method) ParseMethod(
            string relativePath,
            string methodName)
        {
            if (!_sources.TryGetValue(relativePath, out var source))
                throw new InvalidOperationException($"Missing source fixture: {relativePath}");

            var root = CSharpSyntaxTree.ParseText(source, path: relativePath).GetCompilationUnitRoot();
            var method = root.DescendantNodes()
                .OfType<MethodDeclarationSyntax>()
                .FirstOrDefault(x => x.Identifier.ValueText == methodName)
                ?? throw new InvalidOperationException($"Fixture method not found in {relativePath}: {methodName}");
            return (root, method);
        }

        private static bool ContainsInvocation(SyntaxNode node, string invocationName) =>
            node.DescendantNodesAndSelf()
                .OfType<InvocationExpressionSyntax>()
                .Any(invocation => InvocationName(invocation) == invocationName);

        private static string? InvocationName(InvocationExpressionSyntax invocation) =>
            invocation.Expression switch
            {
                IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
                MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
                _ => null,
            };

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

        public static bool HasSingleTopLevelAwaitedInvocation(
            MethodDeclarationSyntax method,
            string invocationName)
        {
            var allInvocations = FindInvocations(method, invocationName);
            var topLevelInvocations = FindTopLevelAwaitedInvocations(method, invocationName);
            return allInvocations.Count == 1 && topLevelInvocations.Count == 1;
        }

        public static bool HasSingleTopLevelInvocationWithNamedBooleanArgument(
            MethodDeclarationSyntax method,
            string invocationName,
            string argumentName,
            bool expectedValue)
        {
            var allInvocations = FindInvocations(method, invocationName);
            var topLevelInvocations = FindTopLevelDirectInvocations(method, invocationName);
            if (allInvocations.Count != 1 || topLevelInvocations.Count != 1)
                return false;

            var expectedKind = expectedValue
                ? SyntaxKind.TrueLiteralExpression
                : SyntaxKind.FalseLiteralExpression;
            return topLevelInvocations[0].Invocation.ArgumentList.Arguments.Any(argument =>
                argument.NameColon?.Name.Identifier.ValueText == argumentName &&
                argument.Expression.IsKind(expectedKind));
        }

        public static bool PersistsContinuationBeforeTopLevelDrain(MethodDeclarationSyntax method)
        {
            var buildInvocations = FindTopLevelDirectInvocations(
                method,
                "BuildDefinitionBindingContinuation");
            var persistInvocations = FindTopLevelAwaitedInvocations(method, "PersistDomainEventsAsync");
            var drainInvocations = FindTopLevelAwaitedInvocations(
                method,
                "DrainPendingDefinitionBindingContinuationAsync");
            if (buildInvocations.Count != 1 ||
                persistInvocations.Count != 1 ||
                drainInvocations.Count != 1 ||
                FindInvocations(method, "DrainPendingDefinitionBindingContinuationAsync").Count != 1)
            {
                return false;
            }

            var persist = persistInvocations[0];
            var registersContinuation = persist.Invocation.DescendantNodes()
                .OfType<ObjectCreationExpressionSyntax>()
                .Where(creation => TypeName(creation.Type) ==
                                   "WorkflowRunDefinitionBindingContinuationRegisteredEvent")
                .Any(creation => ObjectInitializerAssigns(creation, "Continuation", "continuation"));
            return registersContinuation &&
                   buildInvocations[0].StatementIndex < persist.StatementIndex &&
                   persist.StatementIndex < drainInvocations[0].StatementIndex;
        }

        public static bool HasSingleTopLevelConditionalAwaitedInvocation(
            MethodDeclarationSyntax method,
            string receiverName,
            string memberName,
            string invocationName)
        {
            if (FindInvocations(method, invocationName).Count != 1 || method.Body == null)
                return false;

            var matchingConditions = method.Body.Statements
                .OfType<IfStatementSyntax>()
                .Where(statement => IsExactMemberAccess(statement.Condition, receiverName, memberName))
                .ToList();
            return matchingConditions.Count == 1 &&
                   EmbeddedStatementDirectlyAwaitsInvocation(
                       matchingConditions[0].Statement,
                       invocationName);
        }

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
            var publishIndex = FirstSpanStart(
                method.DescendantNodes()
                    .OfType<InvocationExpressionSyntax>()
                    .Where(invocation =>
                        GetInvocationName(invocation) == "PublishAsync" &&
                        invocation.ArgumentList.Arguments.Count > 0 &&
                        invocation.ArgumentList.Arguments[0].Expression.ToString() == "request"));
            var successIndex = FirstSpanStart(
                method.DescendantNodes()
                    .OfType<AssignmentExpressionSyntax>()
                    .Where(assignment =>
                        assignment.Left.ToString() == "requestPublishSucceeded" &&
                        assignment.Right.IsKind(SyntaxKind.TrueLiteralExpression)));
            var recordIndex = FirstSpanStart(
                method.DescendantNodes()
                    .OfType<InvocationExpressionSyntax>()
                    .Where(invocation => GetInvocationName(invocation) == "RecordCompensableStepDispatchAsync"));

            return publishIndex >= 0 &&
                   successIndex > publishIndex &&
                   recordIndex > successIndex;
        }

        private static int FirstSpanStart(IEnumerable<SyntaxNode> nodes) =>
            nodes.Select(node => node.SpanStart).DefaultIfEmpty(-1).Min();

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

        private static IReadOnlyList<InvocationExpressionSyntax> FindInvocations(
            SyntaxNode node,
            string invocationName) =>
            node.DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Where(invocation => GetInvocationName(invocation) == invocationName)
                .ToList();

        private static IReadOnlyList<TopLevelInvocation> FindTopLevelAwaitedInvocations(
            MethodDeclarationSyntax method,
            string invocationName) =>
            FindTopLevelInvocations(method, awaited: true)
                .Where(candidate => GetInvocationName(candidate.Invocation) == invocationName)
                .ToList();

        private static IReadOnlyList<TopLevelInvocation> FindTopLevelDirectInvocations(
            MethodDeclarationSyntax method,
            string invocationName) =>
            FindTopLevelInvocations(method, awaited: false)
                .Where(candidate => GetInvocationName(candidate.Invocation) == invocationName)
                .ToList();

        private static IReadOnlyList<TopLevelInvocation> FindTopLevelInvocations(
            MethodDeclarationSyntax method,
            bool awaited)
        {
            var invocations = new List<TopLevelInvocation>();
            if (method.Body == null)
                return invocations;

            for (var index = 0; index < method.Body.Statements.Count; index++)
            {
                foreach (var expression in DirectStatementExpressions(method.Body.Statements[index]))
                {
                    var candidate = awaited
                        ? (expression as AwaitExpressionSyntax)?.Expression as InvocationExpressionSyntax
                        : expression as InvocationExpressionSyntax;
                    if (candidate != null)
                        invocations.Add(new TopLevelInvocation(index, candidate));
                }
            }

            return invocations;
        }

        private static IEnumerable<ExpressionSyntax> DirectStatementExpressions(StatementSyntax statement)
        {
            if (statement is ExpressionStatementSyntax expressionStatement)
            {
                yield return expressionStatement.Expression;
                yield break;
            }

            if (statement is not LocalDeclarationStatementSyntax localDeclaration)
                yield break;

            foreach (var variable in localDeclaration.Declaration.Variables)
            {
                if (variable.Initializer?.Value is { } value)
                    yield return value;
            }
        }

        private static bool EmbeddedStatementDirectlyAwaitsInvocation(
            StatementSyntax statement,
            string invocationName)
        {
            var directStatement = statement is BlockSyntax { Statements.Count: 1 } block
                ? block.Statements[0]
                : statement;
            return DirectStatementExpressions(directStatement)
                .Select(static expression => expression switch
                {
                    AwaitExpressionSyntax awaitExpression => awaitExpression.Expression,
                    AssignmentExpressionSyntax
                    {
                        Right: AwaitExpressionSyntax awaitExpression,
                    } => awaitExpression.Expression,
                    _ => null,
                })
                .OfType<InvocationExpressionSyntax>()
                .Any(invocation => GetInvocationName(invocation) == invocationName);
        }

        private static bool IsExactMemberAccess(
            ExpressionSyntax expression,
            string receiverName,
            string memberName)
        {
            while (expression is ParenthesizedExpressionSyntax parenthesized)
                expression = parenthesized.Expression;

            return expression is MemberAccessExpressionSyntax
                   {
                       Expression: IdentifierNameSyntax receiver,
                       Name: IdentifierNameSyntax member,
                   } &&
                   receiver.Identifier.ValueText == receiverName &&
                   member.Identifier.ValueText == memberName;
        }

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

        private sealed record TopLevelInvocation(
            int StatementIndex,
            InvocationExpressionSyntax Invocation);
    }
}

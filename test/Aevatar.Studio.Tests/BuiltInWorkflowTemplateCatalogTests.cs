using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Studio.Application.Studio.Services;
using Aevatar.Studio.Domain.Studio.Compatibility;
using Aevatar.Studio.Domain.Studio.Services;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Infrastructure.Serialization;
using Aevatar.Studio.Infrastructure.WorkflowTemplates;
using Aevatar.Workflow.Abstractions.Execution;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Core.Modules;
using Aevatar.Workflow.Core.Primitives;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Studio.Tests;

public sealed class BuiltInWorkflowTemplateCatalogTests
{
    [Fact]
    public async Task ListAsync_ShouldReturnTheThreeCuratedTemplatesInStableProductOrder()
    {
        var catalog = BuiltInWorkflowTemplateTestSupport.CreateCatalog();

        var page = await catalog.ListAsync(new WorkflowTemplateCatalogQuery(PageSize: 20));

        page.Items.Select(item => (item.TemplateId, item.Revision)).Should().Equal(
            ("simple-assistant", "1"),
            ("conditional-routing", "1"),
            ("review-and-approve", "1"));
        page.Items.Should().OnlyContain(item =>
            !string.IsNullOrWhiteSpace(item.Title.EnUS) &&
            !string.IsNullOrWhiteSpace(item.Title.ZhCN) &&
            !string.IsNullOrWhiteSpace(item.Summary.EnUS) &&
            !string.IsNullOrWhiteSpace(item.Summary.ZhCN) &&
            !string.IsNullOrWhiteSpace(item.Description.EnUS) &&
            !string.IsNullOrWhiteSpace(item.Description.ZhCN) &&
            !string.IsNullOrWhiteSpace(item.ExpectedIO.Input.EnUS) &&
            !string.IsNullOrWhiteSpace(item.ExpectedIO.Input.ZhCN) &&
            !string.IsNullOrWhiteSpace(item.ExpectedIO.Output.EnUS) &&
            !string.IsNullOrWhiteSpace(item.ExpectedIO.Output.ZhCN));
        page.Items.Should().OnlyContain(item => item.Tags.Count > 0);
    }

    [Fact]
    public async Task Details_ShouldUseEmbeddedEnvironmentNeutralYamlAndTruthfulRequirements()
    {
        var catalog = BuiltInWorkflowTemplateTestSupport.CreateCatalog();

        var details = await Task.WhenAll(
            new[] { "simple-assistant", "conditional-routing", "review-and-approve" }
                .Select(async templateId =>
                    (await catalog.GetAsync(templateId, "1")).Detail!));

        details.Should().OnlyContain(detail => !string.IsNullOrWhiteSpace(detail.WorkflowYaml));
        details.Should().OnlyContain(detail =>
            !detail.WorkflowYaml.Contains("credential", StringComparison.OrdinalIgnoreCase) &&
            !detail.WorkflowYaml.Contains("token", StringComparison.OrdinalIgnoreCase) &&
            !detail.WorkflowYaml.Contains("api_key", StringComparison.OrdinalIgnoreCase) &&
            !detail.WorkflowYaml.Contains("service_id", StringComparison.OrdinalIgnoreCase) &&
            !detail.WorkflowYaml.Contains("actor_id", StringComparison.OrdinalIgnoreCase) &&
            !detail.WorkflowYaml.Contains("scope_id", StringComparison.OrdinalIgnoreCase) &&
            !detail.WorkflowYaml.Contains("member_id", StringComparison.OrdinalIgnoreCase) &&
            !detail.WorkflowYaml.Contains("workflow_id", StringComparison.OrdinalIgnoreCase) &&
            !detail.WorkflowYaml.Contains("published_service_id", StringComparison.OrdinalIgnoreCase) &&
            !detail.WorkflowYaml.Contains("approval_definition_id", StringComparison.OrdinalIgnoreCase) &&
            !detail.WorkflowYaml.Contains("http://", StringComparison.OrdinalIgnoreCase) &&
            !detail.WorkflowYaml.Contains("https://", StringComparison.OrdinalIgnoreCase));

        details.Single(detail => detail.TemplateId == "simple-assistant")
            .Requirements.RequiresDefaultLLMRoute.Should().BeTrue();
        var approval = details.Single(detail => detail.TemplateId == "review-and-approve");
        approval.Requirements.RequiresDefaultLLMRoute.Should().BeTrue();
        approval.Requirements.RequiresHumanInteraction.Should().BeTrue();
        approval.Requirements.RequiredPrimitives.Should().Contain("human_approval");
    }

    [Fact]
    public async Task SimpleAssistant_DraftRun_ShouldReturnTheAssistantResponse()
    {
        var run = await BuiltInWorkflowTemplateTestSupport.RunDraftAsync(
            "simple-assistant",
            "Explain the current workflow.");

        run.VisitedStepIds.Should().Equal("answer_request");
        run.Output.Should().Be("Assistant response");
    }

    [Theory]
    [InlineData(" urgent request ", "urgent_result", "Urgent route selected.")]
    [InlineData("routine request", "standard_result", "Standard route selected.")]
    public async Task ConditionalRouting_DraftRun_ShouldSelectTheExpectedBranch(
        string input,
        string branchStepId,
        string expectedOutput)
    {
        var run = await BuiltInWorkflowTemplateTestSupport.RunDraftAsync(
            "conditional-routing",
            input);

        run.VisitedStepIds.Should().Equal(
            "prepare_input",
            "select_route",
            branchStepId,
            "return_result");
        run.Output.Should().Be(expectedOutput);
    }

    [Theory]
    [InlineData(true, "mark_approved", "Summary: approved")]
    [InlineData(false, "mark_rejected", "Summary: rejected")]
    public async Task ReviewAndApprove_DraftRun_ShouldBranchAndSummarizeTheDecision(
        bool approved,
        string branchStepId,
        string expectedOutput)
    {
        var run = await BuiltInWorkflowTemplateTestSupport.RunDraftAsync(
            "review-and-approve",
            "Prepare a release note.",
            approved);

        run.VisitedStepIds.Should().Equal(
            "generate_draft",
            "review_draft",
            branchStepId,
            "summarize_result");
        run.Output.Should().Be(expectedOutput);
    }
}

internal static class BuiltInWorkflowTemplateTestSupport
{
    public static EmbeddedWorkflowTemplateCatalogQueryPort CreateCatalog()
    {
        var profile = WorkflowCompatibilityProfile.AevatarV1;
        return new EmbeddedWorkflowTemplateCatalogQueryPort(
            new CanonicalWorkflowDefinitionParser(),
            new YamlWorkflowDocumentService(profile),
            new WorkflowValidator(profile),
            new WorkflowGraphMapper(profile),
            BuiltInWorkflowTemplateRegistry.CreateRegistrations());
    }

    public static async Task<DraftRunResult> RunDraftAsync(
        string templateId,
        string input,
        bool approved = true)
    {
        var lookup = await CreateCatalog().GetAsync(templateId, "1");
        lookup.Status.Should().Be(WorkflowTemplateLookupStatus.Found);
        var workflow = new WorkflowParser().Parse(lookup.Detail!.WorkflowYaml);
        StepDefinition? current = workflow.Steps[0];
        var currentInput = input;
        var visited = new List<string>();

        while (current != null)
        {
            visited.Add(current.Id);
            var completed = await ExecuteStepAsync(current, currentInput, approved);
            completed.Success.Should().BeTrue(completed.Error);
            currentInput = completed.Output;
            current = !string.IsNullOrWhiteSpace(completed.NextStepId)
                ? workflow.GetStep(completed.NextStepId)
                : workflow.GetNextStep(current.Id, completed.BranchKey);
        }

        return new DraftRunResult(currentInput, visited);
    }

    private static async Task<StepCompletedEvent> ExecuteStepAsync(
        StepDefinition step,
        string input,
        bool approved)
    {
        if (step.Type == "llm_call")
        {
            var output = step.Id switch
            {
                "answer_request" => "Assistant response",
                "generate_draft" => "Prepared draft",
                "summarize_result" => $"Summary: {input}",
                _ => input,
            };
            return Completed(step, output);
        }

        var request = new StepRequestEvent
        {
            StepId = step.Id,
            StepType = step.Type,
            RunId = "draft-run-1",
            Input = input,
        };
        request.Parameters.Add(step.Parameters);
        if (step.Branches != null)
        {
            foreach (var (key, target) in step.Branches)
                request.Parameters[$"branch.{key}"] = target;
        }

        var context = new RecordingWorkflowContext();
        var envelope = Wrap(request);
        switch (step.Type)
        {
            case "transform":
                await new TransformModule().HandleAsync(envelope, context, CancellationToken.None);
                break;
            case "switch":
                await new SwitchModule().HandleAsync(envelope, context, CancellationToken.None);
                break;
            case "assign":
                await new AssignModule().HandleAsync(envelope, context, CancellationToken.None);
                break;
            case "human_approval":
                var module = new HumanApprovalModule();
                await module.HandleAsync(envelope, context, CancellationToken.None);
                await module.HandleAsync(
                    Wrap(new WorkflowResumedEvent
                    {
                        RunId = request.RunId,
                        StepId = request.StepId,
                        Approved = approved,
                    }),
                    context,
                    CancellationToken.None);
                break;
            default:
                throw new InvalidOperationException($"Draft-run probe does not support step type '{step.Type}'.");
        }

        return context.Published.OfType<StepCompletedEvent>().Should().ContainSingle().Subject;
    }

    private static StepCompletedEvent Completed(StepDefinition step, string output) =>
        new()
        {
            StepId = step.Id,
            RunId = "draft-run-1",
            Success = true,
            Output = output,
        };

    private static EventEnvelope Wrap(IMessage payload) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(payload),
        };

    internal sealed record DraftRunResult(string Output, IReadOnlyList<string> VisitedStepIds);

    private sealed class CanonicalWorkflowDefinitionParser : IWorkflowDefinitionParser
    {
        private static readonly ISet<string> KnownStepTypes =
            WorkflowPrimitiveCatalog.BuildCanonicalStepTypeSet(
                new WorkflowCoreModulePack().Modules.SelectMany(static module => module.Names));

        public Task<WorkflowYamlParseResult> ParseWorkflowYamlAsync(
            string workflowYaml,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var workflow = new WorkflowParser().Parse(workflowYaml);
                var errors = Aevatar.Workflow.Core.Validation.WorkflowValidator.Validate(
                    workflow,
                    new Aevatar.Workflow.Core.Validation.WorkflowValidator.WorkflowValidationOptions
                    {
                        RequireKnownStepTypes = true,
                        KnownStepTypes = KnownStepTypes,
                    },
                    availableWorkflowNames: null);
                return Task.FromResult(errors.Count == 0
                    ? WorkflowYamlParseResult.Success(workflow.Name)
                    : WorkflowYamlParseResult.Invalid(string.Join("; ", errors)));
            }
            catch (Exception exception)
            {
                return Task.FromResult(WorkflowYamlParseResult.Invalid(exception.Message));
            }
        }
    }

    private sealed class RecordingWorkflowContext : IWorkflowExecutionContext
    {
        private readonly Dictionary<string, Any> _states = new(StringComparer.Ordinal);

        public EventEnvelope InboundEnvelope { get; } = new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
        };

        public string AgentId => "draft-run-agent";
        public string RunId => "draft-run-1";
        public IServiceProvider Services { get; } = new EmptyServiceProvider();
        public ILogger Logger { get; } = NullLogger.Instance;
        public List<IMessage> Published { get; } = [];

        public TState LoadState<TState>(string scopeKey)
            where TState : class, IMessage<TState>, new() =>
            _states.TryGetValue(scopeKey, out var state) && state.Is(new TState().Descriptor)
                ? state.Unpack<TState>()
                : new TState();

        public IReadOnlyList<KeyValuePair<string, TState>> LoadStates<TState>(string scopeKeyPrefix = "")
            where TState : class, IMessage<TState>, new() =>
            _states
                .Where(item => item.Key.StartsWith(scopeKeyPrefix, StringComparison.Ordinal) &&
                               item.Value.Is(new TState().Descriptor))
                .Select(item => new KeyValuePair<string, TState>(item.Key, item.Value.Unpack<TState>()))
                .ToArray();

        public Task SaveStateAsync<TState>(
            string scopeKey,
            TState state,
            CancellationToken ct = default)
            where TState : class, IMessage<TState>
        {
            ct.ThrowIfCancellationRequested();
            _states[scopeKey] = Any.Pack(state);
            return Task.CompletedTask;
        }

        public Task ClearStateAsync(string scopeKey, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _states.Remove(scopeKey);
            return Task.CompletedTask;
        }

        public Task<RuntimeCallbackLease> ScheduleSelfDurableTimeoutAsync(
            string callbackId,
            TimeSpan dueTime,
            IMessage evt,
            EventEnvelopePublishOptions? options = null,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new RuntimeCallbackLease(
                AgentId,
                callbackId,
                1,
                RuntimeCallbackBackend.InMemory));
        }

        public Task CancelDurableCallbackAsync(
            RuntimeCallbackLease lease,
            CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task PublishAsync<TEvent>(
            TEvent evt,
            TopologyAudience audience = TopologyAudience.Children,
            CancellationToken ct = default,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            ct.ThrowIfCancellationRequested();
            Published.Add(evt);
            return Task.CompletedTask;
        }
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(System.Type serviceType) => null;
    }
}

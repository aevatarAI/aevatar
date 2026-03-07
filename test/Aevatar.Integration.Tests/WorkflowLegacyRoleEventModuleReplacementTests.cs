using Aevatar.AI.Abstractions.Agents;
using Aevatar.AI.Core.Agents;
using Aevatar.Configuration;
using Aevatar.Demos.Workflow.Web;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Runtime.Implementations.Local.DependencyInjection;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Integration.Tests;

[Trait("Category", "Integration")]
[Trait("Feature", "WorkflowLegacyRoleEventModuleReplacement")]
public sealed class WorkflowLegacyRoleEventModuleReplacementTests
{
    private static readonly ReplacementScenario[] ScenarioData =
    [
        new("20_role_event_module_template", "20_explicit_template_route", "payment_api_timeout",
            "Template route handled: payment_api_timeout",
            ["render_template"]),
        new("21_role_event_module_csv_markdown", "21_explicit_csv_markdown_route",
            "service,error_rate,latency_ms\ngateway,1.2,210\ncheckout,0.3,120",
            Markdown(
                "| service | error_rate | latency_ms |",
                "| --- | --- | --- |",
                "| gateway | 1.2 | 210 |",
                "| checkout | 0.3 | 120 |"),
            ["render_csv_markdown"]),
        new("22_role_event_module_json_pick", "22_explicit_json_pick_route",
            """{"incident":{"id":"INC-2026-002","owner":{"team":"payments-sre","user":"bob"}},"severity":"critical"}""",
            "payments-sre",
            ["extract_owner_team"]),
        new("23_role_event_module_multiplex_template", "23_explicit_multiplex_template_route",
            "template branch selected",
            "Template multiplex route: template branch selected",
            ["choose_route", "render_template", "finish"],
            ExpectedSwitchBranch: "template"),
        new("24_role_event_module_multiplex_csv", "24_explicit_multiplex_csv_route",
            "service,error_rate,latency_ms\ngateway,1.2,210\ncheckout,0.3,120",
            Markdown(
                "| service | error_rate | latency_ms |",
                "| --- | --- | --- |",
                "| gateway | 1.2 | 210 |",
                "| checkout | 0.3 | 120 |"),
            ["choose_route", "render_csv_markdown", "finish"],
            ExpectedSwitchBranch: "service,error_rate,latency_ms"),
        new("25_role_event_module_multiplex_json", "25_explicit_multiplex_json_route",
            """{"incident":{"owner":{"team":"platform"}}}""",
            "platform",
            ["choose_route", "extract_owner_team", "finish"],
            ExpectedSwitchBranch: "{\"incident\""),
        new("26_role_event_module_multi_role_chain", "26_explicit_multi_stage_template_csv_json_chain",
            "prepare routing escalation",
            "routing-platform",
            ["prepare_brief", "seed_csv", "render_csv_markdown", "seed_json", "extract_owner_team"]),
        new("27_role_event_module_extensions_template", "27_explicit_extensions_template_route",
            "extensions-template",
            "Extensions template route: extensions-template",
            ["render_template"]),
        new("28_role_event_module_extensions_csv", "28_explicit_extensions_csv_route",
            "service,error_rate,latency_ms\nsearch,0.9,190\nfeed,1.5,310",
            Markdown(
                "| service | error_rate | latency_ms |",
                "| --- | --- | --- |",
                "| search | 0.9 | 190 |",
                "| feed | 1.5 | 310 |"),
            ["render_csv_markdown"]),
        new("29_role_event_module_top_level_overrides_extensions", "29_explicit_precedence_json_pick",
            """{"incident":{"owner":{"team":"top-level-wins"}}}""",
            "top-level-wins",
            ["extract_owner_team"]),
        new("30_role_event_module_extensions_multi_role_chain", "30_explicit_extensions_multi_stage_chain",
            "extensions chain kickoff",
            "extensions-chain",
            ["prepare_brief", "seed_csv", "render_csv_markdown", "seed_json", "extract_owner_team"]),
        new("31_role_event_module_extensions_multiplex_json", "31_explicit_extensions_multiplex_json_route",
            """{"incident":{"owner":{"team":"extensions-multiplex"}}}""",
            "extensions-multiplex",
            ["choose_route", "extract_owner_team", "finish"],
            ExpectedSwitchBranch: "{\"incident\""),
        new("32_role_event_module_top_level_overrides_extensions_multiplex", "32_explicit_precedence_multiplex_csv",
            "service,error_rate,latency_ms\ngateway,1.9,250\ncheckout,0.4,140",
            Markdown(
                "| service | error_rate | latency_ms |",
                "| --- | --- | --- |",
                "| gateway | 1.9 | 250 |",
                "| checkout | 0.4 | 140 |"),
            ["choose_route", "render_csv_markdown", "finish"],
            ExpectedSwitchBranch: "service,error_rate,latency_ms"),
        new("33_role_event_module_no_routes_template", "33_explicit_template_without_routes",
            "no-routes-template",
            "No-routes explicit template: no-routes-template",
            ["render_template"]),
        new("34_role_event_module_route_dsl_csv", "34_explicit_csv_route_dsl_equivalent",
            "service,error_rate,latency_ms\napi,2.2,260\nworker,0.5,110",
            Markdown(
                "| service | error_rate | latency_ms |",
                "| --- | --- | --- |",
                "| api | 2.2 | 260 |",
                "| worker | 0.5 | 110 |"),
            ["render_csv_markdown"]),
        new("35_role_event_module_unknown_ignored_template", "35_explicit_template_ignore_unknown_module",
            "ignore-missing-module",
            "Known template still runs: ignore-missing-module",
            ["render_template"]),
        new("36_mixed_step_json_pick_then_role_template", "36_explicit_json_pick_then_template",
            """{"incident":{"owner":{"team":"database-oncall"}}}""",
            "Escalate to database-oncall",
            ["extract_owner_team", "render_message"]),
        new("37_mixed_step_csv_markdown_then_role_template", "37_explicit_csv_markdown_then_template",
            "service,error_rate,latency_ms\ngateway,1.2,210\ncheckout,0.3,120",
            Markdown(
                "Rendered report:",
                "| service | error_rate | latency_ms |",
                "| --- | --- | --- |",
                "| gateway | 1.2 | 210 |",
                "| checkout | 0.3 | 120 |"),
            ["render_csv_markdown", "wrap_report"]),
        new("38_mixed_step_template_then_role_csv_markdown", "38_explicit_template_then_csv_markdown",
            "1.7",
            Markdown(
                "| service | error_rate | latency_ms |",
                "| --- | --- | --- |",
                "| gateway | 1.7 | 220 |",
                "| checkout | 0.9 | 170 |"),
            ["compose_csv", "render_csv_markdown"]),
    ];

    public static IEnumerable<object[]> ReplacementScenarios =>
        ScenarioData.Select(scenario => new object[] { scenario });

    [Fact]
    public void ReplacementCatalog_ShouldCoverEveryRetiredRoleEventModuleWorkflow()
    {
        var expectedRetiredWorkflows = new[]
        {
            "20_role_event_module_template",
            "21_role_event_module_csv_markdown",
            "22_role_event_module_json_pick",
            "23_role_event_module_multiplex_template",
            "24_role_event_module_multiplex_csv",
            "25_role_event_module_multiplex_json",
            "26_role_event_module_multi_role_chain",
            "27_role_event_module_extensions_template",
            "28_role_event_module_extensions_csv",
            "29_role_event_module_top_level_overrides_extensions",
            "30_role_event_module_extensions_multi_role_chain",
            "31_role_event_module_extensions_multiplex_json",
            "32_role_event_module_top_level_overrides_extensions_multiplex",
            "33_role_event_module_no_routes_template",
            "34_role_event_module_route_dsl_csv",
            "35_role_event_module_unknown_ignored_template",
            "36_mixed_step_json_pick_then_role_template",
            "37_mixed_step_csv_markdown_then_role_template",
            "38_mixed_step_template_then_role_csv_markdown",
        };

        ScenarioData.Select(x => x.OldWorkflowName).Should().BeEquivalentTo(expectedRetiredWorkflows);
        ScenarioData.Select(x => x.NewWorkflowName).Should().OnlyHaveUniqueItems();

        foreach (var scenario in ScenarioData)
            File.Exists(GetWorkflowPath(scenario.NewWorkflowName)).Should().BeTrue($"{scenario.NewWorkflowName} must exist");
    }

    [Fact]
    public void ReplacementWorkflows_ShouldNotUseRetiredEventModuleFields()
    {
        foreach (var scenario in ScenarioData)
        {
            var yaml = LoadWorkflowYaml(scenario.NewWorkflowName);
            yaml.Should().NotContain("event_modules");
            yaml.Should().NotContain("event_routes");
        }
    }

    [Theory]
    [MemberData(nameof(ReplacementScenarios))]
    public async Task ReplacementWorkflow_ShouldExecuteEquivalentBusinessLogic(ReplacementScenario scenario)
    {
        await using var env = BuildEnvironment();
        var result = await RunWorkflowAsync(env.Provider, env.Runtime, scenario.NewWorkflowName, scenario.Input);

        result.WorkflowCompleted.Should().NotBeNull();
        result.WorkflowCompleted!.Success.Should().BeTrue();
        result.WorkflowCompleted.Output.Should().Be(scenario.ExpectedOutput);
        result.StepCompletions.Should().OnlyContain(step => step.Success);
        result.StepCompletions.Select(step => step.StepId).Should().Equal(scenario.ExpectedStepIds);

        if (!string.IsNullOrWhiteSpace(scenario.ExpectedSwitchBranch))
        {
            var routeStep = result.StepCompletions.First(step => step.StepId == "choose_route");
            routeStep.Metadata.Should().ContainKey("branch");
            routeStep.Metadata["branch"].Should().Be(scenario.ExpectedSwitchBranch);
        }
    }

    private static TestEnvironment BuildEnvironment()
    {
        var services = new ServiceCollection();
        services.AddAevatarRuntime();
        services.AddAevatarWorkflow();
        services.AddSingleton<IWorkflowPrimitivePack, DemoWorkflowPrimitivePack>();
        services.AddSingleton<IRoleAgentTypeResolver, RoleGAgentTypeResolver>();

        var provider = services.BuildServiceProvider();
        var runtime = provider.GetRequiredService<IActorRuntime>();
        return new TestEnvironment(provider, runtime);
    }

    private static async Task<WorkflowRunResult> RunWorkflowAsync(
        ServiceProvider provider,
        IActorRuntime runtime,
        string workflowName,
        string input)
    {
        var actor = await runtime.CreateAsync<WorkflowRunGAgent>("wf-replacement-" + Guid.NewGuid().ToString("N")[..8]);
        await actor.HandleEventAsync(new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(new BindWorkflowDefinitionEvent
            {
                WorkflowYaml = LoadWorkflowYaml(workflowName),
                WorkflowName = workflowName,
            }),
            PublisherId = "test",
            Direction = EventDirection.Self,
            CorrelationId = Guid.NewGuid().ToString("N"),
        });

        var stream = provider.GetRequiredService<IStreamProvider>().GetStream(actor.Id);
        var stepCompletions = new List<StepCompletedEvent>();
        var workflowCompleted = new TaskCompletionSource<WorkflowCompletedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var subscription = await stream.SubscribeAsync<EventEnvelope>(envelope =>
        {
            var payload = envelope.Payload;
            if (payload == null)
                return Task.CompletedTask;

            if (payload.Is(StepCompletedEvent.Descriptor))
                stepCompletions.Add(payload.Unpack<StepCompletedEvent>());
            else if (payload.Is(WorkflowCompletedEvent.Descriptor))
                workflowCompleted.TrySetResult(payload.Unpack<WorkflowCompletedEvent>());

            return Task.CompletedTask;
        });

        await actor.HandleEventAsync(new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(new ChatRequestEvent
            {
                Prompt = input,
                SessionId = workflowName + "-session",
            }),
            PublisherId = "test",
            Direction = EventDirection.Self,
        });

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var completed = await workflowCompleted.Task.WaitAsync(timeout.Token);
        await runtime.DestroyAsync(actor.Id);
        return new WorkflowRunResult(completed, stepCompletions);
    }

    private static string LoadWorkflowYaml(string workflowName) => File.ReadAllText(GetWorkflowPath(workflowName));

    private static string GetWorkflowPath(string workflowName) =>
        Path.Combine(
            AevatarPaths.RepoRoot,
            "demos",
            "Aevatar.Demos.Workflow",
            "workflows",
            workflowName + ".yaml");

    private static string Markdown(params string[] lines) => string.Join("\n", lines);

    public sealed record ReplacementScenario(
        string OldWorkflowName,
        string NewWorkflowName,
        string Input,
        string ExpectedOutput,
        string[] ExpectedStepIds,
        string? ExpectedSwitchBranch = null);

    private sealed record WorkflowRunResult(
        WorkflowCompletedEvent? WorkflowCompleted,
        List<StepCompletedEvent> StepCompletions);

    private sealed class TestEnvironment(ServiceProvider provider, IActorRuntime runtime) : IAsyncDisposable
    {
        public ServiceProvider Provider { get; } = provider;
        public IActorRuntime Runtime { get; } = runtime;

        public ValueTask DisposeAsync() => Provider.DisposeAsync();
    }
}

using Aevatar.AI.ToolProviders.Skills;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Infrastructure.Adapters;
using Aevatar.Workflow.Application.Abstractions.Runs;
using FluentAssertions;

namespace Aevatar.GAgentService.Tests.Infrastructure;

public sealed class SkillWorkflowMountAdapterTests
{
    [Fact]
    public void Constructor_NullDependencies_Throw()
    {
        var commandPort = new RecordingScopeWorkflowCommandPort();
        var parser = new StubWorkflowDefinitionParser(new Dictionary<string, string>());

        var noCommandPort = () => new SkillWorkflowMountAdapter(null!, parser);
        var noParser = () => new SkillWorkflowMountAdapter(commandPort, null!);

        noCommandPort.Should().Throw<ArgumentNullException>().WithParameterName("scopeWorkflowCommandPort");
        noParser.Should().Throw<ArgumentNullException>().WithParameterName("workflowDefinitionParser");
    }

    [Fact]
    public async Task MountAsync_WithNoWorkflows_ReturnsNoWorkflowResult()
    {
        var commandPort = new RecordingScopeWorkflowCommandPort();
        var adapter = new SkillWorkflowMountAdapter(
            commandPort,
            new StubWorkflowDefinitionParser(new Dictionary<string, string>()));

        var result = await adapter.MountAsync(new SkillWorkflowMountRequest(
            ScopeId: "scope-1",
            NyxIdAccessToken: "token-a",
            Workflows: []));

        result.Status.Should().Be("no_workflows");
        result.Mounted.Should().BeFalse();
        result.Workflows.Should().BeEmpty();
        result.Message.Should().Be("The skill does not expose workflow YAML bundles.");
        commandPort.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task MountAsync_UsesScopeWorkflowUpsert_WithEquivalentRootAndInlineYamls()
    {
        var commandPort = new RecordingScopeWorkflowCommandPort();
        var parser = new StubWorkflowDefinitionParser(new Dictionary<string, string>
        {
            ["name: talisman_review\nsteps: []"] = "talisman_review",
            ["name: shared_child\nsteps: []"] = "shared_child",
        });
        var adapter = new SkillWorkflowMountAdapter(commandPort, parser);

        var result = await adapter.MountAsync(new SkillWorkflowMountRequest(
            ScopeId: "scope-1",
            NyxIdAccessToken: "token-a",
            Workflows:
            [
                new SkillWorkflowDescriptor
                {
                    WorkflowId = "talisman_review",
                    WorkflowYamls =
                    [
                        "name: talisman_review\nsteps: []",
                        "name: shared_child\nsteps: []",
                    ],
                },
            ]));

        commandPort.Requests.Should().ContainSingle();
        commandPort.Requests[0].ScopeId.Should().Be("scope-1");
        commandPort.Requests[0].WorkflowId.Should().Be("talisman_review");
        commandPort.Requests[0].WorkflowName.Should().Be("talisman_review");
        commandPort.Requests[0].WorkflowYaml.Should().Be("name: talisman_review\nsteps: []");
        commandPort.Requests[0].InlineWorkflowYamls.Should().ContainSingle();
        commandPort.Requests[0].InlineWorkflowYamls!["shared_child"].Should().Be("name: shared_child\nsteps: []");

        result.Status.Should().Be("mounted");
        result.Mounted.Should().BeTrue();
        result.Workflows.Should().ContainSingle();
        result.Workflows[0].WorkflowId.Should().Be("talisman_review");
        result.Workflows[0].ServiceId.Should().Be("talisman_review");
        result.Workflows[0].EndpointId.Should().Be("chat");
    }

    [Fact]
    public async Task MountAsync_Throws_WhenWorkflowBundleContainsDuplicateNames()
    {
        var commandPort = new RecordingScopeWorkflowCommandPort();
        var parser = new StubWorkflowDefinitionParser(new Dictionary<string, string>
        {
            ["name: duplicate\nsteps: []"] = "duplicate",
        });
        var adapter = new SkillWorkflowMountAdapter(commandPort, parser);

        var act = () => adapter.MountAsync(new SkillWorkflowMountRequest(
            ScopeId: "scope-1",
            NyxIdAccessToken: "token-a",
            Workflows:
            [
                new SkillWorkflowDescriptor
                {
                    WorkflowId = "duplicate",
                    WorkflowYamls =
                    [
                        "name: duplicate\nsteps: []",
                        "name: duplicate\nsteps: []",
                    ],
                },
            ]));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*duplicate workflow name*");
    }

    [Fact]
    public async Task MountAsync_Throws_WhenWorkflowHasNoYamlDocuments()
    {
        var adapter = new SkillWorkflowMountAdapter(
            new RecordingScopeWorkflowCommandPort(),
            new StubWorkflowDefinitionParser(new Dictionary<string, string>()));

        var act = () => adapter.MountAsync(new SkillWorkflowMountRequest(
            ScopeId: "scope-1",
            NyxIdAccessToken: "token-a",
            Workflows:
            [
                new SkillWorkflowDescriptor
                {
                    WorkflowId = "empty",
                    WorkflowYamls = [],
                },
            ]));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Skill workflow 'empty' does not include any YAML documents.");
    }

    [Fact]
    public async Task MountAsync_Throws_WhenWorkflowYamlIsBlank()
    {
        var adapter = new SkillWorkflowMountAdapter(
            new RecordingScopeWorkflowCommandPort(),
            new StubWorkflowDefinitionParser(new Dictionary<string, string>()));

        var act = () => adapter.MountAsync(new SkillWorkflowMountRequest(
            ScopeId: "scope-1",
            NyxIdAccessToken: "token-a",
            Workflows:
            [
                new SkillWorkflowDescriptor
                {
                    WorkflowId = "blank",
                    WorkflowYamls = ["  "],
                },
            ]));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Skill workflow 'blank' contains an empty YAML document.");
    }

    [Fact]
    public async Task MountAsync_Throws_WhenParserReturnsFailure()
    {
        var adapter = new SkillWorkflowMountAdapter(
            new RecordingScopeWorkflowCommandPort(),
            new StubWorkflowDefinitionParser(new Dictionary<string, string>
            {
                ["name: bad\nsteps: []"] = "bad",
            })
            {
                Failure = "parse failed",
            });

        var act = () => adapter.MountAsync(new SkillWorkflowMountRequest(
            ScopeId: "scope-1",
            NyxIdAccessToken: "token-a",
            Workflows:
            [
                new SkillWorkflowDescriptor
                {
                    WorkflowId = "bad",
                    WorkflowYamls = ["name: bad\nsteps: []"],
                },
            ]));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("parse failed");
    }

    [Fact]
    public async Task MountAsync_Throws_WhenParsedWorkflowNameIsBlank()
    {
        var adapter = new SkillWorkflowMountAdapter(
            new RecordingScopeWorkflowCommandPort(),
            new StubWorkflowDefinitionParser(new Dictionary<string, string>
            {
                ["name: blank\nsteps: []"] = " ",
            }));

        var act = () => adapter.MountAsync(new SkillWorkflowMountRequest(
            ScopeId: "scope-1",
            NyxIdAccessToken: "token-a",
            Workflows:
            [
                new SkillWorkflowDescriptor
                {
                    WorkflowId = "blank-name",
                    WorkflowYamls = ["name: blank\nsteps: []"],
                },
            ]));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Skill workflow 'blank-name' must define a workflow name.");
    }

    private sealed class RecordingScopeWorkflowCommandPort : IScopeWorkflowCommandPort
    {
        public List<ScopeWorkflowUpsertRequest> Requests { get; } = [];

        public Task<ScopeWorkflowUpsertResult> UpsertAsync(
            ScopeWorkflowUpsertRequest request,
            CancellationToken ct = default)
        {
            Requests.Add(request);
            return Task.FromResult(new ScopeWorkflowUpsertResult(
                ScopeId: request.ScopeId,
                WorkflowId: request.WorkflowId,
                ServiceKey: $"service:{request.WorkflowId}",
                RevisionId: "rev-1",
                DefinitionActorIdPrefix: $"prefix:{request.WorkflowId}",
                ExpectedActorId: $"actor:{request.WorkflowId}",
                ExpectedDeploymentId: "dep-1",
                AcceptedAtUtc: DateTimeOffset.UtcNow,
                CommandHandles: [],
                ReadModelUrl: $"/scopes/{request.ScopeId}/workflows/{request.WorkflowId}"));
        }
    }

    private sealed class StubWorkflowDefinitionParser : IWorkflowDefinitionParser
    {
        private readonly IReadOnlyDictionary<string, string> _workflowNamesByYaml;

        public StubWorkflowDefinitionParser(IReadOnlyDictionary<string, string> workflowNamesByYaml)
        {
            _workflowNamesByYaml = workflowNamesByYaml;
        }

        public string? Failure { get; init; }

        public Task<WorkflowYamlParseResult> ParseWorkflowYamlAsync(
            string workflowYaml,
            CancellationToken ct = default)
        {
            if (!_workflowNamesByYaml.TryGetValue(workflowYaml, out var workflowName))
                throw new InvalidOperationException($"Unexpected YAML: {workflowYaml}");

            if (Failure != null)
                return Task.FromResult(WorkflowYamlParseResult.Invalid(Failure));

            return Task.FromResult(WorkflowYamlParseResult.Success(workflowName));
        }
    }
}

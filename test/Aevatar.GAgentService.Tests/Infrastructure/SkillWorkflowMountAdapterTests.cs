using Aevatar.AI.ToolProviders.Skills;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Infrastructure.Adapters;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
using Aevatar.Workflow.Application.Abstractions.Runs;
using FluentAssertions;

namespace Aevatar.GAgentService.Tests.Infrastructure;

public sealed class SkillWorkflowMountAdapterTests
{
    [Fact]
    public void SkillWorkflowMountRequest_ToString_RedactsCredentialsAndWorkflows()
    {
        const string secretToken = "secret-token-sentinel-7f21c0";
        const string secretWorkflowContents = "name: workflow-secret-sentinel-5a93de\nsteps: []";
        var request = new SkillWorkflowMountRequest(
            ScopeId: "scope-alpha",
            SourceReadableNyxIdAccessToken: secretToken,
            Workflows:
            [
                new SkillWorkflowDescriptor
                {
                    WorkflowId = "workflow-alpha",
                    WorkflowYamls = [secretWorkflowContents],
                },
            ])
        {
            CallerId = "caller-alpha",
        };

        var rendered = request.ToString();

        rendered.Should().Contain("ScopeId = scope-alpha");
        rendered.Should().Contain("CallerId = caller-alpha");
        rendered.Should().Contain("Credentials = [REDACTED]");
        rendered.Should().Contain("Workflows = [REDACTED]");
        rendered.Should().Contain("WorkflowCount = 1");
        rendered.Should().NotContain(secretToken);
        rendered.Should().NotContain(secretWorkflowContents);
    }

    [Fact]
    public void Constructor_NullDependencies_Throw()
    {
        var commandPort = new RecordingScopeWorkflowCommandPort();
        var parser = new StubWorkflowDefinitionParser(new Dictionary<string, string>());
        var previewService = new RecordingWorkflowExplicitRequestPreviewService();

        var noCommandPort = () => new SkillWorkflowMountAdapter(null!, parser, previewService);
        var noParser = () => new SkillWorkflowMountAdapter(commandPort, null!, previewService);
        var noPreviewService = () => new SkillWorkflowMountAdapter(commandPort, parser, null!);

        noCommandPort.Should().Throw<ArgumentNullException>().WithParameterName("scopeWorkflowCommandPort");
        noParser.Should().Throw<ArgumentNullException>().WithParameterName("workflowDefinitionParser");
        noPreviewService.Should().Throw<ArgumentNullException>().WithParameterName("explicitRequestPreviewService");
    }

    [Fact]
    public async Task MountAsync_WithNoWorkflows_ReturnsNoWorkflowResult()
    {
        var commandPort = new RecordingScopeWorkflowCommandPort();
        var adapter = new SkillWorkflowMountAdapter(
            commandPort,
            new StubWorkflowDefinitionParser(new Dictionary<string, string>()),
            new RecordingWorkflowExplicitRequestPreviewService());

        var result = await adapter.MountAsync(new SkillWorkflowMountRequest(
            ScopeId: "scope-1",
            SourceReadableNyxIdAccessToken: "token-a",
            Workflows: []));

        result.Status.Should().Be("no_workflows");
        result.Mounted.Should().BeFalse();
        result.Workflows.Should().BeEmpty();
        result.Message.Should().Be("The skill does not expose workflow YAML bundles.");
        commandPort.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ConfirmAsync_DurablePreviewAndExactToken_ShouldNeverMutateWorkflowState()
    {
        const string yaml = "name: durable_workflow\nsteps: []";
        var commandPort = new RecordingScopeWorkflowCommandPort();
        var previewService = new RecordingWorkflowExplicitRequestPreviewService(
        [
            ExplicitRequestPreview(),
        ]);
        var adapter = new SkillWorkflowMountAdapter(
            commandPort,
            new StubWorkflowDefinitionParser(new Dictionary<string, string>
            {
                [yaml] = "durable_workflow",
            }),
            previewService);
        var request = new SkillWorkflowConfirmationRequest(
            "scope-alpha",
            "caller-alpha",
            "source-readable-token",
            [new SkillWorkflowDescriptor
            {
                WorkflowId = "durable_workflow",
                WorkflowYamls = [yaml],
            }],
            ExternalCapabilityExecutionMode.Durable);

        var preview = await adapter.ConfirmAsync(request);
        var confirmed = await adapter.ConfirmAsync(request with
        {
            ConfirmationToken = preview.ConfirmationToken!,
        });

        preview.Status.Should().Be("confirmation_required");
        preview.Confirmed.Should().BeFalse();
        preview.ConfirmationToken.Should().StartWith("sha256:");
        confirmed.Status.Should().Be("confirmed");
        confirmed.Confirmed.Should().BeTrue();
        previewService.Requests.Should().HaveCount(2);
        previewService.Requests.Should().OnlyContain(request =>
            request.ExecutionMode == ExternalCapabilityExecutionMode.Durable);
        commandPort.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ConfirmAsync_WhenTokenDoesNotMatch_ShouldRejectWithoutMutation()
    {
        const string yaml = "name: durable_workflow\nsteps: []";
        var commandPort = new RecordingScopeWorkflowCommandPort();
        var adapter = new SkillWorkflowMountAdapter(
            commandPort,
            new StubWorkflowDefinitionParser(new Dictionary<string, string>
            {
                [yaml] = "durable_workflow",
            }),
            new RecordingWorkflowExplicitRequestPreviewService());

        var result = await adapter.ConfirmAsync(new SkillWorkflowConfirmationRequest(
            "scope-alpha",
            "caller-alpha",
            "source-readable-token",
            [new SkillWorkflowDescriptor
            {
                WorkflowId = "durable_workflow",
                WorkflowYamls = [yaml],
            }],
            ExternalCapabilityExecutionMode.Durable)
        {
            ConfirmationToken = "sha256:forged",
        });

        result.Status.Should().Be("confirmation_mismatch");
        result.Confirmed.Should().BeFalse();
        result.FailureCode.Should().Be("USE_SKILL_MOUNT_CONFIRMATION_MISMATCH");
        commandPort.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ConfirmAsync_WhenAdmissionBlocks_ShouldReturnSafeBlockerCode()
    {
        const string yaml = "name: durable_workflow\nsteps: []";
        var commandPort = new RecordingScopeWorkflowCommandPort();
        var readiness = new ExternalCapabilityReadiness
        {
            Status = ExternalCapabilityReadinessStatus.ContractDrift,
            Blockers =
            {
                new ExternalCapabilityBlocker
                {
                    Status = ExternalCapabilityReadinessStatus.ContractDrift,
                    Code = "NYXID_EXPLICIT_REQUEST_CONFIRMATION_BINDING_MISMATCH",
                    SafeMessage = "The confirmation is bound to another workflow identity.",
                },
            },
        };
        var adapter = new SkillWorkflowMountAdapter(
            commandPort,
            new StubWorkflowDefinitionParser(new Dictionary<string, string>
            {
                [yaml] = "durable_workflow",
            }),
            new RecordingWorkflowExplicitRequestPreviewService
            {
                Exception = new WorkflowExternalCapabilityAdmissionException(readiness),
            });

        var result = await adapter.ConfirmAsync(new SkillWorkflowConfirmationRequest(
            "scope-alpha",
            "caller-alpha",
            "source-readable-token",
            [new SkillWorkflowDescriptor
            {
                WorkflowId = "durable_workflow",
                WorkflowYamls = [yaml],
            }],
            ExternalCapabilityExecutionMode.Durable));

        result.Status.Should().Be("capability_admission_blocked");
        result.Confirmed.Should().BeFalse();
        result.FailureCode.Should().Be("NYXID_EXPLICIT_REQUEST_CONFIRMATION_BINDING_MISMATCH");
        result.Message.Should().Be("The confirmation is bound to another workflow identity.");
        commandPort.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ConfirmAsync_WhenTokenWasIssuedForInteractiveMode_ShouldRejectDurableUse()
    {
        const string yaml = "name: mode_bound_workflow\nsteps: []";
        var commandPort = new RecordingScopeWorkflowCommandPort();
        var adapter = new SkillWorkflowMountAdapter(
            commandPort,
            new StubWorkflowDefinitionParser(new Dictionary<string, string>
            {
                [yaml] = "mode_bound_workflow",
            }),
            new RecordingWorkflowExplicitRequestPreviewService());
        var interactiveRequest = new SkillWorkflowConfirmationRequest(
            "scope-alpha",
            "caller-alpha",
            "source-readable-token",
            [new SkillWorkflowDescriptor
            {
                WorkflowId = "mode_bound_workflow",
                WorkflowYamls = [yaml],
            }],
            ExternalCapabilityExecutionMode.Interactive);

        var interactive = await adapter.ConfirmAsync(interactiveRequest);
        var durable = await adapter.ConfirmAsync(interactiveRequest with
        {
            ExecutionMode = ExternalCapabilityExecutionMode.Durable,
            ConfirmationToken = interactive.ConfirmationToken!,
        });

        durable.Status.Should().Be("confirmation_mismatch");
        durable.Confirmed.Should().BeFalse();
        commandPort.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task MountAsync_PreviewsBeforeUpsert_ThenMountsTheExactReviewedBundle()
    {
        var commandPort = new RecordingScopeWorkflowCommandPort();
        var parser = new StubWorkflowDefinitionParser(new Dictionary<string, string>
        {
            ["name: talisman_review\nsteps: []"] = "talisman_review",
            ["name: shared_child\nsteps: []"] = "shared_child",
        });
        var previewService = new RecordingWorkflowExplicitRequestPreviewService();
        var adapter = new SkillWorkflowMountAdapter(commandPort, parser, previewService);

        var request = new SkillWorkflowMountRequest(
            ScopeId: "scope-1",
            SourceReadableNyxIdAccessToken: "token-a",
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
            ])
        {
            CallerId = "caller-alpha",
        };

        var preview = await adapter.MountAsync(request);

        preview.Status.Should().Be("confirmation_required");
        preview.Mounted.Should().BeFalse();
        preview.ConfirmationRequests.Should().ContainSingle();
        commandPort.Requests.Should().BeEmpty();

        var confirmation = preview.ConfirmationRequests![0].Confirmation;
        var result = await adapter.MountAsync(request with { Confirmations = [confirmation] });

        commandPort.Requests.Should().ContainSingle();
        commandPort.Requests[0].ScopeId.Should().Be("scope-1");
        commandPort.Requests[0].WorkflowId.Should().Be("talisman_review");
        commandPort.Requests[0].WorkflowName.Should().Be("talisman_review");
        commandPort.Requests[0].WorkflowYaml.Should().Be("name: talisman_review\nsteps: []");
        commandPort.Requests[0].InlineWorkflowYamls.Should().ContainSingle();
        commandPort.Requests[0].InlineWorkflowYamls!["shared_child"].Should().Be("name: shared_child\nsteps: []");
        commandPort.Requests[0].CapabilityAdmission.Should().NotBeNull();
        commandPort.Requests[0].CapabilityAdmission!.CallerId.Should().Be("caller-alpha");
        commandPort.Requests[0].RevisionId.Should().Be(confirmation.RevisionId);
        previewService.Requests.Should().HaveCount(2);

        result.Status.Should().Be("mounted");
        result.Mounted.Should().BeTrue();
        result.Workflows.Should().ContainSingle();
        result.Workflows[0].WorkflowId.Should().Be("talisman_review");
        result.Workflows[0].ServiceId.Should().Be("talisman_review");
        result.Workflows[0].EndpointId.Should().Be("chat");
    }

    [Fact]
    public async Task MountAsync_WithOpaquePreviewToken_MountsServerRecomputedConfirmations()
    {
        const string yaml = "name: guarded_workflow\nsteps: []";
        var commandPort = new RecordingScopeWorkflowCommandPort();
        var adapter = new SkillWorkflowMountAdapter(
            commandPort,
            new StubWorkflowDefinitionParser(new Dictionary<string, string>
            {
                [yaml] = "guarded_workflow",
            }),
            new RecordingWorkflowExplicitRequestPreviewService(
            [
                ExplicitRequestPreview(),
            ]));
        var request = MountRequest("guarded_workflow", yaml);

        var preview = await adapter.MountAsync(request);
        var result = await adapter.MountAsync(request with
        {
            ConfirmationToken = preview.ConfirmationToken!,
        });

        preview.Status.Should().Be("confirmation_required");
        preview.ConfirmationToken.Should().StartWith("sha256:").And.HaveLength(71);
        result.Status.Should().Be("mounted");
        commandPort.Requests.Should().ContainSingle();
        commandPort.Requests[0].CapabilityAdmission!.ExplicitRequestConfirmations.Should().ContainSingle();
    }

    [Fact]
    public async Task MountAsync_WithChangedOpaquePreviewToken_RejectsBeforeUpsert()
    {
        const string yaml = "name: guarded_workflow\nsteps: []";
        var commandPort = new RecordingScopeWorkflowCommandPort();
        var adapter = new SkillWorkflowMountAdapter(
            commandPort,
            new StubWorkflowDefinitionParser(new Dictionary<string, string>
            {
                [yaml] = "guarded_workflow",
            }),
            new RecordingWorkflowExplicitRequestPreviewService(
            [
                ExplicitRequestPreview(),
            ]));
        var request = MountRequest("guarded_workflow", yaml);

        var result = await adapter.MountAsync(request with
        {
            ConfirmationToken = "sha256:forged",
        });

        result.Status.Should().Be("confirmation_mismatch");
        result.FailureCode.Should().Be("USE_SKILL_MOUNT_CONFIRMATION_MISMATCH");
        commandPort.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("bundle")]
    [InlineData("call_site")]
    [InlineData("request_digest")]
    [InlineData("risk")]
    public async Task MountAsync_WhenReviewedConfirmationChanges_RejectsBeforeUpsert(string mutation)
    {
        const string yaml = "name: guarded_workflow\nsteps: []";
        var commandPort = new RecordingScopeWorkflowCommandPort();
        var previewService = new RecordingWorkflowExplicitRequestPreviewService(
        [
            ExplicitRequestPreview(),
        ]);
        var adapter = new SkillWorkflowMountAdapter(
            commandPort,
            new StubWorkflowDefinitionParser(new Dictionary<string, string>
            {
                [yaml] = "guarded_workflow",
            }),
            previewService);
        var request = MountRequest("guarded_workflow", yaml);
        var preview = await adapter.MountAsync(request);
        var confirmation = preview.ConfirmationRequests!.Single().Confirmation;
        var explicitRequest = confirmation.ExplicitRequests.Single();
        var mutated = mutation switch
        {
            "revision" => confirmation with { RevisionId = "rev-forged" },
            "bundle" => confirmation with { WorkflowBundleDigest = "sha256:forged" },
            "call_site" => confirmation with
            {
                ExplicitRequests = [explicitRequest with { CallSiteId = "forged/call-site" }],
            },
            "request_digest" => confirmation with
            {
                ExplicitRequests = [explicitRequest with { RequestContractDigest = "sha256:forged" }],
            },
            "risk" => confirmation with
            {
                ExplicitRequests = [explicitRequest with { AttestedRisk = NyxIdOperationRisk.ReadOnly }],
            },
            _ => throw new InvalidOperationException($"Unknown test mutation '{mutation}'."),
        };

        var result = await adapter.MountAsync(request with { Confirmations = [mutated] });

        result.Status.Should().Be("confirmation_mismatch");
        result.FailureCode.Should().Be("USE_SKILL_MOUNT_CONFIRMATION_MISMATCH");
        result.Mounted.Should().BeFalse();
        commandPort.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task MountAsync_WithExactExplicitRequestConfirmation_ForwardsTypedGrant()
    {
        const string yaml = "name: guarded_workflow\nsteps: []";
        var commandPort = new RecordingScopeWorkflowCommandPort();
        var adapter = new SkillWorkflowMountAdapter(
            commandPort,
            new StubWorkflowDefinitionParser(new Dictionary<string, string>
            {
                [yaml] = "guarded_workflow",
            }),
            new RecordingWorkflowExplicitRequestPreviewService(
            [
                ExplicitRequestPreview(),
            ]));
        var request = MountRequest("guarded_workflow", yaml);
        var preview = await adapter.MountAsync(request);
        var confirmation = preview.ConfirmationRequests!.Single().Confirmation;

        var result = await adapter.MountAsync(request with { Confirmations = [confirmation] });

        result.Status.Should().Be("mounted");
        var admission = commandPort.Requests.Should().ContainSingle().Which.CapabilityAdmission;
        admission.Should().NotBeNull();
        var forwarded = admission!.ExplicitRequestConfirmations.Should().ContainSingle().Which;
        forwarded.WorkflowId.Should().Be(confirmation.WorkflowId);
        forwarded.RevisionId.Should().Be(confirmation.RevisionId);
        forwarded.CallSiteId.Should().Be("guarded_workflow/fetch");
        forwarded.RequestContractDigest.Should().Be("sha256:request-alpha");
        forwarded.AttestedRisk.Should().Be(NyxIdOperationRisk.Write);
    }

    [Fact]
    public async Task MountAsync_Throws_WhenAuthenticatedCallerIdentityIsMissing()
    {
        var commandPort = new RecordingScopeWorkflowCommandPort();
        var adapter = new SkillWorkflowMountAdapter(
            commandPort,
            new StubWorkflowDefinitionParser(new Dictionary<string, string>
            {
                ["name: caller_required\nsteps: []"] = "caller_required",
            }),
            new RecordingWorkflowExplicitRequestPreviewService());

        var act = () => adapter.MountAsync(new SkillWorkflowMountRequest(
            ScopeId: "scope-alpha",
            SourceReadableNyxIdAccessToken: "token-a",
            Workflows:
            [
                new SkillWorkflowDescriptor
                {
                    WorkflowId = "caller_required",
                    WorkflowYamls = ["name: caller_required\nsteps: []"],
                },
            ]));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*authenticated caller identity*");
        commandPort.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task MountAsync_Throws_WhenWorkflowBundleContainsDuplicateNames()
    {
        var commandPort = new RecordingScopeWorkflowCommandPort();
        var parser = new StubWorkflowDefinitionParser(new Dictionary<string, string>
        {
            ["name: duplicate\nsteps: []"] = "duplicate",
        });
        var adapter = new SkillWorkflowMountAdapter(
            commandPort,
            parser,
            new RecordingWorkflowExplicitRequestPreviewService());

        var act = () => adapter.MountAsync(new SkillWorkflowMountRequest(
            ScopeId: "scope-1",
            SourceReadableNyxIdAccessToken: "token-a",
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
            ])
        {
            CallerId = "caller-alpha",
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*duplicate workflow name*");
    }

    [Fact]
    public async Task MountAsync_Throws_WhenWorkflowHasNoYamlDocuments()
    {
        var adapter = new SkillWorkflowMountAdapter(
            new RecordingScopeWorkflowCommandPort(),
            new StubWorkflowDefinitionParser(new Dictionary<string, string>()),
            new RecordingWorkflowExplicitRequestPreviewService());

        var act = () => adapter.MountAsync(new SkillWorkflowMountRequest(
            ScopeId: "scope-1",
            SourceReadableNyxIdAccessToken: "token-a",
            Workflows:
            [
                new SkillWorkflowDescriptor
                {
                    WorkflowId = "empty",
                    WorkflowYamls = [],
                },
            ])
        {
            CallerId = "caller-alpha",
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Skill workflow 'empty' does not include any YAML documents.");
    }

    [Fact]
    public async Task MountAsync_Throws_WhenWorkflowYamlIsBlank()
    {
        var adapter = new SkillWorkflowMountAdapter(
            new RecordingScopeWorkflowCommandPort(),
            new StubWorkflowDefinitionParser(new Dictionary<string, string>()),
            new RecordingWorkflowExplicitRequestPreviewService());

        var act = () => adapter.MountAsync(new SkillWorkflowMountRequest(
            ScopeId: "scope-1",
            SourceReadableNyxIdAccessToken: "token-a",
            Workflows:
            [
                new SkillWorkflowDescriptor
                {
                    WorkflowId = "blank",
                    WorkflowYamls = ["  "],
                },
            ])
        {
            CallerId = "caller-alpha",
        });

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
            },
            new RecordingWorkflowExplicitRequestPreviewService());

        var act = () => adapter.MountAsync(new SkillWorkflowMountRequest(
            ScopeId: "scope-1",
            SourceReadableNyxIdAccessToken: "token-a",
            Workflows:
            [
                new SkillWorkflowDescriptor
                {
                    WorkflowId = "bad",
                    WorkflowYamls = ["name: bad\nsteps: []"],
                },
            ])
        {
            CallerId = "caller-alpha",
        });

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
            }),
            new RecordingWorkflowExplicitRequestPreviewService());

        var act = () => adapter.MountAsync(new SkillWorkflowMountRequest(
            ScopeId: "scope-1",
            SourceReadableNyxIdAccessToken: "token-a",
            Workflows:
            [
                new SkillWorkflowDescriptor
                {
                    WorkflowId = "blank-name",
                    WorkflowYamls = ["name: blank\nsteps: []"],
                },
            ])
        {
            CallerId = "caller-alpha",
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Skill workflow 'blank-name' must define a workflow name.");
    }

    private static SkillWorkflowMountRequest MountRequest(string workflowId, string yaml) =>
        new(
            "scope-alpha",
            "token-alpha",
            [
                new SkillWorkflowDescriptor
                {
                    WorkflowId = workflowId,
                    WorkflowYamls = [yaml],
                },
            ])
        {
            CallerId = "caller-alpha",
        };

    private static WorkflowExplicitRequestPreviewItem ExplicitRequestPreview() =>
        new(
            "guarded_workflow/fetch",
            "sha256:request-alpha",
            "service-alpha",
            NyxIdRequestMethod.Post,
            "/records/search",
            NyxIdRequestBodyMode.Json,
            true,
            NyxIdRequestResponseMode.Text,
            NyxIdOperationRisk.Write,
            true,
            [ExternalCapabilityExecutionMode.Interactive]);

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

    private sealed class RecordingWorkflowExplicitRequestPreviewService(
        IReadOnlyList<WorkflowExplicitRequestPreviewItem>? items = null) :
        IWorkflowExplicitRequestPreviewService
    {
        public List<WorkflowExplicitRequestPreviewRequest> Requests { get; } = [];
        public Exception? Exception { get; init; }

        public Task<WorkflowExplicitRequestPreviewResult> PreviewAsync(
            WorkflowExplicitRequestPreviewRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            if (Exception != null)
                throw Exception;
            return Task.FromResult(new WorkflowExplicitRequestPreviewResult(
                request.WorkflowId ?? string.Empty,
                request.RevisionId ?? string.Empty,
                items ?? []));
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

        public async Task<WorkflowInlineYamlBundleParseResult> ParseInlineWorkflowBundleAsync(
            IReadOnlyList<WorkflowChatInlineYamlDocument> inlineWorkflowDocuments,
            CancellationToken ct = default)
        {
            if (inlineWorkflowDocuments.Count == 0)
                return WorkflowInlineYamlBundleParseResult.Invalid("workflowYamls is required.");

            var workflowYamlsByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string entryWorkflowName = string.Empty;
            string entryWorkflowYaml = string.Empty;

            for (var i = 0; i < inlineWorkflowDocuments.Count; i++)
            {
                var document = inlineWorkflowDocuments[i];
                var parseResult = await ParseWorkflowYamlAsync(document.Yaml, ct);
                if (!parseResult.Succeeded)
                    return WorkflowInlineYamlBundleParseResult.Invalid(parseResult.Error, parseResult.ExternalCapabilityReadiness);

                if (!workflowYamlsByName.TryAdd(parseResult.WorkflowName, document.Yaml))
                    return WorkflowInlineYamlBundleParseResult.Invalid($"Duplicate workflow name '{parseResult.WorkflowName}' in workflowYamls.");

                if (i == 0)
                {
                    entryWorkflowName = parseResult.WorkflowName;
                    entryWorkflowYaml = document.Yaml;
                }
            }

            return WorkflowInlineYamlBundleParseResult.Success(entryWorkflowName, entryWorkflowYaml, workflowYamlsByName);
        }
    }
}

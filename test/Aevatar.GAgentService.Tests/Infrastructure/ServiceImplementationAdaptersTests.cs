using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core.TypeSystem;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Infrastructure.Adapters;
using Aevatar.GAgentService.Tests.TestSupport;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Core.Ports;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
using Aevatar.Workflow.Application.Abstractions.Runs;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Tests.Infrastructure;

public sealed class ServiceImplementationAdaptersTests
{
    private const string LegacyWorkflowYaml = "name: rebound-workflow\nsteps: []\n";

    [Fact]
    public async Task ScriptingAdapter_ShouldValidateConstructorAndRequest()
    {
        Action nullPort = () => new ScriptingServiceImplementationAdapter(null!);
        var adapter = new ScriptingServiceImplementationAdapter(
            new RecordingScriptDefinitionSnapshotPort(new ScriptDefinitionSnapshot(
                ScriptId: "script-1",
                Revision: "r1",
                SourceText: "// source",
                SourceHash: "hash-1",
                StateTypeUrl: "type.googleapis.com/test.State",
                ReadModelTypeUrl: "type.googleapis.com/test.ReadModel",
                ReadModelSchemaVersion: "1",
                ReadModelSchemaHash: "rm-hash",
                RuntimeSemantics: new ScriptRuntimeSemanticsSpec
                {
                    Messages =
                    {
                        new ScriptMessageSemanticsSpec
                        {
                            TypeUrl = "type.googleapis.com/test.Command",
                            DescriptorFullName = "test.Command",
                            Kind = ScriptMessageKind.Command,
                        },
                    },
                })));
        var nullRequest = () => adapter.PrepareRevisionAsync(null!);

        nullPort.Should().Throw<ArgumentNullException>();
        await nullRequest.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task StaticAdapter_ShouldPrepareRevisionArtifact()
    {
        var adapter = new StaticServiceImplementationAdapter(CreateStaticAgentKindRegistry());
        var request = new PrepareServiceRevisionRequest
        {
            ServiceKey = "tenant:app:default:svc",
            Spec = GAgentServiceTestKit.CreateStaticRevisionSpec(),
        };

        var artifact = await adapter.PrepareRevisionAsync(request);

        artifact.ImplementationKind.Should().Be(ServiceImplementationKind.Static);
        artifact.Endpoints.Should().ContainSingle(x => x.EndpointId == "run");
        artifact.DeploymentPlan.StaticPlan.AgentKind.Should().Be(GAgentServiceTestKit.TestStaticServiceAgentKind);
        artifact.DeploymentPlan.StaticPlan.ActorTypeName.Should().Be(typeof(TestStaticServiceAgent).AssemblyQualifiedName);
        artifact.DeploymentPlan.StaticPlan.PreferredActorId.Should().Be("static:r1");
    }

    [Fact]
    public async Task StaticAdapter_ShouldRejectLegacyActorTypeNameAsIdentity()
    {
        var adapter = new StaticServiceImplementationAdapter(CreateStaticAgentKindRegistry());
        var request = new PrepareServiceRevisionRequest
        {
            Spec = GAgentServiceTestKit.CreateStaticRevisionSpec(
                actorTypeName: typeof(TestStaticServiceAgent).AssemblyQualifiedName,
                agentKind: string.Empty),
        };

        var act = () => adapter.PrepareRevisionAsync(request);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"Static actor_type_name '{typeof(TestStaticServiceAgent).AssemblyQualifiedName}' is deprecated and cannot be used for identity. Provide static agent_kind.");
    }

    [Fact]
    public async Task StaticAdapter_ShouldRejectMissingAgentKind()
    {
        var adapter = new StaticServiceImplementationAdapter(CreateStaticAgentKindRegistry());
        var request = new PrepareServiceRevisionRequest
        {
            Spec = GAgentServiceTestKit.CreateStaticRevisionSpec(actorTypeName: string.Empty, agentKind: string.Empty),
        };

        var act = () => adapter.PrepareRevisionAsync(request);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("static agent_kind is required.");
    }

    [Fact]
    public async Task StaticAdapter_ShouldRejectMissingEndpoints()
    {
        var adapter = new StaticServiceImplementationAdapter(CreateStaticAgentKindRegistry());
        var spec = GAgentServiceTestKit.CreateStaticRevisionSpec();
        spec.StaticSpec.Endpoints.Clear();

        var act = () => adapter.PrepareRevisionAsync(new PrepareServiceRevisionRequest
        {
            Spec = spec,
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("static endpoints are required.");
    }

    [Fact]
    public async Task StaticAdapter_ShouldRejectUnknownAgentKind()
    {
        var adapter = new StaticServiceImplementationAdapter(CreateStaticAgentKindRegistry());

        var act = () => adapter.PrepareRevisionAsync(new PrepareServiceRevisionRequest
        {
            Spec = GAgentServiceTestKit.CreateStaticRevisionSpec(agentKind: "tests.missing-static-agent"),
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*tests.missing-static-agent*");
    }

    [Fact]
    public async Task ScriptingAdapter_ShouldPrepareArtifactFromRuntimeSemantics()
    {
        var snapshotPort = new RecordingScriptDefinitionSnapshotPort(new ScriptDefinitionSnapshot(
            ScriptId: "script-1",
            Revision: "r1",
            SourceText: "// source",
            SourceHash: "hash-1",
            StateTypeUrl: "type.googleapis.com/test.State",
            ReadModelTypeUrl: "type.googleapis.com/test.ReadModel",
            ReadModelSchemaVersion: "1",
            ReadModelSchemaHash: "rm-hash",
            ProtocolDescriptorSet: ByteString.CopyFromUtf8("descriptor"),
            RuntimeSemantics: new ScriptRuntimeSemanticsSpec
            {
                Messages =
                {
                    new ScriptMessageSemanticsSpec
                    {
                        TypeUrl = "type.googleapis.com/test.Command",
                        DescriptorFullName = "test.Command",
                        Kind = ScriptMessageKind.Command,
                    },
                },
            }));
        var adapter = new ScriptingServiceImplementationAdapter(snapshotPort);
        var request = new PrepareServiceRevisionRequest
        {
            Spec = new ServiceRevisionSpec
            {
                Identity = GAgentServiceTestKit.CreateIdentity(),
                RevisionId = "service-r1",
                ImplementationKind = ServiceImplementationKind.Scripting,
                ScriptingSpec = new ScriptingServiceRevisionSpec
                {
                    ScriptId = "script-1",
                    Revision = "r1",
                    DefinitionActorId = "script-definition-1",
                },
            },
        };

        var artifact = await adapter.PrepareRevisionAsync(request);

        artifact.ImplementationKind.Should().Be(ServiceImplementationKind.Scripting);
        artifact.ProtocolDescriptorSet.ToStringUtf8().Should().Be("descriptor");
        artifact.Endpoints.Should().ContainSingle();
        artifact.Endpoints[0].EndpointId.Should().Be("test.Command");
        artifact.DeploymentPlan.ScriptingPlan.ScriptId.Should().Be("script-1");
        artifact.DeploymentPlan.ScriptingPlan.DefinitionActorId.Should().Be("script-definition-1");
        snapshotPort.Calls.Should().ContainSingle();
        snapshotPort.Calls[0].definitionActorId.Should().Be("script-definition-1");
        snapshotPort.Calls[0].revision.Should().Be("r1");
    }

    [Fact]
    public async Task ScriptingAdapter_ShouldRejectMissingCommandEndpoints()
    {
        var adapter = new ScriptingServiceImplementationAdapter(
            new RecordingScriptDefinitionSnapshotPort(new ScriptDefinitionSnapshot(
                ScriptId: "script-1",
                Revision: "r1",
                SourceText: "// source",
                SourceHash: "hash-1",
                StateTypeUrl: "type.googleapis.com/test.State",
                ReadModelTypeUrl: "type.googleapis.com/test.ReadModel",
                ReadModelSchemaVersion: "1",
                ReadModelSchemaHash: "rm-hash",
                RuntimeSemantics: new ScriptRuntimeSemanticsSpec())));

        var act = () => adapter.PrepareRevisionAsync(new PrepareServiceRevisionRequest
        {
            Spec = new ServiceRevisionSpec
            {
                Identity = GAgentServiceTestKit.CreateIdentity(),
                RevisionId = "service-r1",
                ImplementationKind = ServiceImplementationKind.Scripting,
                ScriptingSpec = new ScriptingServiceRevisionSpec
                {
                    ScriptId = "script-1",
                    Revision = "r1",
                    DefinitionActorId = "script-definition-1",
                },
            },
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*does not declare command endpoints*");
    }

    [Fact]
    public async Task ScriptingAdapter_ShouldRejectMissingDefinitionActorId()
    {
        var adapter = new ScriptingServiceImplementationAdapter(
            new RecordingScriptDefinitionSnapshotPort(new ScriptDefinitionSnapshot(
                ScriptId: "script-1",
                Revision: "r1",
                SourceText: "// source",
                SourceHash: "hash-1",
                StateTypeUrl: "type.googleapis.com/test.State",
                ReadModelTypeUrl: "type.googleapis.com/test.ReadModel",
                ReadModelSchemaVersion: "1",
                ReadModelSchemaHash: "rm-hash",
                RuntimeSemantics: new ScriptRuntimeSemanticsSpec())));

        var act = () => adapter.PrepareRevisionAsync(new PrepareServiceRevisionRequest
        {
            Spec = new ServiceRevisionSpec
            {
                Identity = GAgentServiceTestKit.CreateIdentity(),
                RevisionId = "service-r1",
                ImplementationKind = ServiceImplementationKind.Scripting,
                ScriptingSpec = new ScriptingServiceRevisionSpec
                {
                    ScriptId = "script-1",
                    Revision = "r1",
                    DefinitionActorId = string.Empty,
                },
            },
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("scripting definition_actor_id is required.");
    }

    [Fact]
    public async Task ScriptingAdapter_ShouldRejectMissingScriptingSpec()
    {
        var snapshotPort = new RecordingScriptDefinitionSnapshotPort(new ScriptDefinitionSnapshot(
            ScriptId: "script-1",
            Revision: "r1",
            SourceText: "// source",
            SourceHash: "hash-1",
            StateTypeUrl: "type.googleapis.com/test.State",
            ReadModelTypeUrl: "type.googleapis.com/test.ReadModel",
            ReadModelSchemaVersion: "1",
            ReadModelSchemaHash: "rm-hash",
            RuntimeSemantics: new ScriptRuntimeSemanticsSpec
            {
                Messages =
                {
                    new ScriptMessageSemanticsSpec
                    {
                        TypeUrl = "type.googleapis.com/test.Command",
                        Kind = ScriptMessageKind.Command,
                    },
                },
            }));
        var adapter = new ScriptingServiceImplementationAdapter(snapshotPort);

        var act = () => adapter.PrepareRevisionAsync(new PrepareServiceRevisionRequest
        {
            Spec = new ServiceRevisionSpec
            {
                Identity = GAgentServiceTestKit.CreateIdentity(),
                RevisionId = "service-r1",
                ImplementationKind = ServiceImplementationKind.Scripting,
            },
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("scripting implementation_spec is required.");
    }

    [Fact]
    public async Task ScriptingAdapter_ShouldFallbackToTypeUrlAndNormalizePackageFields()
    {
        var snapshotPort = new RecordingScriptDefinitionSnapshotPort(new ScriptDefinitionSnapshot(
            ScriptId: "script-2",
            Revision: "r2",
            SourceText: "// source",
            SourceHash: "hash-2",
            StateTypeUrl: "type.googleapis.com/test.State",
            ReadModelTypeUrl: "type.googleapis.com/test.ReadModel",
            ReadModelSchemaVersion: "2",
            ReadModelSchemaHash: "rm-hash-2",
            ProtocolDescriptorSet: ByteString.CopyFromUtf8("descriptor-2"),
            RuntimeSemantics: new ScriptRuntimeSemanticsSpec
            {
                Messages =
                {
                    new ScriptMessageSemanticsSpec
                    {
                        TypeUrl = "type.googleapis.com/test.CommandOnlyTypeUrl",
                        Kind = ScriptMessageKind.Command,
                    },
                    new ScriptMessageSemanticsSpec
                    {
                        TypeUrl = "type.googleapis.com/test.Signal",
                        DescriptorFullName = "test.Signal",
                        Kind = ScriptMessageKind.InternalSignal,
                    },
                },
            },
            ScriptPackage: new ScriptPackageSpec
            {
                CsharpSources =
                {
                    new ScriptPackageFile(),
                },
                ProtoFiles =
                {
                    new ScriptPackageFile(),
                },
            }));
        var adapter = new ScriptingServiceImplementationAdapter(snapshotPort);

        var artifact = await adapter.PrepareRevisionAsync(new PrepareServiceRevisionRequest
        {
            Spec = new ServiceRevisionSpec
            {
                Identity = GAgentServiceTestKit.CreateIdentity(),
                RevisionId = "service-r2",
                ImplementationKind = ServiceImplementationKind.Scripting,
                ScriptingSpec = new ScriptingServiceRevisionSpec
                {
                    ScriptId = "script-2",
                    Revision = "r2",
                    DefinitionActorId = "script-definition-2",
                },
            },
        });

        artifact.Endpoints.Should().ContainSingle();
        artifact.Endpoints[0].EndpointId.Should().Be("type.googleapis.com/test.CommandOnlyTypeUrl");
        artifact.Endpoints[0].DisplayName.Should().Be("type.googleapis.com/test.CommandOnlyTypeUrl");
        artifact.DeploymentPlan.ScriptingPlan.PackageSpec.EntryBehaviorTypeName.Should().BeEmpty();
        artifact.DeploymentPlan.ScriptingPlan.PackageSpec.EntrySourcePath.Should().BeEmpty();
        artifact.DeploymentPlan.ScriptingPlan.PackageSpec.CsharpSources.Should().ContainSingle();
        artifact.DeploymentPlan.ScriptingPlan.PackageSpec.CsharpSources[0].Path.Should().BeEmpty();
        artifact.DeploymentPlan.ScriptingPlan.PackageSpec.ProtoFiles.Should().ContainSingle();
        artifact.DeploymentPlan.ScriptingPlan.PackageSpec.ProtoFiles[0].Content.Should().BeEmpty();
    }

    [Fact]
    public async Task ScriptingAdapter_ShouldUseDescriptorFullName_AndPreservePackagePayloads()
    {
        var snapshotPort = new RecordingScriptDefinitionSnapshotPort(new ScriptDefinitionSnapshot(
            ScriptId: "script-3",
            Revision: "r3",
            SourceText: "// source",
            SourceHash: "hash-3",
            StateTypeUrl: "type.googleapis.com/test.State",
            ReadModelTypeUrl: "type.googleapis.com/test.ReadModel",
            ReadModelSchemaVersion: "3",
            ReadModelSchemaHash: "rm-hash-3",
            ProtocolDescriptorSet: ByteString.CopyFromUtf8("descriptor-3"),
            RuntimeSemantics: new ScriptRuntimeSemanticsSpec
            {
                Messages =
                {
                    new ScriptMessageSemanticsSpec
                    {
                        TypeUrl = "type.googleapis.com/test.Command",
                        DescriptorFullName = "test.Command",
                        Kind = ScriptMessageKind.Command,
                    },
                    new ScriptMessageSemanticsSpec
                    {
                        TypeUrl = "type.googleapis.com/test.Query",
                        DescriptorFullName = "test.Query",
                        Kind = ScriptMessageKind.QueryRequest,
                    },
                },
            },
            ScriptPackage: new ScriptPackageSpec
            {
                EntryBehaviorTypeName = "Demo.Behavior",
                EntrySourcePath = "src/Behavior.cs",
                CsharpSources =
                {
                    new ScriptPackageFile
                    {
                        Path = "src/Behavior.cs",
                        Content = "public sealed class Behavior {}",
                    },
                },
                ProtoFiles =
                {
                    new ScriptPackageFile
                    {
                        Path = "protos/demo.proto",
                        Content = "syntax = \"proto3\";",
                    },
                },
            }));
        var adapter = new ScriptingServiceImplementationAdapter(snapshotPort);

        var artifact = await adapter.PrepareRevisionAsync(new PrepareServiceRevisionRequest
        {
            Spec = new ServiceRevisionSpec
            {
                Identity = GAgentServiceTestKit.CreateIdentity(),
                RevisionId = "service-r3",
                ImplementationKind = ServiceImplementationKind.Scripting,
                ScriptingSpec = new ScriptingServiceRevisionSpec
                {
                    ScriptId = "script-3",
                    Revision = "r3",
                    DefinitionActorId = "script-definition-3",
                },
            },
        });

        artifact.Endpoints.Should().ContainSingle();
        artifact.Endpoints[0].EndpointId.Should().Be("test.Command");
        artifact.Endpoints[0].DisplayName.Should().Be("test.Command");
        artifact.Endpoints[0].RequestTypeUrl.Should().Be("type.googleapis.com/test.Command");
        artifact.DeploymentPlan.ScriptingPlan.PackageSpec.EntryBehaviorTypeName.Should().Be("Demo.Behavior");
        artifact.DeploymentPlan.ScriptingPlan.PackageSpec.EntrySourcePath.Should().Be("src/Behavior.cs");
        artifact.DeploymentPlan.ScriptingPlan.PackageSpec.CsharpSources.Should().ContainSingle();
        artifact.DeploymentPlan.ScriptingPlan.PackageSpec.CsharpSources[0].Path.Should().Be("src/Behavior.cs");
        artifact.DeploymentPlan.ScriptingPlan.PackageSpec.CsharpSources[0].Content.Should().Contain("Behavior");
        artifact.DeploymentPlan.ScriptingPlan.PackageSpec.ProtoFiles.Should().ContainSingle();
        artifact.DeploymentPlan.ScriptingPlan.PackageSpec.ProtoFiles[0].Path.Should().Be("protos/demo.proto");
        artifact.DeploymentPlan.ScriptingPlan.PackageSpec.ProtoFiles[0].Content.Should().Contain("proto3");
    }

    [Fact]
    public async Task ScriptingAdapter_ShouldKeepEmptyEndpointId_WhenNoDescriptorOrTypeUrlExists()
    {
        var snapshotPort = new RecordingScriptDefinitionSnapshotPort(new ScriptDefinitionSnapshot(
            ScriptId: "script-4",
            Revision: "r4",
            SourceText: "// source",
            SourceHash: "hash-4",
            StateTypeUrl: "type.googleapis.com/test.State",
            ReadModelTypeUrl: "type.googleapis.com/test.ReadModel",
            ReadModelSchemaVersion: "4",
            ReadModelSchemaHash: "rm-hash-4",
            RuntimeSemantics: new ScriptRuntimeSemanticsSpec
            {
                Messages =
                {
                    new ScriptMessageSemanticsSpec
                    {
                        Kind = ScriptMessageKind.Command,
                    },
                },
            }));
        var adapter = new ScriptingServiceImplementationAdapter(snapshotPort);

        var artifact = await adapter.PrepareRevisionAsync(new PrepareServiceRevisionRequest
        {
            Spec = new ServiceRevisionSpec
            {
                Identity = GAgentServiceTestKit.CreateIdentity(),
                RevisionId = "service-r4",
                ImplementationKind = ServiceImplementationKind.Scripting,
                ScriptingSpec = new ScriptingServiceRevisionSpec
                {
                    ScriptId = "script-4",
                    Revision = "r4",
                    DefinitionActorId = "script-definition-4",
                },
            },
        });

        artifact.Endpoints.Should().ContainSingle();
        artifact.Endpoints[0].EndpointId.Should().BeEmpty();
        artifact.Endpoints[0].RequestTypeUrl.Should().BeEmpty();
        artifact.Endpoints[0].Description.Should().Be("Scripting command endpoint for .");
    }

    [Fact]
    public async Task WorkflowAdapter_ShouldInferWorkflowNameAndPreserveExactCapabilityEvidence_WhenNotProvided()
    {
        var dependencies = new WorkflowAuthorizationDependencies
        {
            OwnerLlmRouteRequired = true,
            ServiceGrantPolicy = WorkflowServiceGrantPolicy.Required,
        };
        ExternalWorkflowCapabilityRef[] admittedCapabilities =
        [
            new()
            {
                HostConnector = new HostConnectorCapabilityRef
                {
                    ConnectorCapabilityRef = "connector-calendar-alpha",
                    OperationId = "create_event",
                    ContractDigest = "connector-digest-alpha",
                },
            },
            new()
            {
                NyxIdUserService = new NyxIdUserServiceCapabilityRef
                {
                    UserServiceId = "us-home-alpha",
                    ServiceSlugSnapshot = "home-assistant",
                    EndpointId = "read_states",
                    HttpMethod = "GET",
                    PathTemplate = "/api/states",
                    ContractDigest = "nyxid-digest-alpha",
                    ExecutionPolicy = ReadOnlyNyxIdPolicy(ExternalCapabilityExecutionMode.Interactive),
                },
            },
            new()
            {
                NyxIdUserService = new NyxIdUserServiceCapabilityRef
                {
                    UserServiceId = "us-home-beta",
                    ServiceSlugSnapshot = "home-assistant",
                    EndpointId = "read_states",
                    HttpMethod = "GET",
                    PathTemplate = "/api/states",
                    ContractDigest = "nyxid-digest-beta",
                    ExecutionPolicy = ReadOnlyNyxIdPolicy(ExternalCapabilityExecutionMode.Interactive),
                },
            },
        ];
        var workflowPort = new RecordingWorkflowRunActorPort
        {
            ParseResult = WorkflowYamlParseResult.Success("inferred-workflow", dependencies),
        };
        var admissionPlan = WorkflowCapabilityAdmissionPlanIntegrity.Create(
            "name: inferred-workflow",
            inlineWorkflowYamls: null,
            ExternalCapabilityExecutionMode.Interactive,
            admittedCapabilities.Select(static (capability, index) => new WorkflowCapabilityInvocationAdmission
            {
                CallSiteId = $"inferred-workflow/step-{index}",
                Capability = capability,
            }),
            []);
        var adapter = new WorkflowServiceImplementationAdapter(
            workflowPort,
            new RecordingWorkflowCapabilityAdmissionService
            {
                Result = admissionPlan,
            });

        var artifact = await adapter.PrepareRevisionAsync(new PrepareServiceRevisionRequest
        {
            Spec = new ServiceRevisionSpec
            {
                Identity = GAgentServiceTestKit.CreateIdentity(),
                RevisionId = "r1",
                ImplementationKind = ServiceImplementationKind.Workflow,
                WorkflowSpec = new WorkflowServiceRevisionSpec
                {
                    WorkflowYaml = "name: inferred-workflow",
                    ExpectedExecutionMode = ExternalCapabilityExecutionMode.Interactive,
                },
            },
        });

        artifact.ImplementationKind.Should().Be(ServiceImplementationKind.Workflow);
        artifact.Endpoints.Should().ContainSingle(x => x.Kind == ServiceEndpointKind.Chat);
        artifact.DeploymentPlan.WorkflowPlan.WorkflowName.Should().Be("inferred-workflow");
        artifact.DeploymentPlan.WorkflowPlan.AuthorizationEvidence.OwnerLlmRouteRequired.Should().BeTrue();
        artifact.DeploymentPlan.WorkflowPlan.AuthorizationEvidence.ExternalCapabilities.Should()
            .BeEquivalentTo(admittedCapabilities);
        artifact.DeploymentPlan.WorkflowPlan.AuthorizationEvidence.ExternalCapabilities.Should()
            .OnlyContain(capability => admittedCapabilities.All(source => !ReferenceEquals(source, capability)));
        artifact.DeploymentPlan.WorkflowPlan.AuthorizationEvidence.ServiceGrantRequirement.Should()
            .Be(Aevatar.GAgentService.Abstractions.Schedules.Authorization.AuthorizationGrantRequirement.Required);
        workflowPort.ParseCalls.Should().ContainSingle("name: inferred-workflow");
    }

    [Fact]
    public async Task WorkflowAdapter_ShouldUseAdmittedBundleCapabilitiesForAuthorizationEvidence()
    {
        const string workflowYaml = "name: root-workflow";
        var dependencies = new WorkflowAuthorizationDependencies
        {
            ServiceGrantPolicy = WorkflowServiceGrantPolicy.NotRequiredNoExternalService,
        };
        var admittedCapability = new ExternalWorkflowCapabilityRef
        {
            NyxIdUserService = new NyxIdUserServiceCapabilityRef
            {
                UserServiceId = "us-home-alpha",
                ServiceSlugSnapshot = "home-assistant",
                EndpointId = "read_states",
                HttpMethod = "GET",
                PathTemplate = "/api/states",
                ContractDigest = "nyxid-digest-alpha",
                ExecutionPolicy = ReadOnlyNyxIdPolicy(
                    ExternalCapabilityExecutionMode.Interactive,
                    ExternalCapabilityExecutionMode.Durable),
            },
        };
        var admissionPlan = WorkflowCapabilityAdmissionPlanIntegrity.Create(
            workflowYaml,
            new Dictionary<string, string> { ["child"] = "name: child" },
            ExternalCapabilityExecutionMode.Durable,
            [new WorkflowCapabilityInvocationAdmission
            {
                CallSiteId = "root-workflow/read-states",
                Capability = admittedCapability,
            }],
            []);
        var workflowPort = new RecordingWorkflowRunActorPort
        {
            ParseResult = WorkflowYamlParseResult.Success("root-workflow", dependencies),
        };
        var adapter = new WorkflowServiceImplementationAdapter(
            workflowPort,
            new RecordingWorkflowCapabilityAdmissionService
            {
                Result = admissionPlan,
            });

        var artifact = await adapter.PrepareRevisionAsync(new PrepareServiceRevisionRequest
        {
            Spec = new ServiceRevisionSpec
            {
                Identity = GAgentServiceTestKit.CreateIdentity(),
                RevisionId = "r-bundle",
                ImplementationKind = ServiceImplementationKind.Workflow,
                WorkflowSpec = new WorkflowServiceRevisionSpec
                {
                    WorkflowYaml = workflowYaml,
                    InlineWorkflowYamls = { ["child"] = "name: child" },
                    CapabilityAdmissionPlan = admissionPlan,
                    ExpectedExecutionMode = ExternalCapabilityExecutionMode.Durable,
                },
            },
        });

        artifact.DeploymentPlan.WorkflowPlan.AuthorizationEvidence.ExternalCapabilities.Should()
            .ContainSingle()
            .Which.Should().BeEquivalentTo(admittedCapability);
        artifact.DeploymentPlan.WorkflowPlan.AuthorizationEvidence.ServiceGrantRequirement.Should()
            .Be(Aevatar.GAgentService.Abstractions.Schedules.Authorization.AuthorizationGrantRequirement.Required);
    }

    [Fact]
    public async Task WorkflowAdapter_ShouldRejectInvalidWorkflowYaml()
    {
        var adapter = new WorkflowServiceImplementationAdapter(
            new RecordingWorkflowRunActorPort
            {
                ParseResult = WorkflowYamlParseResult.Invalid("invalid yaml"),
            },
            new RecordingWorkflowCapabilityAdmissionService());

        var act = () => adapter.PrepareRevisionAsync(new PrepareServiceRevisionRequest
        {
            Spec = new ServiceRevisionSpec
            {
                Identity = GAgentServiceTestKit.CreateIdentity(),
                RevisionId = "r1",
                ImplementationKind = ServiceImplementationKind.Workflow,
                WorkflowSpec = new WorkflowServiceRevisionSpec
                {
                    WorkflowYaml = "invalid",
                },
            },
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("invalid yaml");
    }

    [Fact]
    public async Task WorkflowAdapter_ShouldUseProvidedWorkflowNameAndStillParseAndValidate()
    {
        var workflowPort = new RecordingWorkflowRunActorPort
        {
            ParseResult = CreateSuccessfulWorkflowParse("provided-workflow"),
        };
        var admission = new RecordingWorkflowCapabilityAdmissionService();
        var adapter = new WorkflowServiceImplementationAdapter(workflowPort, admission);
        var capabilityAdmissionPlan = WorkflowCapabilityAdmissionPlanIntegrity.Create(
            "name: ignored",
            new Dictionary<string, string> { ["child.yaml"] = "name: child" },
            ExternalCapabilityExecutionMode.Interactive,
            [],
            []);

        var artifact = await adapter.PrepareRevisionAsync(new PrepareServiceRevisionRequest
        {
            Spec = new ServiceRevisionSpec
            {
                Identity = GAgentServiceTestKit.CreateIdentity(),
                RevisionId = "r1",
                ImplementationKind = ServiceImplementationKind.Workflow,
                WorkflowSpec = new WorkflowServiceRevisionSpec
                {
                    WorkflowName = "provided-workflow",
                    WorkflowYaml = "name: ignored",
                    DefinitionActorId = "workflow-definition-1",
                    InlineWorkflowYamls = { ["child.yaml"] = "name: child" },
                    CapabilityAdmissionPlan = capabilityAdmissionPlan,
                    ExpectedExecutionMode = ExternalCapabilityExecutionMode.Interactive,
                },
            },
        });

        artifact.DeploymentPlan.WorkflowPlan.WorkflowName.Should().Be("provided-workflow");
        artifact.DeploymentPlan.WorkflowPlan.InlineWorkflowYamls.Should().ContainKey("child.yaml");
        artifact.DeploymentPlan.WorkflowPlan.CapabilityAdmissionPlan.AdmissionDigest.Should()
            .Be(capabilityAdmissionPlan.AdmissionDigest);
        admission.PersistedRequest.Should().NotBeNull();
        admission.PersistedRequest!.Plan.AdmissionDigest.Should().Be(capabilityAdmissionPlan.AdmissionDigest);
        admission.PersistedRequest.ExpectedExecutionMode.Should()
            .Be(ExternalCapabilityExecutionMode.Interactive);
        workflowPort.ParseCalls.Should().ContainSingle("name: ignored");
    }

    [Fact]
    public async Task WorkflowAdapter_WithPersistedPlan_ShouldRevalidateWithoutInferringCallerFromServiceIdentity()
    {
        const string workflowYaml = "name: persisted-workflow";
        var capability = new ExternalWorkflowCapabilityRef
        {
            NyxIdUserService = new NyxIdUserServiceCapabilityRef
            {
                UserServiceId = "us-home-alpha",
                ServiceSlugSnapshot = "home-assistant",
                EndpointId = "read_states",
                HttpMethod = "GET",
                PathTemplate = "/api/states",
                ContractDigest = "nyxid-digest-alpha",
                ExecutionPolicy = ReadOnlyNyxIdPolicy(
                    ExternalCapabilityExecutionMode.Interactive,
                    ExternalCapabilityExecutionMode.Durable),
            },
        };
        var dependencies = new WorkflowAuthorizationDependencies
        {
            ServiceGrantPolicy = WorkflowServiceGrantPolicy.Required,
        };
        dependencies.ExternalInvocations.Add(new ExternalToolInvocationSpec
        {
            CallSiteId = "persisted-workflow/read-states",
            ToolName = "nyxid_proxy",
            Selector = new ExternalWorkflowCapabilitySelector
            {
                NyxIdOperation = new NyxIdOperationSelector
                {
                    UserServiceId = "us-home-alpha",
                    EndpointId = "read_states",
                },
            },
        });
        var persistedPlan = WorkflowCapabilityAdmissionPlanIntegrity.Create(
            workflowYaml,
            inlineWorkflowYamls: null,
            ExternalCapabilityExecutionMode.Durable,
            [new WorkflowCapabilityInvocationAdmission
            {
                CallSiteId = "persisted-workflow/read-states",
                Capability = capability,
            }],
            [],
            new ExternalCapabilityAuthorizationOwner
            {
                Authority = WorkflowCapabilityAdmissionPlanIntegrity.NyxIdAuthority,
                OwnerKind = ExternalCapabilityAuthorizationOwnerKind.Personal,
                OwnerSubject = "caller-alpha",
            });
        var workflowPort = new RecordingWorkflowRunActorPort
        {
            ParseResult = WorkflowYamlParseResult.Success("persisted-workflow", dependencies),
        };
        var admission = new RecordingWorkflowCapabilityAdmissionService();
        var adapter = new WorkflowServiceImplementationAdapter(workflowPort, admission);

        var artifact = await adapter.PrepareRevisionAsync(new PrepareServiceRevisionRequest
        {
            Spec = new ServiceRevisionSpec
            {
                Identity = new ServiceIdentity
                {
                    TenantId = "tenant-alpha",
                    AppId = "app-beta",
                    Namespace = "ns-delta",
                    ServiceId = "svc-gamma",
                },
                RevisionId = "r-persisted",
                ImplementationKind = ServiceImplementationKind.Workflow,
                WorkflowSpec = new WorkflowServiceRevisionSpec
                {
                    WorkflowId = "wf-alpha",
                    WorkflowYaml = workflowYaml,
                    CapabilityAdmissionPlan = persistedPlan,
                    ExpectedExecutionMode = ExternalCapabilityExecutionMode.Durable,
                },
            },
        });

        admission.LiveRequest.Should().BeNull();
        admission.PersistedRequest.Should().NotBeNull();
        var persistedRequest = admission.PersistedRequest!;
        typeof(PersistedWorkflowCapabilityAdmissionRequest)
            .GetProperty(nameof(WorkflowExternalCapabilityAdmissionRequest.Access))
            .Should().BeNull();
        persistedRequest.ExpectedExecutionMode.Should().Be(ExternalCapabilityExecutionMode.Durable);
        persistedRequest.WorkflowId.Should().Be("wf-alpha");
        persistedRequest.RevisionId.Should().Be("r-persisted");
        persistedRequest.Plan.DurableAuthorizationOwner.OwnerSubject.Should().Be("caller-alpha");
        persistedRequest.Plan.DurableAuthorizationOwner.OwnerSubject.Should().NotBe("app-beta");
        persistedRequest.Plan.DurableAuthorizationOwner.OwnerSubject.Should().NotBe("svc-gamma");
        artifact.DeploymentPlan.WorkflowPlan.CapabilityAdmissionPlan.DurableAuthorizationOwner.OwnerSubject
            .Should().Be("caller-alpha");
    }

    [Fact]
    public async Task WorkflowAdapter_WithPersistedPlan_ShouldRequireExpectedExecutionMode()
    {
        const string workflowYaml = "name: persisted-workflow";
        var persistedPlan = WorkflowCapabilityAdmissionPlanIntegrity.Create(
            workflowYaml,
            inlineWorkflowYamls: null,
            ExternalCapabilityExecutionMode.Interactive,
            [],
            []);
        var adapter = new WorkflowServiceImplementationAdapter(
            new RecordingWorkflowRunActorPort
            {
                ParseResult = CreateSuccessfulWorkflowParse("persisted-workflow"),
            },
            new RecordingWorkflowCapabilityAdmissionService());

        var act = () => adapter.PrepareRevisionAsync(new PrepareServiceRevisionRequest
        {
            Spec = new ServiceRevisionSpec
            {
                Identity = GAgentServiceTestKit.CreateIdentity(),
                RevisionId = "r-persisted",
                ImplementationKind = ServiceImplementationKind.Workflow,
                WorkflowSpec = new WorkflowServiceRevisionSpec
                {
                    WorkflowYaml = workflowYaml,
                    CapabilityAdmissionPlan = persistedPlan,
                },
            },
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*execution mode is required*");
    }

    [Fact]
    public async Task WorkflowAdapter_ShouldPreserveTypedV2RebindBeforeParsingLegacyYaml()
    {
        var readiness = new ExternalCapabilityReadiness
        {
            ExecutionMode = ExternalCapabilityExecutionMode.Interactive,
            Status = ExternalCapabilityReadinessStatus.AdmissionRebindRequired,
            Blockers =
            {
                new ExternalCapabilityBlocker
                {
                    Status = ExternalCapabilityReadinessStatus.AdmissionRebindRequired,
                    Code = WorkflowCapabilityAdmissionPlanIntegrity.RebindRequiredCode,
                    SafeMessage = "The persisted workflow capability admission plan must be rebound.",
                },
            },
        };
        var workflowPort = new RecordingWorkflowRunActorPort
        {
            ParseResult = WorkflowYamlParseResult.Invalid("legacy authoring is invalid"),
        };
        var admission = new RecordingWorkflowCapabilityAdmissionService
        {
            Failure = new WorkflowExternalCapabilityAdmissionException(readiness),
        };
        var adapter = new WorkflowServiceImplementationAdapter(workflowPort, admission);

        var action = () => adapter.PrepareRevisionAsync(new PrepareServiceRevisionRequest
        {
            Spec = new ServiceRevisionSpec
            {
                Identity = GAgentServiceTestKit.CreateIdentity(),
                RevisionId = "r-legacy",
                ImplementationKind = ServiceImplementationKind.Workflow,
                WorkflowSpec = new WorkflowServiceRevisionSpec
                {
                    WorkflowYaml = LegacyWorkflowYaml,
                    CapabilityAdmissionPlan = new WorkflowCapabilityAdmissionPlan
                    {
                        SchemaVersion = WorkflowCapabilityAdmissionPlanIntegrity.LegacySchemaVersion,
                        ExecutionMode = ExternalCapabilityExecutionMode.Interactive,
                    },
                    ExpectedExecutionMode = ExternalCapabilityExecutionMode.Interactive,
                },
            },
        });

        var exception = await action.Should().ThrowAsync<WorkflowExternalCapabilityAdmissionException>();
        exception.Which.Readiness.Blockers.Should().ContainSingle().Which.Code.Should()
            .Be(WorkflowCapabilityAdmissionPlanIntegrity.RebindRequiredCode);
        admission.PersistedRequest.Should().NotBeNull();
        workflowPort.ParseCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task WorkflowAdapter_WithoutLegacyPlan_ShouldReadmitSameYamlAsV3Artifact()
    {
        var v3Plan = WorkflowCapabilityAdmissionPlanIntegrity.Create(
            LegacyWorkflowYaml,
            inlineWorkflowYamls: null,
            ExternalCapabilityExecutionMode.Interactive,
            invocationAdmissions: [],
            sourceStamps: []);
        var workflowPort = new RecordingWorkflowRunActorPort
        {
            ParseResult = CreateSuccessfulWorkflowParse("rebound-workflow"),
        };
        var admission = new RecordingWorkflowCapabilityAdmissionService
        {
            Result = v3Plan,
        };
        var adapter = new WorkflowServiceImplementationAdapter(workflowPort, admission);

        var reboundArtifact = await adapter.PrepareRevisionAsync(new PrepareServiceRevisionRequest
        {
            Spec = new ServiceRevisionSpec
            {
                Identity = GAgentServiceTestKit.CreateIdentity(),
                RevisionId = "revision-v3",
                ImplementationKind = ServiceImplementationKind.Workflow,
                WorkflowSpec = new WorkflowServiceRevisionSpec
                {
                    WorkflowYaml = LegacyWorkflowYaml,
                    ExpectedExecutionMode = ExternalCapabilityExecutionMode.Interactive,
                },
            },
        });

        admission.PersistedRequest.Should().BeNull();
        admission.LiveRequest.Should().NotBeNull();
        admission.LiveRequest!.WorkflowYaml.Should().Be(LegacyWorkflowYaml);
        reboundArtifact.RevisionId.Should().Be("revision-v3");
        reboundArtifact.DeploymentPlan.WorkflowPlan.WorkflowYaml.Should().Be(LegacyWorkflowYaml);
        reboundArtifact.DeploymentPlan.WorkflowPlan.CapabilityAdmissionPlan.SchemaVersion.Should()
            .Be(WorkflowCapabilityAdmissionPlanIntegrity.SchemaVersion);
        reboundArtifact.DeploymentPlan.WorkflowPlan.CapabilityAdmissionPlan.AdmissionDigest.Should()
            .Be(v3Plan.AdmissionDigest);
        workflowPort.ParseCalls.Should().ContainSingle().Which.Should().Be(LegacyWorkflowYaml);
    }

    [Fact]
    public async Task WorkflowAdapter_ShouldRejectInvalidWorkflowYaml_WhenWorkflowNameProvided()
    {
        var adapter = new WorkflowServiceImplementationAdapter(
            new RecordingWorkflowRunActorPort
            {
                ParseResult = WorkflowYamlParseResult.Invalid("invalid yaml"),
            },
            new RecordingWorkflowCapabilityAdmissionService());

        var act = () => adapter.PrepareRevisionAsync(new PrepareServiceRevisionRequest
        {
            Spec = new ServiceRevisionSpec
            {
                Identity = GAgentServiceTestKit.CreateIdentity(),
                RevisionId = "r1",
                ImplementationKind = ServiceImplementationKind.Workflow,
                WorkflowSpec = new WorkflowServiceRevisionSpec
                {
                    WorkflowName = "provided-workflow",
                    WorkflowYaml = "invalid",
                },
            },
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("invalid yaml");
    }

    [Fact]
    public async Task WorkflowAdapter_ShouldRejectWorkflowNameMismatch()
    {
        var adapter = new WorkflowServiceImplementationAdapter(
            new RecordingWorkflowRunActorPort
            {
                ParseResult = CreateSuccessfulWorkflowParse("yaml-workflow"),
            },
            new RecordingWorkflowCapabilityAdmissionService());

        var act = () => adapter.PrepareRevisionAsync(new PrepareServiceRevisionRequest
        {
            Spec = new ServiceRevisionSpec
            {
                Identity = GAgentServiceTestKit.CreateIdentity(),
                RevisionId = "r1",
                ImplementationKind = ServiceImplementationKind.Workflow,
                WorkflowSpec = new WorkflowServiceRevisionSpec
                {
                    WorkflowName = "provided-workflow",
                    WorkflowYaml = "name: yaml-workflow",
                },
            },
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("workflow_name must match workflow_yaml name.");
    }

    [Fact]
    public async Task WorkflowAdapter_ShouldRejectMissingWorkflowYaml()
    {
        var adapter = new WorkflowServiceImplementationAdapter(
            new RecordingWorkflowRunActorPort(),
            new RecordingWorkflowCapabilityAdmissionService());

        var act = () => adapter.PrepareRevisionAsync(new PrepareServiceRevisionRequest
        {
            Spec = new ServiceRevisionSpec
            {
                Identity = GAgentServiceTestKit.CreateIdentity(),
                RevisionId = "r1",
                ImplementationKind = ServiceImplementationKind.Workflow,
                WorkflowSpec = new WorkflowServiceRevisionSpec
                {
                    WorkflowName = "wf",
                    WorkflowYaml = string.Empty,
                },
            },
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("workflow_yaml is required.");
    }

    [Fact]
    public async Task WorkflowAdapter_ShouldValidateConstructorAndMissingWorkflowSpec()
    {
        Action nullPort = () => new WorkflowServiceImplementationAdapter(
            null!,
            new RecordingWorkflowCapabilityAdmissionService());
        Action nullAdmission = () => new WorkflowServiceImplementationAdapter(
            new RecordingWorkflowRunActorPort(),
            null!);
        var adapter = new WorkflowServiceImplementationAdapter(
            new RecordingWorkflowRunActorPort(),
            new RecordingWorkflowCapabilityAdmissionService());
        var act = () => adapter.PrepareRevisionAsync(new PrepareServiceRevisionRequest
        {
            Spec = new ServiceRevisionSpec
            {
                Identity = GAgentServiceTestKit.CreateIdentity(),
                RevisionId = "r1",
                ImplementationKind = ServiceImplementationKind.Workflow,
            },
        });

        nullPort.Should().Throw<ArgumentNullException>();
        nullAdmission.Should().Throw<ArgumentNullException>();
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("workflow implementation_spec is required.");
    }

    private sealed class RecordingScriptDefinitionSnapshotPort : IScriptDefinitionSnapshotPort
    {
        private readonly ScriptDefinitionSnapshot _snapshot;

        public RecordingScriptDefinitionSnapshotPort(ScriptDefinitionSnapshot snapshot)
        {
            _snapshot = snapshot;
        }

        public List<(string definitionActorId, string revision)> Calls { get; } = [];

        public Task<ScriptDefinitionSnapshot> GetRequiredAsync(
            string definitionActorId,
            string requestedRevision,
            CancellationToken ct)
        {
            Calls.Add((definitionActorId, requestedRevision));
            return Task.FromResult(_snapshot);
        }
    }

    private sealed class RecordingWorkflowCapabilityAdmissionService : IWorkflowExternalCapabilityAdmissionService
    {
        public WorkflowExternalCapabilityAdmissionRequest? LiveRequest { get; private set; }

        public PersistedWorkflowCapabilityAdmissionRequest? PersistedRequest { get; private set; }

        public WorkflowCapabilityAdmissionPlan? Result { get; init; }

        public Exception? Failure { get; init; }

        public Task<WorkflowCapabilityAdmissionPlan> AdmitAsync(
            WorkflowExternalCapabilityAdmissionRequest request,
            CancellationToken cancellationToken = default)
        {
            LiveRequest = request;
            if (Failure is not null)
                return Task.FromException<WorkflowCapabilityAdmissionPlan>(Failure);
            return Task.FromResult(Result?.Clone()
                ?? WorkflowCapabilityAdmissionPlanIntegrity.Create(
                    request.WorkflowYaml,
                    request.InlineWorkflowYamls,
                    request.ExecutionMode,
                    [],
                    []));
        }

        public Task<WorkflowCapabilityAdmissionPlan> RevalidatePersistedAsync(
            PersistedWorkflowCapabilityAdmissionRequest request,
            CancellationToken cancellationToken = default)
        {
            PersistedRequest = request;
            if (Failure is not null)
                return Task.FromException<WorkflowCapabilityAdmissionPlan>(Failure);
            return Task.FromResult(Result?.Clone() ?? request.Plan.Clone());
        }
    }

    private static WorkflowYamlParseResult CreateSuccessfulWorkflowParse(string workflowName) =>
        WorkflowYamlParseResult.Success(
            workflowName,
            new WorkflowAuthorizationDependencies
            {
                ServiceGrantPolicy = WorkflowServiceGrantPolicy.NotRequiredNoExternalService,
            });

    private static NyxIdOperationExecutionPolicy ReadOnlyNyxIdPolicy(
        params ExternalCapabilityExecutionMode[] executionModes)
    {
        var policy = new NyxIdOperationExecutionPolicy
        {
            Risk = NyxIdOperationRisk.ReadOnly,
            Approval = NyxIdOperationApproval.None,
            EnforcementOwner = NyxIdOperationEnforcementOwner.Aevatar,
        };
        policy.AllowedExecutionModes.Add(executionModes);
        return policy;
    }

    private sealed class RecordingWorkflowRunActorPort : IWorkflowDefinitionProvisioningPort, IWorkflowRunProvisioningPort, IWorkflowDefinitionParser
    {
        public WorkflowYamlParseResult ParseResult { get; init; } = CreateSuccessfulWorkflowParse("workflow");

        public List<string> ParseCalls { get; } = [];

        public Task<WorkflowDefinitionProvisioningReceipt> EnsureDefinitionAsync(WorkflowDefinitionBinding definition, string? preferredActorId = null, CancellationToken ct = default) =>
            Task.FromResult(new WorkflowDefinitionProvisioningReceipt(preferredActorId ?? definition.DefinitionActorId, CreatedNow: true));

        public Task<WorkflowRunCreationReceipt> CreateRunAsync(WorkflowDefinitionBinding definition, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DestroyAsync(string actorId, CancellationToken ct = default) => Task.CompletedTask;

        public Task MarkStoppedAsync(string actorId, string runId, string reason, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task BindWorkflowDefinitionAsync(
            string actorId,
            string workflowYaml,
            string workflowName,
            IReadOnlyDictionary<string, string>? inlineWorkflowYamls,
            string? scopeId,
            string? sourceKind,
            WorkflowCapabilityAdmissionPlan? capabilityAdmissionPlan,
            string? workflowId,
            string? revisionId,
            ExternalCapabilityExecutionMode expectedExecutionMode,
            CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<WorkflowYamlParseResult> ParseWorkflowYamlAsync(string workflowYaml, CancellationToken ct = default)
        {
            ParseCalls.Add(workflowYaml);
            return Task.FromResult(ParseResult);
        }

        public async Task<WorkflowInlineYamlBundleParseResult> ParseInlineWorkflowBundleAsync(
            IReadOnlyList<WorkflowChatInlineYamlDocument> inlineWorkflowDocuments,
            CancellationToken ct = default)
        {
            if (inlineWorkflowDocuments.Count == 0)
                return WorkflowInlineYamlBundleParseResult.Invalid("workflowYamls is required.");

            var entryYaml = inlineWorkflowDocuments[0].Yaml;
            var parseResult = await ParseWorkflowYamlAsync(entryYaml, ct);
            return parseResult.Succeeded
                ? WorkflowInlineYamlBundleParseResult.Success(
                    parseResult.WorkflowName,
                    entryYaml,
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        [parseResult.WorkflowName] = entryYaml,
                    })
                : WorkflowInlineYamlBundleParseResult.Invalid(parseResult.Error, parseResult.ExternalCapabilityReadiness);
        }
    }

    private sealed class RecordingActor : IActor
    {
        public RecordingActor(string id)
        {
            Id = id;
        }

        public string Id { get; }

        public IAgent Agent { get; } = new TestStaticServiceAgent();

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private static IAgentKindRegistry CreateStaticAgentKindRegistry()
    {
        var builder = new AgentKindRegistryBuilder();
        builder.Register<TestStaticServiceAgent>();
        return new AgentKindRegistry(builder.Build());
    }
}

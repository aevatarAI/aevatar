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
        dependencies.ExternalCapabilities.Add(new ExternalWorkflowCapabilityRef
        {
            HostConnector = new HostConnectorCapabilityRef
            {
                ConnectorCapabilityRef = "connector-calendar-alpha",
                OperationId = "create_event",
                ContractDigest = "connector-digest-alpha",
            },
        });
        dependencies.ExternalCapabilities.Add(new ExternalWorkflowCapabilityRef
        {
            NyxIdUserService = new NyxIdUserServiceCapabilityRef
            {
                UserServiceId = "us-home-alpha",
                ServiceSlugSnapshot = "home-assistant",
                OperationId = "read_states",
                HttpMethod = "GET",
                PathTemplate = "/api/states",
                ContractDigest = "nyxid-digest-alpha",
            },
        });
        dependencies.ExternalCapabilities.Add(new ExternalWorkflowCapabilityRef
        {
            NyxIdUserService = new NyxIdUserServiceCapabilityRef
            {
                UserServiceId = "us-home-beta",
                ServiceSlugSnapshot = "home-assistant",
                OperationId = "read_states",
                HttpMethod = "GET",
                PathTemplate = "/api/states",
                ContractDigest = "nyxid-digest-beta",
            },
        });
        var workflowPort = new RecordingWorkflowRunActorPort
        {
            ParseResult = WorkflowYamlParseResult.Success("inferred-workflow", dependencies),
        };
        var admissionPlan = WorkflowCapabilityAdmissionPlanIntegrity.Create(
            "name: inferred-workflow",
            inlineWorkflowYamls: null,
            ExternalCapabilityExecutionMode.Interactive,
            dependencies.ExternalCapabilities,
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
                },
            },
        });

        artifact.ImplementationKind.Should().Be(ServiceImplementationKind.Workflow);
        artifact.Endpoints.Should().ContainSingle(x => x.Kind == ServiceEndpointKind.Chat);
        artifact.DeploymentPlan.WorkflowPlan.WorkflowName.Should().Be("inferred-workflow");
        artifact.DeploymentPlan.WorkflowPlan.AuthorizationEvidence.OwnerLlmRouteRequired.Should().BeTrue();
        artifact.DeploymentPlan.WorkflowPlan.AuthorizationEvidence.ExternalCapabilities.Should()
            .Equal(dependencies.ExternalCapabilities);
        artifact.DeploymentPlan.WorkflowPlan.AuthorizationEvidence.ExternalCapabilities.Should()
            .OnlyContain(capability => dependencies.ExternalCapabilities.All(source => !ReferenceEquals(source, capability)));
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
                OperationId = "read_states",
                HttpMethod = "GET",
                PathTemplate = "/api/states",
                ContractDigest = "nyxid-digest-alpha",
            },
        };
        var admissionPlan = WorkflowCapabilityAdmissionPlanIntegrity.Create(
            workflowYaml,
            new Dictionary<string, string> { ["child"] = "name: child" },
            ExternalCapabilityExecutionMode.Durable,
            [admittedCapability],
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
                },
            },
        });

        artifact.DeploymentPlan.WorkflowPlan.WorkflowName.Should().Be("provided-workflow");
        artifact.DeploymentPlan.WorkflowPlan.InlineWorkflowYamls.Should().ContainKey("child.yaml");
        artifact.DeploymentPlan.WorkflowPlan.CapabilityAdmissionPlan.AdmissionDigest.Should()
            .Be(capabilityAdmissionPlan.AdmissionDigest);
        admission.Request.Should().NotBeNull();
        admission.Request!.ExistingPlan!.AdmissionDigest.Should().Be(capabilityAdmissionPlan.AdmissionDigest);
        workflowPort.ParseCalls.Should().ContainSingle("name: ignored");
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
        public WorkflowExternalCapabilityAdmissionRequest? Request { get; private set; }

        public WorkflowCapabilityAdmissionPlan? Result { get; init; }

        public Task<WorkflowCapabilityAdmissionPlan> AdmitAsync(
            WorkflowExternalCapabilityAdmissionRequest request,
            CancellationToken cancellationToken = default)
        {
            Request = request;
            return Task.FromResult(Result?.Clone()
                ?? request.ExistingPlan?.Clone()
                ?? WorkflowCapabilityAdmissionPlanIntegrity.Create(
                    request.WorkflowYaml,
                    request.InlineWorkflowYamls,
                    request.ExecutionMode,
                    [],
                    []));
        }
    }

    private static WorkflowYamlParseResult CreateSuccessfulWorkflowParse(string workflowName) =>
        WorkflowYamlParseResult.Success(
            workflowName,
            new WorkflowAuthorizationDependencies
            {
                ServiceGrantPolicy = WorkflowServiceGrantPolicy.NotRequiredNoExternalService,
            });

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
            IReadOnlyDictionary<string, string>? inlineWorkflowYamls = null,
            string? scopeId = null,
            string? sourceKind = null,
            WorkflowCapabilityAdmissionPlan? capabilityAdmissionPlan = null,
            CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<WorkflowYamlParseResult> ParseWorkflowYamlAsync(string workflowYaml, CancellationToken ct = default)
        {
            ParseCalls.Add(workflowYaml);
            return Task.FromResult(ParseResult);
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

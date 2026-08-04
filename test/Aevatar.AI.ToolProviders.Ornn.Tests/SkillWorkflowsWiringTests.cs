using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.Skills;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using FluentAssertions;
using System.Text.Json;

namespace Aevatar.AI.ToolProviders.Ornn.Tests;

public sealed class SkillWorkflowsWiringTests
{
    [Fact]
    public async Task OrnnRemoteSkillFetcher_ExtractsWorkflowsAndStripsThemFromAssociatedFiles()
    {
        var handler = OrnnTestHttpMessageHandler.ReturningJson("""
            {
              "data": {
                "name": "Translator",
                "description": "Translates",
                "files": {
                  "SKILL.md": "# Translator\nUse this.",
                  "workflows/translate.yaml": "name: translate_flow\ndescription: Translate text\nwhen_to_use: When user asks to translate\nsteps:\n  - id: do\n    type: llm_call\n",
                  "scripts/run.sh": "echo hi"
                }
              }
            }
            """);
        var client = CreateClient(handler);
        var fetcher = new OrnnRemoteSkillFetcher(client);

        var skill = await fetcher.FetchSkillAsync("token", "Translator");

        skill.Should().NotBeNull();
        skill!.Workflows.Should().ContainSingle();
        skill.Workflows![0].WorkflowId.Should().Be("translate");
        skill.Workflows[0].WorkflowYamls.Should().ContainSingle()
            .Which.Should().Contain("name: translate_flow");

        // Workflow file must not also appear in AssociatedFiles.
        skill.AssociatedFiles.Should().NotBeNull();
        skill.AssociatedFiles.Should().NotContainKey("workflows/translate.yaml");
        skill.AssociatedFiles.Should().ContainKey("scripts/run.sh");
    }

    [Fact]
    public async Task OrnnRemoteSkillFetcher_FallsBackToAssetsYamlWhenNoWorkflowsDir()
    {
        var handler = OrnnTestHttpMessageHandler.ReturningJson("""
            {
              "data": {
                "name": "Translator",
                "files": {
                  "SKILL.md": "# Translator",
                  "assets/translate.yaml": "name: translate_asset\nsteps:\n  - id: do\n",
                  "assets/prompt.txt": "raw"
                }
              }
            }
            """);
        var client = CreateClient(handler);
        var fetcher = new OrnnRemoteSkillFetcher(client);

        var skill = await fetcher.FetchSkillAsync("token", "Translator");

        skill!.Workflows.Should().ContainSingle(w => w.WorkflowId == "translate_asset");
        skill.Workflows![0].WorkflowYamls.Should().ContainSingle()
            .Which.Should().Contain("name: translate_asset");
        skill.AssociatedFiles.Should().NotContainKey("assets/translate.yaml");
        skill.AssociatedFiles.Should().ContainKey("assets/prompt.txt");
    }

    [Fact]
    public async Task OrnnRemoteSkillFetcher_ReadsSkillMarkdownCaseInsensitively()
    {
        var handler = OrnnTestHttpMessageHandler.ReturningJson("""
            {
              "data": {
                "name": "Translator",
                "files": {
                  "skill.md": "---\nname: translator\n---\nUse lowercase file name.",
                  "workflows/translate.yaml": "name: translate_flow\nsteps:\n  - id: do\n",
                  "docs/readme.md": "reference"
                }
              }
            }
            """);
        var client = CreateClient(handler);
        var fetcher = new OrnnRemoteSkillFetcher(client);

        var skill = await fetcher.FetchSkillAsync("token", "Translator");

        skill.Should().NotBeNull();
        skill!.Name.Should().Be("translator");
        skill.Instructions.Should().Be("Use lowercase file name.");
        skill.AssociatedFiles.Should().NotContainKey("skill.md");
        skill.AssociatedFiles.Should().ContainKey("docs/readme.md");
    }

    [Fact]
    public async Task UseSkillTool_RendersUnMountedWorkflowsAsTemplates()
    {
        var catalog = new LocalSkillCatalog();
        catalog.Register(new SkillDefinition
        {
            Name = "translator",
            Description = "Translates text",
            Instructions = "Follow these steps.",
            Source = SkillSource.Local,
            Workflows =
            [
                new SkillWorkflowDescriptor
                {
                    WorkflowId = "translate_flow",
                    WorkflowYamls =
                    [
                        "name: translate_flow\nsteps:\n  - id: do\n    type: llm_call\n",
                    ],
                },
            ],
        });

        var tool = new UseSkillTool(catalog);
        var output = await tool.ExecuteAsync("""{"skill":"translator","mount_workflows":false}""");
        var text = ExtractText(output);

        text.Should().Contain("## Workflow Templates");
        text.Should().Contain("templates/import sources");
        text.Should().Contain("Scope Workflow command path");
        text.Should().Contain("not runnable scope workflows by themselves");
        text.Should().Contain("workflow_yamls");
        text.Should().Contain("translate_flow");
        text.Should().Contain("```json");
        text.Should().Contain("type: llm_call");
    }

    [Fact]
    public async Task UseSkillTool_OmitsWorkflowsSectionWhenSkillHasNoWorkflows()
    {
        var catalog = new LocalSkillCatalog();
        catalog.Register(new SkillDefinition
        {
            Name = "plain",
            Description = "no workflows",
            Instructions = "body",
            Source = SkillSource.Local,
        });

        var tool = new UseSkillTool(catalog);
        var output = await tool.ExecuteAsync("""{"skill":"plain"}""");
        var text = ExtractText(output);

        text.Should().NotContain("## Workflow Templates");
        text.Should().NotContain("aevatar_start_workflow");
    }

    [Fact]
    public async Task UseSkillTool_RendersScriptHandoffAfterWorkflowAndBeforeAssociatedFiles()
    {
        var catalog = new LocalSkillCatalog();
        catalog.Register(new SkillDefinition
        {
            Name = "scripted",
            Description = "Runs script",
            Instructions = "Follow these steps.",
            Source = SkillSource.Local,
            Workflows =
            [
                new SkillWorkflowDescriptor
                {
                    WorkflowId = "prepare",
                    WorkflowYamls = ["name: prepare\nsteps: []\n"],
                },
            ],
            Scripts =
            [
                new SkillScriptDescriptor
                {
                    ScriptId = "scripted-main",
                    SourceFiles = new Dictionary<string, string>
                    {
                        ["scripts/Main.cs"] = "public sealed class MainBehavior {}",
                    },
                    ProtoFiles = new Dictionary<string, string>
                    {
                        ["scripts/contract.proto"] = "syntax = \"proto3\";",
                    },
                    EntryBehaviorTypeName = "MainBehavior",
                },
            ],
            AssociatedFiles = new Dictionary<string, string>
            {
                ["docs/readme.md"] = "reference",
            },
        });

        var tool = new UseSkillTool(catalog);
        var output = await tool.ExecuteAsync("""{"skill":"scripted"}""");
        var text = ExtractText(output);

        text.Should().Contain("## script_compile/script_execute Handoff");
        text.Should().Contain("Call `script_compile`");
        text.Should().Contain("### script_compile");
        text.Should().Contain("\"script_id\": \"scripted-main\"");
        text.Should().Contain("\"source_files\"");
        text.Should().Contain("\"scripts/Main.cs\"");
        text.Should().Contain("\"proto_files\"");
        text.Should().Contain("\"scripts/contract.proto\"");
        text.Should().Contain("\"entry_behavior_type_name\": \"MainBehavior\"");
        text.Should().Contain("### script_execute");
        text.Should().Contain("\"input\": \"Use the current user request and skill arguments.\"");

        text.IndexOf("## Workflow Templates", StringComparison.Ordinal)
            .Should().BeLessThan(text.IndexOf("## script_compile/script_execute Handoff", StringComparison.Ordinal));
        text.IndexOf("## script_compile/script_execute Handoff", StringComparison.Ordinal)
            .Should().BeLessThan(text.IndexOf("## Associated Files", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UseSkillTool_OmitsScriptHandoffWhenSkillHasNoScripts()
    {
        var catalog = new LocalSkillCatalog();
        catalog.Register(new SkillDefinition
        {
            Name = "plain",
            Description = "no scripts",
            Instructions = "body",
            Source = SkillSource.Local,
        });

        var tool = new UseSkillTool(catalog);
        var output = await tool.ExecuteAsync("""{"skill":"plain"}""");
        var text = ExtractText(output);

        text.Should().NotContain("## script_compile/script_execute Handoff");
        text.Should().NotContain("script_compile");
    }

    [Fact]
    public async Task UseSkillTool_MountWorkflowsFalse_DoesNotRequireApprovalAndDoesNotCallCommandPort()
    {
        var catalog = CreateCatalogWithWorkflowSkill();
        var commandPort = new RecordingScopeWorkflowCommandPort();
        var tool = new UseSkillTool(catalog, scopeWorkflowCommandPort: commandPort);

        tool.ApprovalMode.Should().Be(ToolApprovalMode.NeverRequire);
        tool.RequiresApproval("""{"skill":"translator"}""").Should().BeFalse();
        tool.RequiresApproval("""{"skill":"translator","mount_workflows":false}""").Should().BeFalse();

        var output = await tool.ExecuteAsync("""{"skill":"translator","mount_workflows":false}""");

        output.Should().Contain("## Workflow Templates");
        output.Should().Contain("templates/import sources");
        output.Should().NotContain("## Mounted Workflows");
        commandPort.Requests.Should().BeEmpty();
    }

    [Fact]
    public void UseSkillTool_MountWorkflowsTrue_DoesNotRequireApproval()
    {
        var tool = new UseSkillTool(new LocalSkillCatalog());

        tool.ApprovalMode.Should().Be(ToolApprovalMode.NeverRequire);
        tool.RequiresApproval("""{"skill":"translator","mount_workflows":true}""").Should().BeFalse();
        ((IAgentTool)tool).GetCallSafety("""{"skill":"translator","mount_workflows":true}""").Should().Be(
            new AgentToolCallSafety(
                RequiresApproval: false,
                IsReadOnly: false,
                IsDestructive: false));
    }

    [Fact]
    public async Task UseSkillTool_MountWorkflowsTrueWithoutScope_ReturnsErrorAndDoesNotUpsert()
    {
        var previous = AgentToolRequestContext.Current;
        try
        {
            AgentToolRequestContext.Current = null;
            var commandPort = new RecordingScopeWorkflowCommandPort();
            var tool = new UseSkillTool(CreateCatalogWithWorkflowSkill(), scopeWorkflowCommandPort: commandPort);

            var output = await tool.ExecuteAsync("""{"skill":"translator","mount_workflows":true}""");

            output.Should().Contain("## Mounted Workflows");
            output.Should().Contain("\"success\": false");
            output.Should().Contain("scope_id not available in request context");
            commandPort.Requests.Should().BeEmpty();
        }
        finally
        {
            AgentToolRequestContext.Current = previous;
        }
    }

    [Fact]
    public async Task UseSkillTool_MountWorkflowsTrueWithoutCommandPort_ReturnsErrorAfterLoadingSkill()
    {
        using var _ = BeginContextScope(scopeId: "scope-1");
        var tool = new UseSkillTool(CreateCatalogWithWorkflowSkill());

        var output = await tool.ExecuteAsync("""{"skill":"translator","mount_workflows":true}""");

        output.Should().Contain("# translator");
        output.Should().Contain("## Mounted Workflows");
        output.Should().Contain("scope workflow command port is not available in this host");
    }

    [Fact]
    public async Task UseSkillTool_MountWorkflowsTrueWithoutCallerAuthority_ReturnsMissingIdentityAndDoesNotUpsert()
    {
        using var _ = BeginContextScope(scopeId: "scope-alpha", ownerSubject: "owner-alpha");
        var commandPort = new RecordingScopeWorkflowCommandPort();
        var tool = new UseSkillTool(CreateCatalogWithWorkflowSkill(), scopeWorkflowCommandPort: commandPort);

        var output = await tool.ExecuteAsync("""{"skill":"translator","mount_workflows":true}""");

        output.Should().Contain("\"status\": \"missing_identity\"");
        commandPort.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task UseSkillTool_MountWorkflowsTrueWithNoWorkflows_ReturnsErrorAndDoesNotUpsert()
    {
        using var _ = BeginContextScope(scopeId: "scope-1");
        var catalog = new LocalSkillCatalog();
        catalog.Register(new SkillDefinition
        {
            Name = "plain",
            Description = "no workflows",
            Instructions = "body",
            Source = SkillSource.Local,
        });
        var commandPort = new RecordingScopeWorkflowCommandPort();
        var tool = new UseSkillTool(catalog, scopeWorkflowCommandPort: commandPort);

        var output = await tool.ExecuteAsync("""{"skill":"plain","mount_workflows":true}""");

        output.Should().Contain("skill has no workflow descriptors to mount");
        commandPort.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task UseSkillTool_MountWorkflowsTrueWithBlankWorkflowId_ReturnsErrorAndDoesNotUpsert()
    {
        using var _ = BeginContextScope(
            scopeId: "scope-alpha",
            ownerSubject: "owner-alpha",
            nyxIdUserId: "nyx-user-alpha");
        var catalog = new LocalSkillCatalog();
        catalog.Register(new SkillDefinition
        {
            Name = "translator",
            Description = "Translates text",
            Instructions = "Follow these steps.",
            Source = SkillSource.Local,
            Workflows =
            [
                new SkillWorkflowDescriptor
                {
                    WorkflowId = " ",
                    WorkflowYamls = ["name: translate_flow\nsteps: []\n"],
                },
            ],
        });
        var commandPort = new RecordingScopeWorkflowCommandPort();
        var tool = new UseSkillTool(catalog, scopeWorkflowCommandPort: commandPort);

        var output = await tool.ExecuteAsync("""{"skill":"translator","mount_workflows":true}""");

        output.Should().Contain("## Mounted Workflows");
        output.Should().Contain("\"success\": false");
        output.Should().Contain("skill workflow descriptor has no workflow_id");
        commandPort.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task UseSkillTool_MountWorkflowsTrueWithBlankWorkflowYamls_ReturnsErrorAndDoesNotUpsert()
    {
        using var _ = BeginContextScope(
            scopeId: "scope-alpha",
            ownerSubject: "owner-alpha",
            nyxIdUserId: "nyx-user-alpha");
        var catalog = new LocalSkillCatalog();
        catalog.Register(new SkillDefinition
        {
            Name = "translator",
            Description = "Translates text",
            Instructions = "Follow these steps.",
            Source = SkillSource.Local,
            Workflows =
            [
                new SkillWorkflowDescriptor
                {
                    WorkflowId = "translate_flow",
                    WorkflowYamls = ["", "   ", "\t"],
                },
            ],
        });
        var commandPort = new RecordingScopeWorkflowCommandPort();
        var tool = new UseSkillTool(catalog, scopeWorkflowCommandPort: commandPort);

        var output = await tool.ExecuteAsync("""{"skill":"translator","mount_workflows":true}""");

        output.Should().Contain("## Mounted Workflows");
        output.Should().Contain("\"success\": false");
        output.Should().Contain(@"skill workflow \u0027translate_flow\u0027 has no workflow YAML");
        commandPort.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task UseSkillTool_MountWorkflowsTrue_UpsertsAllSkillWorkflowsThroughScopeWorkflowCommandPort()
    {
        using var _ = BeginContextScope(
            scopeId: "scope-alpha",
            ownerSubject: "owner-alpha",
            nyxIdUserId: "nyx-user-alpha");
        var commandPort = new RecordingScopeWorkflowCommandPort();
        var tool = new UseSkillTool(CreateCatalogWithWorkflowSkill(), scopeWorkflowCommandPort: commandPort);

        var output = await tool.ExecuteAsync("""{"skill":"translator","mount_workflows":true}""");

        commandPort.Requests.Should().HaveCount(2);
        commandPort.Requests[0].ScopeId.Should().Be("scope-alpha");
        commandPort.Requests[0].WorkflowId.Should().Be("translate_flow");
        commandPort.Requests[0].WorkflowYaml.Should().Be("name: translate_flow\nsteps: []\n");
        commandPort.Requests[0].WorkflowName.Should().BeNull();
        commandPort.Requests[0].DisplayName.Should().Be("translate_flow");
        commandPort.Requests[0].InlineWorkflowYamls.Should().ContainSingle()
            .Which.Should().Be(new KeyValuePair<string, string>("workflow_1", "name: helper_flow\nsteps: []\n"));
        commandPort.Requests[0].CapabilityAdmission.Should().NotBeNull();
        commandPort.Requests[0].CapabilityAdmission!.CallerId.Should().Be("nyx-user-alpha");
        commandPort.Requests[1].WorkflowId.Should().Be("qa_flow");
        commandPort.Requests[1].CapabilityAdmission.Should().NotBeNull();
        commandPort.Requests[1].CapabilityAdmission!.CallerId.Should().Be("nyx-user-alpha");
        output.Should().Contain("## Mounted Workflows");
        output.Should().Contain("Workflow mount/import commands were accepted for dispatch through the Scope Workflow command path; read models may still be propagating before the workflows are page-visible or runnable.");
        output.Should().Contain("\"accepted\": true");
        output.Should().Contain("\"acceptance_stage\": \"accepted\"");
        output.Should().Contain("\"propagation_stage\": \"readmodel_propagating\"");
        output.Should().Contain("\"read_model_url\": \"/api/scopes/scope-alpha/workflows/translate_flow\"");
        output.Should().Contain("\"command_handles\"");
        output.Should().NotContain("already visible");
        output.Should().NotContain("strongly consistent");
    }

    [Theory]
    [InlineData(AgentToolNyxIdCredentialKind.ProxyDelegation)]
    [InlineData(AgentToolNyxIdCredentialKind.Unspecified)]
    public async Task UseSkillTool_MountWorkflowsTrue_WhenCredentialIsNotSourceReadable_ShouldOmitCallerCredential(
        AgentToolNyxIdCredentialKind credentialKind)
    {
        using var _ = BeginContextScope(
            scopeId: "scope-alpha",
            token: "caller-token",
            ownerSubject: "owner-alpha",
            nyxIdUserId: "nyx-user-alpha",
            credentialKind: credentialKind);
        var commandPort = new RecordingScopeWorkflowCommandPort();
        var tool = new UseSkillTool(CreateCatalogWithWorkflowSkill(), scopeWorkflowCommandPort: commandPort);

        await tool.ExecuteAsync("""{"skill":"translator","mount_workflows":true}""");

        commandPort.Requests.Should().HaveCount(2);
        commandPort.Requests.Should().OnlyContain(request =>
            request.CapabilityAdmission != null &&
            request.CapabilityAdmission.NyxIdCallerCredential == null);
    }

    [Fact]
    public async Task UseSkillTool_MountWorkflowsTrue_PreservesDescriptorWorkflowIdAndLetsCommandPortParseYamlName()
    {
        using var _ = BeginContextScope(
            scopeId: "scope-alpha",
            ownerSubject: "owner-alpha",
            nyxIdUserId: "nyx-user-alpha");
        var catalog = new LocalSkillCatalog();
        catalog.Register(new SkillDefinition
        {
            Name = "packaged-skill",
            Description = "Uses packaged workflow identity",
            Instructions = "Follow these steps.",
            Source = SkillSource.Local,
            Workflows =
            [
                new SkillWorkflowDescriptor
                {
                    WorkflowId = "packaged-entry",
                    WorkflowYamls = ["name: parsed_entry\nsteps: []\n"],
                },
            ],
        });
        var commandPort = new RecordingScopeWorkflowCommandPort();
        var tool = new UseSkillTool(catalog, scopeWorkflowCommandPort: commandPort);

        await tool.ExecuteAsync("""{"skill":"packaged-skill","mount_workflows":true}""");

        commandPort.Requests.Should().ContainSingle();
        commandPort.Requests[0].WorkflowId.Should().Be("packaged-entry");
        commandPort.Requests[0].WorkflowYaml.Should().Be("name: parsed_entry\nsteps: []\n");
        commandPort.Requests[0].WorkflowName.Should().BeNull();
        commandPort.Requests[0].DisplayName.Should().Be("packaged-entry");
    }

    [Fact]
    public async Task UseSkillTool_MountWorkflowsTrueForRemoteSkill_UsesCurrentTokenAndMountsFetchedDescriptors()
    {
        using var _ = BeginContextScope(
            scopeId: "scope-alpha",
            token: "current-token",
            ownerSubject: "owner-alpha",
            nyxIdUserId: "nyx-user-alpha");
        var fetcher = new RecordingRemoteSkillFetcher(new SkillDefinition
        {
            Name = "remote-translator",
            Description = "remote skill",
            Instructions = "remote body",
            Source = SkillSource.Remote,
            Workflows =
            [
                new SkillWorkflowDescriptor
                {
                    WorkflowId = "remote_flow",
                    WorkflowYamls = ["name: remote_flow\nsteps: []\n"],
                },
            ],
        });
        var commandPort = new RecordingScopeWorkflowCommandPort();
        var tool = new UseSkillTool(new LocalSkillCatalog(), fetcher, commandPort);

        var output = await tool.ExecuteAsync("""{"skill":"remote-translator","mount_workflows":true}""");

        fetcher.Requests.Should().ContainSingle().Which.Should().Be(("current-token", "remote-translator"));
        commandPort.Requests.Should().ContainSingle().Which.WorkflowId.Should().Be("remote_flow");
        output.Should().Contain("# remote-translator");
        output.Should().Contain("\"workflow_id\": \"remote_flow\"");
    }

    [Fact]
    public void SkillDiscovery_PicksUpWorkflowsFromSkillDirectory()
    {
        using var tempDir = new TempDirectory();
        var skillDir = Path.Combine(tempDir.Path, "translator");
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), """
            ---
            name: translator
            description: Translates
            ---
            Body
            """);
        var workflowsDir = Path.Combine(skillDir, "workflows");
        Directory.CreateDirectory(workflowsDir);
        File.WriteAllText(Path.Combine(workflowsDir, "translate.yaml"), """
            name: translate_flow
            description: Run translation
            when_to_use: User asks to translate
            steps:
              - id: do
                type: llm_call
            """);

        var skills = new SkillDiscovery().ScanDirectory(tempDir.Path);

        skills.Should().ContainSingle();
        skills[0].Name.Should().Be("translator");
        skills[0].Workflows.Should().ContainSingle();
        skills[0].Workflows![0].WorkflowId.Should().Be("translate_flow");
        skills[0].Workflows![0].WorkflowYamls.Should().ContainSingle()
            .Which.Should().Contain("name: translate_flow");
    }

    private static OrnnSkillClient CreateClient(OrnnTestHttpMessageHandler handler)
    {
        var nyxClient = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example" },
            new HttpClient(handler));
        var options = new OrnnOptions { NyxIdSlug = "ornn" };
        return new OrnnSkillClient(options, nyxClient);
    }

    private static LocalSkillCatalog CreateCatalogWithWorkflowSkill()
    {
        var catalog = new LocalSkillCatalog();
        catalog.Register(new SkillDefinition
        {
            Name = "translator",
            Description = "Translates text",
            Instructions = "Follow these steps.",
            Source = SkillSource.Local,
            Workflows =
            [
                new SkillWorkflowDescriptor
                {
                    WorkflowId = "translate_flow",
                    WorkflowYamls =
                    [
                        "name: translate_flow\nsteps: []\n",
                        "name: helper_flow\nsteps: []\n",
                    ],
                },
                new SkillWorkflowDescriptor
                {
                    WorkflowId = "qa_flow",
                    WorkflowYamls = ["name: qa_flow\nsteps: []\n"],
                },
            ],
        });
        return catalog;
    }

    private static AgentToolRequestContextScope BeginContextScope(
        string? scopeId = null,
        string? token = null,
        string? ownerSubject = null,
        string? nyxIdUserId = null,
        AgentToolNyxIdCredentialKind credentialKind = AgentToolNyxIdCredentialKind.SourceReadableUserBearer)
    {
        var metadata = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(scopeId))
            metadata[LLMRequestMetadataKeys.ScopeId] = scopeId;
        if (!string.IsNullOrWhiteSpace(token))
            metadata[LLMRequestMetadataKeys.NyxIdAccessToken] = token;
        if (!string.IsNullOrWhiteSpace(ownerSubject))
            metadata[LLMRequestMetadataKeys.OwnerSubject] = ownerSubject;
        var context = global::TestAgentToolContexts.FromMetadata(metadata);
        context = context with
        {
            Credentials = context.Credentials with
            {
                NyxIdCredentialKind = credentialKind,
            },
        };
        if (!string.IsNullOrWhiteSpace(nyxIdUserId))
        {
            context = context with
            {
                NyxIdAuthority = new AgentToolNyxIdAuthorityContext(
                    "nyxid",
                    "tenant-alpha",
                    nyxIdUserId.Trim()),
            };
        }

        return new AgentToolRequestContextScope(context);
    }

    private sealed class AgentToolRequestContextScope : IDisposable
    {
        private readonly AgentToolExecutionContext? _previous;

        public AgentToolRequestContextScope(AgentToolExecutionContext context)
        {
            _previous = AgentToolRequestContext.Current;
            AgentToolRequestContext.Current = context;
        }

        public void Dispose() => AgentToolRequestContext.Current = _previous;
    }

    private sealed class RecordingRemoteSkillFetcher(SkillDefinition skill) : IRemoteSkillFetcher
    {
        public List<(string AccessToken, string NameOrId)> Requests { get; } = [];

        public Task<SkillDefinition?> FetchSkillAsync(string accessToken, string nameOrId, CancellationToken ct = default)
        {
            Requests.Add((accessToken, nameOrId));
            return Task.FromResult<SkillDefinition?>(skill);
        }
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
                request.ScopeId,
                request.WorkflowId,
                $"service-key-{request.WorkflowId}",
                $"revision-{request.WorkflowId}",
                "definition-prefix",
                $"actor-{request.WorkflowId}",
                $"deployment-{request.WorkflowId}",
                DateTimeOffset.UnixEpoch,
                [new ScopeWorkflowCommandAcceptedHandle("create_revision", "target-actor", "cmd-1", "corr-1")],
                $"/api/scopes/{request.ScopeId}/workflows/{request.WorkflowId}"));
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; }

        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // best effort
            }
        }
    }

    private static string ExtractText(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("text").GetString() ?? string.Empty;
    }
}

using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Workflow.Abstractions.Workflows;
using Aevatar.Workflow.Application.Workflows;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Core.Execution;
using Aevatar.Workflow.Core.Primitives;
using Aevatar.Workflow.Infrastructure.Workflows;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Workflow.Host.Api.Tests;

public class WorkflowDefinitionCatalogTests
{
    [Fact]
    public void Register_And_GetYaml()
    {
        var registry = new WorkflowDefinitionCatalog();
        registry.Register("test", "name: test\nsteps: []");

        registry.GetYaml("test").Should().Contain("name: test");
        registry.GetYaml("TEST").Should().NotBeNull(); // Case-insensitive lookup.
        registry.GetYaml("nonexistent").Should().BeNull();
        registry.GetDefinition("test")!.DefinitionActorId.Should().Be(WorkflowDefinitionActorId.Format("test"));
    }

    [Fact]
    public void GetNames_ReturnsAll()
    {
        var registry = new WorkflowDefinitionCatalog();
        registry.Register("alpha", "a");
        registry.Register("beta", "b");

        registry.GetNames().Should().HaveCount(2);
    }

    [Fact]
    public void FileLoader_NonExistentDirectory_ReturnsZero()
    {
        var registry = new WorkflowDefinitionCatalog();
        var loader = new WorkflowDefinitionFileLoader();
        var loaded = loader.LoadInto(
            registry,
            ["/nonexistent/path/12345"],
            NullLogger.Instance);

        loaded.Should().Be(0);
    }

    [Fact]
    public void FileLoader_LoadsYamlFiles()
    {
        // Create a temporary directory.
        var tmpDir = Path.Combine(Path.GetTempPath(), $"wf_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);

        try
        {
            File.WriteAllText(Path.Combine(tmpDir, "review.yaml"), "name: review");
            File.WriteAllText(Path.Combine(tmpDir, "chat.yml"), "name: chat");
            File.WriteAllText(Path.Combine(tmpDir, "readme.txt"), "not a workflow");

            var registry = new WorkflowDefinitionCatalog();
            var loader = new WorkflowDefinitionFileLoader();
            var count = loader.LoadInto(registry, [tmpDir], NullLogger.Instance);

            count.Should().Be(2);
            registry.GetYaml("review").Should().Contain("review");
            registry.GetYaml("chat").Should().Contain("chat");
        }
        finally
        {
            Directory.Delete(tmpDir, true);
        }
    }

    [Fact]
    public void FileLoader_DuplicateWorkflowName_ShouldThrow()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), $"wf_test_dup_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);

        try
        {
            File.WriteAllText(Path.Combine(tmpDir, "review.yaml"), "name: review");
            File.WriteAllText(Path.Combine(tmpDir, "review.yml"), "name: review_2");

            var registry = new WorkflowDefinitionCatalog();
            var loader = new WorkflowDefinitionFileLoader();

            Action act = () => loader.LoadInto(registry, [tmpDir], NullLogger.Instance);
            act.Should().Throw<InvalidOperationException>()
                .WithMessage("*Duplicate workflow definition name*");
        }
        finally
        {
            Directory.Delete(tmpDir, true);
        }
    }

    [Fact]
    public void FileLoader_DuplicateWorkflowName_WithOverridePolicy_ShouldUseFileVersion()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), $"wf_test_dup_override_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);

        try
        {
            File.WriteAllText(Path.Combine(tmpDir, "direct.yaml"), "name: direct\nsteps:\n  - id: from_file\n");

            var registry = new WorkflowDefinitionCatalog();
            registry.Register("direct", "name: direct\nsteps:\n  - id: built_in\n");
            var loader = new WorkflowDefinitionFileLoader();

            var count = loader.LoadInto(
                registry,
                [tmpDir],
                NullLogger.Instance,
                WorkflowDefinitionDuplicatePolicy.Override);

            count.Should().Be(1);
            registry.GetYaml("direct").Should().Contain("from_file");
            registry.GetYaml("direct").Should().NotContain("built_in");
        }
        finally
        {
            Directory.Delete(tmpDir, true);
        }
    }

    [Fact]
    public void FileLoader_DuplicateDirectoryEntries_ShouldLoadOnlyOnce()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), $"wf_test_dup_dir_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);

        try
        {
            File.WriteAllText(Path.Combine(tmpDir, "brainstorm.yaml"), "name: brainstorm");
            var equivalentPath = Path.Combine(tmpDir, ".");

            var registry = new WorkflowDefinitionCatalog();
            var loader = new WorkflowDefinitionFileLoader();

            var count = loader.LoadInto(registry, [tmpDir, equivalentPath], NullLogger.Instance);

            count.Should().Be(1);
            registry.GetNames().Should().ContainSingle().Which.Should().Be("brainstorm");
        }
        finally
        {
            Directory.Delete(tmpDir, true);
        }
    }

    [Fact]
    public void BuiltInAutoYaml_ShouldContainFewShotWorkflowExamples()
    {
        WorkflowDefinitionCatalog.BuiltInAutoYaml.Should().Contain("Example valid workflow YAML (simple deterministic flow):");
        WorkflowDefinitionCatalog.BuiltInAutoYaml.Should().Contain("name: normalize_text");
        WorkflowDefinitionCatalog.BuiltInAutoYaml.Should().Contain("name: review_summary");
        WorkflowDefinitionCatalog.BuiltInAutoYaml.Should().Contain("Do not invent extra fields.");
        WorkflowDefinitionCatalog.BuiltInAutoYaml.Should().Contain("86400000ms");
        WorkflowDefinitionCatalog.BuiltInAutoYaml.Should().Contain("do not invent await_job or async_job");
        WorkflowDefinitionCatalog.BuiltInAutoYaml.Should().NotContain("5400000ms");
    }

    [Fact]
    public void BuiltInAutoYaml_ShouldUseRouteTokenSwitchWithoutSubstringHeuristics()
    {
        WorkflowDefinitionCatalog.BuiltInAutoYaml.Should().Contain("id: classify_route");
        WorkflowDefinitionCatalog.BuiltInAutoYaml.Should().Contain("id: route_intent");
        WorkflowDefinitionCatalog.BuiltInAutoYaml.Should().Contain("type: switch");
        WorkflowDefinitionCatalog.BuiltInAutoYaml.Should().Contain("\"workflow\": prepare_workflow_request");
        WorkflowDefinitionCatalog.BuiltInAutoYaml.Should().Contain("\"direct\": prepare_direct_response");
        WorkflowDefinitionCatalog.BuiltInAutoYaml.Should().NotContain("condition: \"```y\"");
    }

    [Fact]
    public void CreateBuiltInAutoYaml_ShouldKeepRouteTokenFlow()
    {
        var autoYaml = WorkflowDefinitionCatalog.CreateBuiltInAutoYaml();

        autoYaml.Should().Contain("id: classify_route");
        autoYaml.Should().Contain("next: route_intent");
        autoYaml.Should().NotContain("condition: \"```y\"");
    }

    [Fact]
    public void CreateBuiltInAutoYaml_ShouldUseSharedAuthorableRootSchema()
    {
        var autoYaml = WorkflowDefinitionCatalog.CreateBuiltInAutoYaml();

        autoYaml.Should().Contain(
            $"Authorable top-level keys: {WorkflowYamlRootSchema.FormatAuthorableRootFields()}");
        autoYaml.Should().Contain(
            $"Do NOT use top-level keys from other workflow dialects, including {WorkflowYamlRootSchema.FormatUnsupportedDialectRootFields()}");
        autoYaml.Should().NotContain("Top-level keys: name, description, roles, steps");
    }

    [Fact]
    public void BuiltInStudioYaml_ShouldParseAsMemberProvisionStudioRoleWithToolAllowlist()
    {
        var workflow = new WorkflowParser().Parse(WorkflowDefinitionCatalog.BuiltInStudioYaml);

        workflow.Name.Should().Be("studio");
        var role = workflow.Roles.Should().ContainSingle().Subject;

        // The role carries a workflow-first, Observatory-delivered system prompt that steers to the
        // member/provision path — NOT a bare "helpful assistant", NOT a Lark/skill-publishing playbook,
        // and NOT the loose-definition author-then-run-by-name path that hangs.
        role.SystemPrompt.Should().Contain("Studio agent");
        role.SystemPrompt.Should().Contain("aevatar_create_team");
        role.SystemPrompt.Should().Contain("aevatar_list_teams");
        role.SystemPrompt.Should().Contain("aevatar_create_member");
        role.SystemPrompt.Should().Contain("aevatar_create_member_workflow_draft");
        role.SystemPrompt.Should().Contain("aevatar_bind_member_workflow");
        role.SystemPrompt.Should().Contain("aevatar_schedule_member_workflow");
        role.SystemPrompt.Should().Contain("aevatar_provision_workflow_schedule");
        role.SystemPrompt.Should().Contain(
            "Without a template qualifier, workflow means a Team-owned workflow member in the current workspace");
        role.SystemPrompt.Should().Contain("follow `next_page_token`");
        role.SystemPrompt.Should().Contain("public templates, examples, or the template library");
        role.SystemPrompt.Should().Contain("`member_id`, `workflow_id`, and `published_service_id`");
        role.SystemPrompt.Should().Contain("NOT create a separate `wf-...` member");
        role.SystemPrompt.Should().Contain("Do not call `aevatar_provision_workflow_schedule` until a Team has been selected or created");
        role.SystemPrompt.Should().Contain("pass that confirmed `team_id`");
        role.SystemPrompt.Should().Contain("/workflow/observatory");
        role.SystemPrompt.Should().Contain("Do NOT");
        // Honesty: the receipt is Accepted (async), not a success claim.
        role.SystemPrompt.Should().Contain("Accepted");
        // Schema teaching: without it the model falls back to foreign workflow
        // dialects (GitHub-Actions-style version:/inputs:) that the strict parser
        // rejects. Pin the load-bearing pieces: the closed top-level key list,
        // the foreign-dialect counter-examples, and a runnable example.
        role.SystemPrompt.Should().Contain(
            $"Authorable top-level keys are EXACTLY: {WorkflowYamlRootSchema.FormatAuthorableRootFields()}");
        role.SystemPrompt.Should().Contain(
            $"no {WorkflowYamlRootSchema.FormatUnsupportedDialectRootFields()}");
        role.SystemPrompt.Should().Contain("name: daily_digest");
        // Retry semantics: same display_name converges on the same resources;
        // reusing it for a different automation replaces the previous one.
        role.SystemPrompt.Should().Contain("SAME `display_name`");
        role.SystemPrompt.Should().Contain("REPLACES");
        // The loose-definition tools that hang on by-name resolution are no longer steered to.
        role.SystemPrompt.Should().NotContain("workflow_create_def");
        role.SystemPrompt.Should().NotContain("aevatar_start_workflow");

        // The allowlist is the lever that keeps both the Lark scheduler and the hanging loose-definition
        // tools out of the studio surface, and brings the channel-free provision tool in.
        role.AgentToolScope.Should().NotBeNull();
        var allowed = role.AgentToolScope!.AllowedToolNames;
        allowed.Should().Contain("aevatar_list_teams");
        allowed.Should().Contain("aevatar_create_team");
        allowed.Should().Contain("aevatar_get_team");
        allowed.Should().Contain("aevatar_create_member");
        allowed.Should().Contain("aevatar_create_member_workflow_draft");
        allowed.Should().Contain("aevatar_list_members");
        allowed.Should().Contain("aevatar_get_member");
        allowed.Should().Contain("aevatar_list_schedules");
        allowed.Should().Contain("aevatar_get_schedule");
        allowed.Should().Contain("aevatar_list_workflows");
        allowed.Should().Contain("aevatar_list_workflow_templates");
        allowed.Should().Contain("aevatar_get_workflow_template");
        allowed.Should().NotContain("aevatar_get_workflow");
        allowed.Should().Contain("aevatar_bind_member_workflow");
        allowed.Should().Contain("aevatar_schedule_member_workflow");
        allowed.Should().Contain("aevatar_provision_workflow_schedule");
        allowed.Should().Contain("aevatar_observe_run");
        allowed.Should().Contain("aevatar_read_workflow_run_artifact");
        allowed.Should().Contain("web_search");
        allowed.Should().Contain("web_fetch");
        // The loose-definition path (file-only create + run-by-name) hangs 30s on an unprovisioned
        // definition actor — it must be absent from the studio surface.
        allowed.Should().NotContain("workflow_create_def");
        allowed.Should().NotContain("workflow_update_def");
        allowed.Should().NotContain("workflow_read_def");
        allowed.Should().NotContain("workflow_list_defs");
        allowed.Should().NotContain("aevatar_start_workflow");
        allowed.Should().NotContain("scheduled_agent_creator");
        // Studio is workflow-first: publishing a prose skill is not the deliverable.
        allowed.Should().NotContain("ornn_publish_skill");

        // The single llm_call step runs under the studio role.
        var step = workflow.Steps.Should().ContainSingle().Subject;
        step.Type.Should().Be("llm_call");
        step.TargetRole.Should().Be("studio");
    }

    [Fact]
    public void BuiltInStudioYaml_ShouldExposeNyxIdCapabilityToolsWithoutCollapsingServiceSemantics()
    {
        var workflow = new WorkflowParser().Parse(WorkflowDefinitionCatalog.BuiltInStudioYaml);
        var role = workflow.Roles.Should().ContainSingle().Subject;

        role.SystemPrompt.Should().Contain("NyxID is a separate caller-account capability domain");
        role.SystemPrompt.Should().Contain("The word \"service\" is ambiguous");
        role.SystemPrompt.Should().Contain("我的 nyxId service 有哪些");
        role.SystemPrompt.Should().Contain("call `nyxid_services` with `action: \"list\"`");
        role.SystemPrompt.Should().Contain("Do not treat unqualified \"services\" as NyxID services");
        role.SystemPrompt.Should().Contain("Studio published workflow service");
        role.SystemPrompt.Should().Contain("NyxID connected service");
        role.SystemPrompt.Should().Contain("Do NOT use `aevatar_list_workflows`");
        role.SystemPrompt.Should().Contain("The user does not need to say NyxID for an external capability request");
        role.SystemPrompt.Should().Contain("first look for a matching NyxID connected service");
        role.SystemPrompt.Should().Contain("use the admitted per-operation connected-service tool");
        role.SystemPrompt.Should().Contain("Do not call a provider-specific chat tool first");
        role.SystemPrompt.Should().Contain("`list_external_workflow_capabilities`");
        role.SystemPrompt.Should().Contain("copy its exact `selector`");
        role.SystemPrompt.Should().Contain("step-level `capability.nyxid_operation`");
        role.SystemPrompt.Should().Contain("`path_params`, `query`");
        role.SystemPrompt.Should().Contain("`headers`, `body`, and `response_mode`");
        role.SystemPrompt.Should().NotContain("exact static `service_id`, `slug`, `operation_id`, `method`, `path`, and `contract_digest`");
        role.SystemPrompt.Should().Contain("Do not generate or guess selector identities or server-owned proof fields");
        role.SystemPrompt.Should().NotContain("contract_digest");
        role.SystemPrompt.Should().Contain("Specialized provider or skill-discovery tools are not the default path");
        role.SystemPrompt.Should().Contain("Do not create a provider-specific prompt rule or runtime-tool mapping for one named service");
        role.SystemPrompt.Should().Contain("service-specific behavior must come from discovered connected-service/catalog/host connector/runtime tool schemas");
        role.SystemPrompt.Should().NotContain("Use NyxID tools only when the user explicitly mentions");
        role.SystemPrompt.Should().NotContain("You may use `ornn_search_skills` and `use_skill` to discover and load skills for genuinely");
        role.SystemPrompt.Should().NotContain("Use specialized provider tools only when the user explicitly asks for that provider capability");

        role.AgentToolScope.Should().NotBeNull();
        var allowed = role.AgentToolScope!.AllowedToolNames;
        allowed.Should().Contain("nyxid_status");
        allowed.Should().Contain("nyxid_account");
        allowed.Should().Contain("nyxid_catalog");
        allowed.Should().Contain("nyxid_llm_status");
        allowed.Should().Contain("nyxid_services");
        allowed.Should().NotContain("nyxid_proxy");
        role.AgentToolScope.ToolSetRefs.Should().Equal("nyxid.connected_services");
        allowed.Should().Contain("nyxid_require_service");
        allowed.Should().Contain("list_external_workflow_capabilities");
        allowed.Should().Contain("inspect_external_workflow_capability_readiness");

        allowed.Should().NotContain("nyxid_api_keys");
        allowed.Should().NotContain("nyxid_nodes");
        allowed.Should().NotContain("nyxid_approvals");
        allowed.Should().NotContain("nyxid_providers");
        allowed.Should().NotContain("nyxid_notifications");
        allowed.Should().NotContain("nyxid_mfa");
        allowed.Should().NotContain("nyxid_profile");
        allowed.Should().NotContain("nyxid_endpoints");
        allowed.Should().NotContain("nyxid_external_keys");
        allowed.Should().NotContain("nyxid_channel_bots");
        allowed.Should().NotContain("nyxid_orgs");
        allowed.Should().NotContain("nyxid_admin");
        allowed.Should().NotContain("ssh_exec");
        allowed.Should().NotContain("codex_exec");
        allowed.Should().NotContain("code_execute");
    }

    [Fact]
    public void BuiltInStudioYaml_ShouldSaveUnresolvedNyxIdWorkflowAsNonRunnableDraft()
    {
        var workflow = new WorkflowParser().Parse(WorkflowDefinitionCatalog.BuiltInStudioYaml);
        var prompt = workflow.Roles.Should().ContainSingle().Subject.SystemPrompt;

        var discover = prompt.IndexOf("call `list_external_workflow_capabilities`", StringComparison.Ordinal);
        var research = prompt.IndexOf("use `web_search`", StringComparison.Ordinal);
        var saveDraft = prompt.IndexOf("call `aevatar_create_member_workflow_draft`", StringComparison.Ordinal);

        discover.Should().BeGreaterThanOrEqualTo(0);
        research.Should().BeGreaterThan(discover);
        saveDraft.Should().BeGreaterThan(research);
        prompt.Should().Contain("official documentation");
        prompt.Should().Contain("infer the minimal authoring shape");
        prompt.Should().Contain("omit step-level `capability` when no exact selector exists");
        prompt.Should().Contain("`runnable=false`");
        prompt.Should().Contain("NYXID_OPERATION_SELECTION_REQUIRED");
        prompt.Should().Contain(
            "Do not call `aevatar_bind_member_workflow`, `aevatar_schedule_member_workflow`, or `aevatar_provision_workflow_schedule`");
        prompt.Should().Contain("Do not invent selector identities, operation proof, method, or path authority");
        prompt.Should().NotContain(
            "If no exact descriptor is available, report the typed readiness blocker instead of inventing");
    }

    [Fact]
    public void BuiltInStudioYaml_ShouldTeachGenericRuntimeToolCallSchemaForWorkflowAuthoring()
    {
        var workflow = new WorkflowParser().Parse(WorkflowDefinitionCatalog.BuiltInStudioYaml);
        var role = workflow.Roles.Should().ContainSingle().Subject;

        role.SystemPrompt.Should().Contain("For workflow runtime tool steps, use `type: tool_call`");
        role.SystemPrompt.Should().Contain("parameters.tool");
        role.SystemPrompt.Should().Contain("parameters.arguments");
        role.SystemPrompt.Should().Contain("Do not use `tool_name`");
        role.SystemPrompt.Should().Contain("`${steps.<step_id>.output}`");
        role.SystemPrompt.Should().Contain("When a workflow step needs an external service or available runtime tool");
        role.SystemPrompt.Should().Contain("use the exact registered runtime tool name");
        role.SystemPrompt.Should().Contain("build `parameters.arguments` from that tool's declared schema");
        role.SystemPrompt.Should().Contain("Do not add a provider-specific prompt rule for a single service");
        role.SystemPrompt.Should().NotContain("Ornn skill search example");
        role.SystemPrompt.Should().NotContain("When the user explicitly asks a workflow to query or search Ornn skills");
        role.SystemPrompt.Should().NotContain("tool: \"ornn_search_skills\"");
    }

    [Fact]
    public void Catalog_ShouldRegisterStudioWorkflowAlongsideDirect()
    {
        var registry = new WorkflowDefinitionCatalog();
        registry.Register("direct", WorkflowDefinitionCatalog.BuiltInDirectYaml);
        registry.Register("studio", WorkflowDefinitionCatalog.BuiltInStudioYaml);

        registry.GetYaml("studio").Should().Contain("name: studio");
        registry.GetDefinition("studio")!.DefinitionActorId
            .Should().Be(WorkflowDefinitionActorId.Format("studio"));
    }

    [Fact]
    public void FileLoader_ShouldLoadLarkApprovalWaitTemplates()
    {
        var registry = new WorkflowDefinitionCatalog();
        var loader = new WorkflowDefinitionFileLoader();

        var count = loader.LoadInto(
            registry,
            [Path.Combine(FindRepositoryRoot(), "workflows")],
            NullLogger.Instance);

        count.Should().BeGreaterThanOrEqualTo(2);
        registry.GetYaml("lark_approval_wait").Should().Contain("workflow_call");
        registry.GetYaml("lark_approval_wait").Should().Contain("lark_approval_wait_poll");
        registry.GetYaml("lark_approval_wait").Should().Contain("max_iterations: \"60\"");
        registry.GetYaml("lark_approval_wait").Should().Contain("on: \"${input}\"");
        registry.GetYaml("lark_approval_wait_poll").Should().Contain("lark_approvals_get");
        registry.GetYaml("lark_approval_wait_poll").Should().Contain("arguments: '{\"instance_code\":\"${json(input)}\"}'");
        registry.GetYaml("lark_approval_wait_poll").Should().Contain("duration_ms: \"5000\"");
        registry.GetYaml("lark_approval_wait_poll").Should().Contain("value: \"${steps.get_instance.json.instance_code}\"");
    }

    [Fact]
    public void FileLoader_ShouldLoadFirecrawlAsyncJobTemplates()
    {
        var registry = new WorkflowDefinitionCatalog();
        var loader = new WorkflowDefinitionFileLoader();

        var count = loader.LoadInto(
            registry,
            [Path.Combine(FindRepositoryRoot(), "workflows")],
            NullLogger.Instance);

        count.Should().BeGreaterThanOrEqualTo(4);
        registry.GetYaml("firecrawl_agent_async_submit").Should().Contain("firecrawl_crawl_submit");
        registry.GetYaml("firecrawl_agent_async_submit").Should().Contain("firecrawl_agent_async_poll");
        registry.GetYaml("firecrawl_agent_async_poll").Should().Contain("firecrawl_crawl_status");
        registry.GetYaml("firecrawl_agent_async_poll").Should().Contain("enabled: \"false\"");
    }

    [Fact]
    public void LarkApprovalWaitTemplates_ShouldUseExpectedWorkflowPrimitives()
    {
        var root = FindRepositoryRoot();
        var waitYaml = File.ReadAllText(Path.Combine(root, "workflows", "lark_approval_wait.yaml"));
        var pollYaml = File.ReadAllText(Path.Combine(root, "workflows", "lark_approval_wait_poll.yaml"));

        waitYaml.Should().Contain("type: while");
        waitYaml.Should().Contain("step: workflow_call");
        waitYaml.Should().Contain("condition: \"${and(not(eq(output, 'approved'))");
        waitYaml.Should().Contain("type: switch");
        waitYaml.Should().Contain("on: \"${input}\"");
        pollYaml.Should().Contain("type: tool_call");
        pollYaml.Should().Contain("tool: lark_approvals_get");
        pollYaml.Should().Contain("arguments: '{\"instance_code\":\"${json(input)}\"}'");
        pollYaml.Should().Contain("type: delay");
        pollYaml.Should().Contain("value: \"${steps.get_instance.json.instance_code}\"");
        waitYaml.Should().NotContain("nyxid_proxy");
        pollYaml.Should().NotContain("nyxid_proxy");
    }

    [Fact]
    public void BuiltInAutoYaml_ShouldSteerLongExternalJobsToSplitRunTemplates()
    {
        WorkflowDefinitionCatalog.BuiltInAutoYaml.Should().Contain("may wait up to 86400000ms (24h)");
        WorkflowDefinitionCatalog.BuiltInAutoYaml.Should().Contain("do not invent await_job or async_job");
        WorkflowDefinitionCatalog.BuiltInAutoYaml.Should().Contain("do not model same-run long polling");
        WorkflowDefinitionCatalog.BuiltInAutoYaml.Should().Contain("deterministic self_reschedule schedule owns polling");
        WorkflowDefinitionCatalog.BuiltInAutoYaml.Should().Contain("Do not put those facts in header.* or metadata.");
    }

    [Fact]
    public async Task WorkflowExecutionKernel_ShouldMirrorJsonControlFieldsForTemplateBranching()
    {
        var workflow = new WorkflowDefinition
        {
            Name = "wf",
            Roles = [],
            Steps =
            [
                new StepDefinition
                {
                    Id = "get_instance",
                    Type = "tool_call",
                },
                new StepDefinition
                {
                    Id = "route_status",
                    Type = "switch",
                    Parameters = new Dictionary<string, string>
                    {
                        ["on"] = "${steps.get_instance.json.status}",
                    },
                },
            ],
        };
        var ctx = new CapturingContext();
        var kernel = new WorkflowExecutionKernel(workflow, (IWorkflowExecutionStateHost)ctx.Agent);
        const string runId = "run-json-fields";

        await kernel.HandleAsync(Wrap(new StartWorkflowEvent
        {
            WorkflowName = "wf",
            RunId = runId,
            Input = "inst_1",
        }), ctx, CancellationToken.None);
        ctx.Published.Clear();

        await kernel.HandleAsync(Wrap(new StepCompletedEvent
        {
            StepId = "get_instance",
            RunId = runId,
            Success = true,
            Output = """{"success":true,"status":"approved","should_continue_waiting":false,"task_count":1}""",
        }), ctx, CancellationToken.None);

        var request = ctx.Published.Single(x => x.Event is StepRequestEvent).Event.Should().BeOfType<StepRequestEvent>().Subject;
        request.StepId.Should().Be("route_status");
        request.Parameters["on"].Should().Be("approved");

        var state = ((IWorkflowExecutionStateHost)ctx.Agent)
            .GetExecutionState("workflow_execution_kernel")!
            .Unpack<WorkflowExecutionKernelState>();
        state.Variables["steps.get_instance.json.success"].Should().Be("true");
        state.Variables["steps.get_instance.json.status"].Should().Be("approved");
        state.Variables["steps.get_instance.json.should_continue_waiting"].Should().Be("false");
        state.Variables["steps.get_instance.json.task_count"].Should().Be("1");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "aevatar.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root.");
    }

    private static EventEnvelope Wrap(IMessage evt) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
        Payload = Any.Pack(evt),
        Route = EnvelopeRouteSemantics.CreateTopologyPublication("test", TopologyAudience.Self),
    };

    private sealed class CapturingContext : IEventHandlerContext
    {
        public EventEnvelope InboundEnvelope { get; } = new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
        };

        public string AgentId => "agent-1";
        public IAgent Agent { get; } = new StubWorkflowRunAgent("agent-1", "run-1");
        public IServiceProvider Services { get; } = new NullServiceProvider();
        public Microsoft.Extensions.Logging.ILogger Logger { get; } = NullLogger.Instance;
        public List<(IMessage Event, TopologyAudience Direction)> Published { get; } = [];

        public Task PublishAsync<TEvent>(
            TEvent evt,
            TopologyAudience direction = TopologyAudience.Children,
            CancellationToken ct = default,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            Published.Add((evt, direction));
            return Task.CompletedTask;
        }

        public Task<RuntimeCallbackLease> ScheduleSelfDurableTimeoutAsync(
            string callbackId,
            TimeSpan dueTime,
            IMessage evt,
            EventEnvelopePublishOptions? options = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException("This test context does not support scheduling.");

        public Task<RuntimeCallbackLease> ScheduleSelfDurableTimerAsync(
            string callbackId,
            TimeSpan dueTime,
            TimeSpan period,
            IMessage evt,
            EventEnvelopePublishOptions? options = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException("This test context does not support scheduling.");

        public Task CancelDurableCallbackAsync(RuntimeCallbackLease lease, CancellationToken ct = default) =>
            throw new NotSupportedException("This test context does not support scheduling.");
    }

    private sealed class StubWorkflowRunAgent(string id, string runId) : IAgent, IWorkflowExecutionStateHost
    {
        private readonly Dictionary<string, Any> _executionStates = new(StringComparer.Ordinal);

        public string Id => id;
        public string RunId { get; } = runId;
        public WorkflowExecutionRuntimeContext RuntimeContext { get; } = new();
        public WorkflowRunExecutionContextState ExecutionContextState { get; } = new();
        public WorkflowRunExecutionContextState ExecutionContextSnapshot => ExecutionContextState.Clone();

        public Task UpdateExecutionContextAsync(WorkflowRunExecutionContextDelta delta, CancellationToken ct = default)
        {
            _ = delta;
            _ = ct;
            return Task.CompletedTask;
        }

        public Task ClearExecutionContextAsync(CancellationToken ct = default)
        {
            _ = ct;
            return Task.CompletedTask;
        }

        public Any? GetExecutionState(string scopeKey) =>
            _executionStates.TryGetValue(scopeKey, out var state) ? state : null;

        public IReadOnlyList<KeyValuePair<string, Any>> GetExecutionStates() =>
            _executionStates.ToList();

        public Task UpsertExecutionStateAsync(string scopeKey, Any state, CancellationToken ct = default)
        {
            _ = ct;
            _executionStates[scopeKey] = state;
            return Task.CompletedTask;
        }

        public Task ClearExecutionStateAsync(string scopeKey, CancellationToken ct = default)
        {
            _ = ct;
            _executionStates.Remove(scopeKey);
            return Task.CompletedTask;
        }

        Task<WorkflowCompensationTransitionResult> IWorkflowExecutionStateHost.TryStartCompensationAsync(
            WorkflowCompletedEvent terminalFailure,
            StepCompletedEvent? terminalStep,
            CancellationToken ct)
        {
            _ = terminalFailure;
            _ = terminalStep;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(NoCompensableLedger());
        }

        Task IWorkflowExecutionStateHost.RecordCompensableStepDispatchAsync(
            CompensableStepDispatchedEvent evt,
            CancellationToken ct)
        {
            _ = evt;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<WorkflowCompensationTransitionResult> RecordCompensationStepCompletionAsync(
            CompensationStepCompletedEvent completion,
            CancellationToken ct = default)
        {
            _ = completion;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(NoCompensableLedger());
        }

        public Task<WorkflowCompensationTransitionResult> RecordCompensationPhaseDeadlineExceededAsync(
            string runId,
            string error,
            CancellationToken ct = default)
        {
            _ = runId;
            _ = error;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(NoCompensableLedger());
        }

        private static WorkflowCompensationTransitionResult NoCompensableLedger() =>
            new(
                WorkflowCompensationTransitionStatus.NoCompensableLedger,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty);

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> GetDescriptionAsync() => Task.FromResult("stub");
        public Task<IReadOnlyList<System.Type>> GetSubscribedEventTypesAsync() =>
            Task.FromResult<IReadOnlyList<System.Type>>([]);
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class NullServiceProvider : IServiceProvider
    {
        public object? GetService(System.Type serviceType) => null;
    }
}

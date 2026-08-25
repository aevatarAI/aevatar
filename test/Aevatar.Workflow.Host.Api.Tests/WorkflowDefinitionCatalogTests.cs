using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Abstractions.Workflows;
using Aevatar.Workflow.Application.Workflows;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Core.Execution;
using Aevatar.Workflow.Core.Primitives;
using Aevatar.Workflow.Core.Validation;
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
        registry.Register(
            "test",
            "name: test\nsteps: []",
            ExternalCapabilityExecutionMode.Interactive);

        registry.GetYaml("test").Should().Contain("name: test");
        registry.GetYaml("TEST").Should().NotBeNull(); // Case-insensitive lookup.
        registry.GetYaml("nonexistent").Should().BeNull();
        registry.GetDefinition("test")!.DefinitionActorId.Should().Be(WorkflowDefinitionActorId.Format("test"));
        registry.GetDefinition("test")!.ExpectedExecutionMode.Should()
            .Be(ExternalCapabilityExecutionMode.Interactive);
    }

    [Fact]
    public void Register_WithUnspecifiedMode_ShouldReject()
    {
        var registry = new WorkflowDefinitionCatalog();

        var act = () => registry.Register(
            "test",
            "name: test\nsteps: []",
            ExternalCapabilityExecutionMode.Unspecified);

        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithMessage("*execution mode is required*");
        registry.GetNames().Should().BeEmpty();
    }

    [Fact]
    public void GetNames_ReturnsAll()
    {
        var registry = new WorkflowDefinitionCatalog();
        registry.Register("alpha", "a", ExternalCapabilityExecutionMode.Interactive);
        registry.Register("beta", "b", ExternalCapabilityExecutionMode.Interactive);

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
            registry.Register(
                "direct",
                "name: direct\nsteps:\n  - id: built_in\n",
                ExternalCapabilityExecutionMode.Interactive);
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

    [Theory]
    [InlineData("direct")]
    [InlineData("studio")]
    [InlineData("auto")]
    [InlineData("auto_review")]
    public void BuiltInWorkflow_ShouldSatisfyCurrentToolCatalogPublicationPolicy(string workflowName)
    {
        var yaml = workflowName switch
        {
            "direct" => WorkflowDefinitionCatalog.BuiltInDirectYaml,
            "studio" => WorkflowDefinitionCatalog.BuiltInStudioYaml,
            "auto" => WorkflowDefinitionCatalog.CreateBuiltInAutoYaml(),
            "auto_review" => WorkflowDefinitionCatalog.CreateBuiltInAutoReviewYaml(),
            _ => throw new ArgumentOutOfRangeException(nameof(workflowName)),
        };
        var workflow = new WorkflowParser().Parse(yaml);

        var errors = WorkflowValidator.Validate(
            workflow,
            new WorkflowValidator.WorkflowValidationOptions
            {
                RequireExplicitLlmAgentToolScopes = true,
            },
            availableWorkflowNames: null);

        errors.Should().BeEmpty();
    }

    [Fact]
    public void BuiltInYaml_ShouldExportTargetRoleField()
    {
        var builtInYaml = string.Join('\n',
            WorkflowDefinitionCatalog.BuiltInDirectYaml,
            WorkflowDefinitionCatalog.BuiltInStudioYaml,
            WorkflowDefinitionCatalog.CreateBuiltInAutoYaml(),
            WorkflowDefinitionCatalog.CreateBuiltInAutoReviewYaml());

        builtInYaml.Should().Contain("target_role:");
        builtInYaml.Should().NotContain("\n    role:");
        builtInYaml.Should().NotContain("\n          role:");
    }

    [Fact]
    public void BuiltInYaml_ShouldExportTargetRoleField()
    {
        var builtInYaml = string.Join('\n',
            WorkflowDefinitionCatalog.BuiltInDirectYaml,
            WorkflowDefinitionCatalog.BuiltInStudioYaml,
            WorkflowDefinitionCatalog.CreateBuiltInAutoYaml(),
            WorkflowDefinitionCatalog.CreateBuiltInAutoReviewYaml());

        builtInYaml.Should().Contain("target_role:");
        builtInYaml.Should().NotContain("\n    role:");
        builtInYaml.Should().NotContain("\n          role:");
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
        role.SystemPrompt.Should().Contain("/admin#/observatory");
        role.SystemPrompt.Should().Contain("Do NOT");
        // Honesty: the receipt is Accepted (async), not a success claim.
        role.SystemPrompt.Should().Contain("Accepted");
        role.SystemPrompt.Should().Contain("`aevatar_invoke_member` dispatches exactly one member run");
        role.SystemPrompt.Should().Contain("Never pass `wait: \"complete\"`");
        role.SystemPrompt.Should().Contain("A pending observation is not permission to dispatch another member run");
        // Schema teaching: without it the model falls back to foreign workflow
        // dialects (GitHub-Actions-style version:/inputs:) that the strict parser
        // rejects. Pin the load-bearing pieces: the closed top-level key list,
        // the foreign-dialect counter-examples, and a runnable example.
        role.SystemPrompt.Should().Contain(
            $"Authorable top-level keys are EXACTLY: {WorkflowYamlRootSchema.FormatAuthorableRootFields()}");
        role.SystemPrompt.Should().Contain(
            $"no {WorkflowYamlRootSchema.FormatUnsupportedDialectRootFields()}");
        role.SystemPrompt.Should().Contain("name: daily_digest");
        role.SystemPrompt.Should().Contain("`${json(...)}` escapes characters only; it does not add surrounding quotes.");
        role.SystemPrompt.Should().Contain("When embedding dynamic text as a JSON string value, write `\"${json(...)}\"`.");
        role.SystemPrompt.Should().Contain("If a tool argument field itself contains JSON encoded as a string");
        // Retry semantics: same display_name converges on the same resources;
        // reusing it for a different automation replaces the previous one.
        role.SystemPrompt.Should().Contain("SAME `display_name`");
        role.SystemPrompt.Should().Contain("REPLACES");
        // The loose-definition tools that hang on by-name resolution are no longer steered to.
        role.SystemPrompt.Should().NotContain("workflow_create_def");
        role.SystemPrompt.Should().NotContain("aevatar_start_workflow");

        // Static tools are restricted empty. The compatibility wrapper opts into bounded Studio
        // and workflow-authoring sets instead of combining every historical capability in one model turn.
        role.AgentToolScope.Should().NotBeNull();
        var allowed = role.AgentToolScope!.AllowedToolNames;
        role.AgentToolScope.RestrictAllowedToolNames.Should().BeTrue();
        allowed.Should().BeEmpty();
        role.AgentToolScope.ToolSetRefs.Should().Equal(
            "studio.local",
            "workflow.external-capability-authoring");

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
        role.SystemPrompt.Should().Contain("Treat connected-service visibility as a branch point");
        role.SystemPrompt.Should().Contain("If a caller-visible matching NyxID UserService is found");
        role.SystemPrompt.Should().Contain("execute through the connected-service operation path");
        role.SystemPrompt.Should().Contain("If no caller-visible matching NyxID UserService is found");
        role.SystemPrompt.Should().Contain("immediately resolve the named external service through `nyxid_catalog`");
        role.SystemPrompt.Should().Contain("then call `nyxid_require_service`");
        role.SystemPrompt.Should().Contain("Do not stop after `nyxid_services`");
        role.SystemPrompt.Should().Contain("For every connect, add, or authorize request");
        role.SystemPrompt.Should().Contain("`nyxid_catalog` is mandatory discovery");
        role.SystemPrompt.Should().Contain("Natural-language service or provider names are not exact NyxID catalog slugs");
        role.SystemPrompt.Should().Contain("Do not pass a display name, provider name, brand name, or ordinary service word as `nyxid_catalog.slug`");
        role.SystemPrompt.Should().Contain("If the exact catalog slug is not already verified in the current turn");
        role.SystemPrompt.Should().Contain("call `nyxid_catalog` without `slug`");
        role.SystemPrompt.Should().Contain("If a `nyxid_catalog` slug lookup returns 404 or `not_found`");
        role.SystemPrompt.Should().Contain("treat only that candidate slug as unverified");
        role.SystemPrompt.Should().Contain("recover by calling `nyxid_catalog` without `slug`");
        role.SystemPrompt.Should().Contain("Do not let a catalog 404 replace `nyxid_require_service`");
        role.SystemPrompt.Should().Contain("`catalogIdentityCandidate`");
        role.SystemPrompt.Should().Contain("only the exact returned `slug` may enter");
        role.SystemPrompt.Should().Contain("Never pass a provider slug, display name");
        role.SystemPrompt.Should().Contain("guessed").And.Contain("value");
        role.SystemPrompt.Should().Contain("For a bare source-code-hosting connection");
        role.SystemPrompt.Should().Contain("repository");
        role.SystemPrompt.Should().Contain("access scope instead of omitting scopes");
        role.SystemPrompt.Should().Contain("Then always call `nyxid_require_service`");
        role.SystemPrompt.Should().Contain("Never end the turn after catalog discovery");
        role.SystemPrompt.Should().Contain("the authority for the interactive");
        role.SystemPrompt.Should().Contain("`service.connect` handoff");
        role.SystemPrompt.Should().Contain("prose and catalog results are not substitutes");
        role.SystemPrompt.Should().Contain("use the admitted per-operation connected-service tool");
        role.SystemPrompt.Should().Contain("Do not call a provider-specific chat tool first");
        role.SystemPrompt.Should().Contain("`list_external_workflow_capabilities`");
        role.SystemPrompt.Should().Contain("copy that descriptor's exact `selector` object");
        role.SystemPrompt.Should().Contain("as the step-level `capability` value");
        role.SystemPrompt.Should().Contain("The list tool's `selector` uses workflow YAML field names");
        role.SystemPrompt.Should().Contain("Do not author protobuf JSON spellings `nyx_id_operation` or `nyx_id_request` in workflow YAML");
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
        role.AgentToolScope!.RestrictAllowedToolNames.Should().BeTrue();
        role.AgentToolScope.AllowedToolNames.Should().BeEmpty();
        role.AgentToolScope.ToolSetRefs.Should().Equal(
            "studio.local",
            "workflow.external-capability-authoring");
    }

    [Fact]
    public void BuiltInStudioYaml_ShouldAuthorNyxIdRequestWhenExactDescriptorIsUnavailable()
    {
        var workflow = new WorkflowParser().Parse(WorkflowDefinitionCatalog.BuiltInStudioYaml);
        var prompt = workflow.Roles.Should().ContainSingle().Subject.SystemPrompt;

        var discover = prompt.IndexOf("call `list_external_workflow_capabilities`", StringComparison.Ordinal);
        var selectService = prompt.IndexOf(
            "`nyxid_services` with `action: \"list\"`",
            discover + 1,
            StringComparison.Ordinal);
        var inspectService = prompt.IndexOf(
            "`nyxid_services` with `action: \"show\"`",
            selectService + 1,
            StringComparison.Ordinal);
        var search = prompt.IndexOf("call `web_search`", inspectService + 1, StringComparison.Ordinal);
        var fetch = prompt.IndexOf("call `web_fetch`", search + 1, StringComparison.Ordinal);
        var author = prompt.IndexOf(
            "`capability.nyxid_request` with the exact",
            fetch + 1,
            StringComparison.Ordinal);
        var saveDraft = prompt.IndexOf(
            "`aevatar_create_member_workflow_draft`",
            author + 1,
            StringComparison.Ordinal);
        var preview = prompt.IndexOf("`preview_workflow_explicit_requests`", saveDraft + 1, StringComparison.Ordinal);
        var bind = prompt.IndexOf("`aevatar_bind_member_workflow`", preview + 1, StringComparison.Ordinal);

        discover.Should().BeGreaterThanOrEqualTo(0);
        selectService.Should().BeGreaterThan(discover);
        inspectService.Should().BeGreaterThan(selectService);
        search.Should().BeGreaterThan(inspectService);
        fetch.Should().BeGreaterThan(search);
        author.Should().BeGreaterThan(fetch);
        saveDraft.Should().BeGreaterThan(author);
        preview.Should().BeGreaterThan(saveDraft);
        bind.Should().BeGreaterThan(preview);
        prompt.Should().Contain("No matching exact descriptor is a fallback trigger, not a blocker.");
        prompt.Should().Contain(
            "Only after `descriptor_discovery` returns no matching exact descriptor may the workflow enter the `nyxid_request` fallback branch.");
        prompt.Should().Contain("The next tool call MUST be");
        prompt.Should().Contain("Before capability resolution reaches");
        prompt.Should().Contain("`exact_operation_resolved`, `fallback_request_resolved`, or `fallback_exhausted`");
        prompt.Should().Contain("member, or workflow draft, and do not produce a final answer.");
        prompt.Should().Contain("Only these outcomes set `fallback_exhausted`");
        prompt.Should().Contain("official documentation");
        prompt.Should().Contain("exact `user_service_id`");
        prompt.Should().Contain("method, path_template");
        prompt.Should().Contain(
            "query_parameters, header_parameters, body_mode, body_required, and response_mode");
        prompt.Should().Contain("`tool_call` to `nyxid_proxy`");
        prompt.Should().Contain(
            "Only the descriptor-miss fallback may author `capability.nyxid_request` plus `nyxid_proxy` as the workflow-callable service path.");
        prompt.Should().Contain("either an exact descriptor or a descriptor-miss fallback request");
        prompt.Should().Contain("Only when no matching connected UserService exists or official documentation cannot establish");
        prompt.Should().Contain("`runnable=false`");
        prompt.Should().Contain("NYXID_OPERATION_SELECTION_REQUIRED");
        prompt.Should().Contain("Do not invent selector identities, operation proof, credentials, or server-owned proof fields");
        prompt.Should().NotContain("When the exact UserService and official HTTP contract are established");
        prompt.Should().NotContain("A canonical `capability.nyxid_request` plus `nyxid_proxy` is a workflow-callable");
        prompt.Should().NotContain("infer the minimal authoring shape");
        prompt.Should().NotContain("omit step-level `capability` when no exact selector exists");
        prompt.Should().NotContain("It is runnable only when every external invocation has an exact descriptor");
        prompt.Should().NotContain(
            "If no exact descriptor is available, report the typed readiness blocker instead of inventing");
        prompt.Should().NotContain("never to a chat or bot");
        prompt.Should().NotContain("Never deliver results to Lark/Telegram or any chat/bot");
        prompt.Should().Contain("workflow run records remain visible in the Observatory");
        prompt.Should().Contain("This does not prohibit authoring a workflow step that");
        prompt.Should().Contain(
            "calls Lark, Telegram, or another external messaging API requested by the user.");
    }

    [Fact]
    public void BuiltInStudioYaml_ShouldSplitExactOperationAndFallbackRequestBindingFlows()
    {
        var workflow = new WorkflowParser().Parse(WorkflowDefinitionCatalog.BuiltInStudioYaml);
        var prompt = workflow.Roles.Should().ContainSingle().Subject.SystemPrompt;

        var exactBranch = prompt.IndexOf(
            "Exact `capability.nyxid_operation` branch",
            StringComparison.Ordinal);
        var exactCreateDraft = prompt.IndexOf(
            "`aevatar_create_member_workflow_draft`",
            exactBranch + 1,
            StringComparison.Ordinal);
        var exactBind = prompt.IndexOf(
            "`aevatar_bind_member_workflow`",
            exactCreateDraft + 1,
            StringComparison.Ordinal);
        var exactNoPreview = prompt.IndexOf(
            "Do not call `preview_workflow_explicit_requests` for this exact-operation branch",
            exactBranch + 1,
            StringComparison.Ordinal);

        var fallbackBranch = prompt.IndexOf(
            "Fallback `capability.nyxid_request` branch",
            StringComparison.Ordinal);
        var fallbackCreateDraft = prompt.IndexOf(
            "`aevatar_create_member_workflow_draft`",
            fallbackBranch + 1,
            StringComparison.Ordinal);
        var fallbackPreview = prompt.IndexOf(
            "`preview_workflow_explicit_requests`",
            fallbackCreateDraft + 1,
            StringComparison.Ordinal);
        var fallbackBind = prompt.IndexOf(
            "`aevatar_bind_member_workflow`",
            fallbackPreview + 1,
            StringComparison.Ordinal);

        exactBranch.Should().BeGreaterThanOrEqualTo(0);
        exactCreateDraft.Should().BeGreaterThan(exactBranch);
        exactBind.Should().BeGreaterThan(exactCreateDraft);
        exactNoPreview.Should().BeGreaterThan(exactBind);
        fallbackBranch.Should().BeGreaterThan(exactNoPreview);
        fallbackCreateDraft.Should().BeGreaterThan(fallbackBranch);
        fallbackPreview.Should().BeGreaterThan(fallbackCreateDraft);
        fallbackBind.Should().BeGreaterThan(fallbackPreview);
        prompt.Should().Contain(
            "Only the descriptor-miss fallback branch calls `preview_workflow_explicit_requests`.");
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
        registry.Register(
            "direct",
            WorkflowDefinitionCatalog.BuiltInDirectYaml,
            ExternalCapabilityExecutionMode.Interactive);
        registry.Register(
            "studio",
            WorkflowDefinitionCatalog.BuiltInStudioYaml,
            ExternalCapabilityExecutionMode.Interactive);

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

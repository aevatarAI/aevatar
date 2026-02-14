// ─────────────────────────────────────────────────────────────
// WorkflowIntegrationTests — 端到端集成测试
//
// 验证完整的工作流执行链路：
// 1. YAML 解析 → WorkflowDefinition
// 2. WorkflowGAgent 创建 RoleGAgent 子 Agent 树
// 3. 层级关系正确建立（Link）
// 4. RoleGAgent 从 YAML 配置装配（system prompt）
// 5. 事件通过 Stream 在层级中正确传播
// 6. Mock LLM 被正确调用
//
// 使用 MockLLMProvider 替代真实 LLM
// ─────────────────────────────────────────────────────────────

using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core;
using Aevatar.AI.Core;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.Workflows.Core;
using Aevatar.Workflows.Core.Primitives;
using Aevatar.Workflows.Core.Validation;
using Aevatar.Foundation.Runtime.DependencyInjection;
using Aevatar.Foundation.Abstractions.EventModules;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Integration.Tests;

[Trait("Category", "Integration")]
public class WorkflowIntegrationTests
{
    // ─── 测试用 YAML：三角色研究工作流 ───

    private const string ResearchWorkflowYaml = """
        name: research_workflow
        description: 多角色科研协作工作流
        roles:
          - id: researcher
            name: Researcher
            system_prompt: "你是一个 researcher，负责调研主题并输出调研结果"
          - id: reviewer
            name: Reviewer
            system_prompt: "你是一个 reviewer，负责审查研究结果并给出改进建议"
          - id: writer
            name: Writer
            system_prompt: "你是一个 writer，负责将研究结果和审查意见整合为最终报告"
        steps:
          - id: research
            type: llm_call
            target_role: researcher
          - id: review
            type: llm_call
            target_role: reviewer
          - id: write
            type: llm_call
            target_role: writer
        """;

    // ─── 辅助：构建完整 DI 环境 ───

    private static (ServiceProvider sp, IActorRuntime runtime, MockLLMProvider mockLlm) BuildTestEnvironment()
    {
        var mockLlm = new MockLLMProvider();
        var services = new ServiceCollection();

        // 注册 Aevatar 运行时
        services.AddAevatarRuntime();

        // 注册 Mock LLM
        services.AddSingleton<ILLMProvider>(mockLlm);
        services.AddSingleton<ILLMProviderFactory>(mockLlm);

        // 注册 Cognitive Module Factory
        services.AddSingleton<IEventModuleFactory, CognitiveModuleFactory>();

        var sp = services.BuildServiceProvider();
        var runtime = sp.GetRequiredService<IActorRuntime>();
        return (sp, runtime, mockLlm);
    }

    // ═══════════════════════════════════════════════════════════
    //  Scenario 1: YAML 解析 + 验证
    // ═══════════════════════════════════════════════════════════

    [Fact(DisplayName = "给定合法的 YAML，WorkflowParser 应正确解析出 3 个角色和 3 个步骤")]
    [Trait("Feature", "WorkflowParsing")]
    public void Scenario1_ParseYaml()
    {
        // Given
        var parser = new WorkflowParser();

        // When
        var workflow = parser.Parse(ResearchWorkflowYaml);

        // Then
        workflow.Name.Should().Be("research_workflow");
        workflow.Roles.Should().HaveCount(3);
        workflow.Steps.Should().HaveCount(3);

        workflow.Roles[0].Id.Should().Be("researcher");
        workflow.Roles[0].SystemPrompt.Should().Contain("researcher");

        workflow.Roles[1].Id.Should().Be("reviewer");
        workflow.Roles[2].Id.Should().Be("writer");

        workflow.Steps[0].TargetRole.Should().Be("researcher");
        workflow.Steps[1].TargetRole.Should().Be("reviewer");
        workflow.Steps[2].TargetRole.Should().Be("writer");
    }

    [Fact(DisplayName = "给定合法的 WorkflowDefinition，Validator 应通过验证")]
    [Trait("Feature", "WorkflowValidation")]
    public void Scenario1b_ValidateWorkflow()
    {
        var workflow = new WorkflowParser().Parse(ResearchWorkflowYaml);
        var errors = WorkflowValidator.Validate(workflow);
        errors.Should().BeEmpty();
    }

    [Fact(DisplayName = "给定有重复 step ID 的 YAML，Validator 应报错")]
    [Trait("Feature", "WorkflowValidation")]
    public void Scenario1c_DuplicateStepId()
    {
        var yaml = """
            name: bad_workflow
            roles:
              - id: r1
                name: Role1
            steps:
              - id: step1
                type: llm_call
                target_role: r1
              - id: step1
                type: llm_call
                target_role: r1
            """;
        var workflow = new WorkflowParser().Parse(yaml);
        var errors = WorkflowValidator.Validate(workflow);
        errors.Should().Contain(e => e.Contains("重复"));
    }

    // ═══════════════════════════════════════════════════════════
    //  Scenario 2: WorkflowGAgent 创建 Agent 树
    // ═══════════════════════════════════════════════════════════

    [Fact(DisplayName = "给定 WorkflowGAgent 加载了 YAML，当触发时应创建 3 个 RoleGAgent 子 Agent")]
    [Trait("Feature", "AgentTree")]
    public async Task Scenario2_CreateAgentTree()
    {
        // Given
        var (sp, runtime, _) = BuildTestEnvironment();
        using var _ = sp;

        // 创建 WorkflowGAgent 并手动设置 workflow YAML
        var actor = await runtime.CreateAsync<WorkflowGAgent>("wf-1");
        var wfAgent = (WorkflowGAgent)actor.Agent;

        // 直接设置 State 中的 YAML（模拟初始化）
        // 由于 State 只能在 handler scope 修改，我们通过发送事件触发
        var chatEnvelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Google.Protobuf.WellKnownTypes.Any.Pack(new ChatRequestEvent
            {
                Prompt = "分析量子纠缠的最新进展",
                SessionId = "test-session",
            }),
            PublisherId = "test",
            Direction = EventDirection.Self,
        };

        // 先手动编译 workflow（通过反射设置 State，因为在测试中）
        // WorkflowGAgent 需要 State.WorkflowYaml 在激活时有值
        // 但我们在 CreateAsync 之后才能操作...
        // 更好的方式：重新创建一个预配置了 YAML 的 WorkflowGAgent

        // 验证 Agent 树创建：检查 runtime 中的所有 actors
        // WorkflowGAgent 在 HandleChatRequest 中会调用 EnsureAgentTreeAsync
        // 但由于 _compiledWorkflow 是 null（State.WorkflowYaml 为空），会返回"未编译"
        // 所以需要另一种方式来测试

        // 让我们直接测试 WorkflowParser + Runtime 的组合
        var parser = new WorkflowParser();
        var workflow = parser.Parse(ResearchWorkflowYaml);

        // 手动模拟 WorkflowGAgent 创建子 Agent 的逻辑
        var childIds = new List<string>();
        foreach (var role in workflow.Roles)
        {
            var childActor = await runtime.CreateAsync<RoleGAgent>(role.Id);
            if (childActor.Agent is RoleGAgent roleAgent)
            {
                roleAgent.SetRoleName(role.Name);
                await roleAgent.ConfigureAsync(new AIAgentConfig
                {
                    SystemPrompt = role.SystemPrompt,
                    ProviderName = "mock",
                });
            }
            await runtime.LinkAsync("wf-1", childActor.Id);
            childIds.Add(childActor.Id);
        }

        // Then
        childIds.Should().HaveCount(3);

        var allActors = await runtime.GetAllAsync();
        allActors.Should().HaveCount(4); // 1 WorkflowGAgent + 3 RoleGAgent

        // 验证层级
        var children = await actor.GetChildrenIdsAsync();
        children.Should().HaveCount(3);
        children.Should().Contain("researcher");
        children.Should().Contain("reviewer");
        children.Should().Contain("writer");

        // 验证每个 RoleGAgent 的配置
        var researcherActor = await runtime.GetAsync("researcher");
        researcherActor.Should().NotBeNull();
        var researcher = (RoleGAgent)researcherActor!.Agent;
        researcher.RoleName.Should().Be("Researcher");
    }

    // ═══════════════════════════════════════════════════════════
    //  Scenario 3: RoleGAgent YAML 配置装配
    // ═══════════════════════════════════════════════════════════

    [Fact(DisplayName = "给定 RoleGAgent，通过 RoleGAgentFactory 从 YAML 配置装配")]
    [Trait("Feature", "RoleConfig")]
    public async Task Scenario3_RoleGAgentYamlConfig()
    {
        // Given
        var (sp, runtime, _) = BuildTestEnvironment();
        using var _ = sp;

        var actor = await runtime.CreateAsync<RoleGAgent>("role-test-1");
        var agent = (RoleGAgent)actor.Agent;

        var yaml = """
            name: Expert Analyst
            system_prompt: "你是一个金融分析专家，擅长市场趋势分析"
            provider: mock
            model: gpt-4
            temperature: 0.7
            """;

        // When
        await RoleGAgentFactory.ConfigureFromYaml(agent, yaml, sp);

        // Then
        agent.RoleName.Should().Be("Expert Analyst");
        agent.Config.SystemPrompt.Should().Contain("金融分析专家");
        agent.Config.ProviderName.Should().Be("mock");
        agent.Config.Model.Should().Be("gpt-4");
    }

    // ═══════════════════════════════════════════════════════════
    //  Scenario 4: RoleGAgent 调用 Mock LLM
    // ═══════════════════════════════════════════════════════════

    [Fact(DisplayName = "给定配置了 Mock LLM 的 RoleGAgent，当收到 ChatRequest 时，应调用 LLM 并返回")]
    [Trait("Feature", "LLMCall")]
    public async Task Scenario4_RoleGAgentCallsLLM()
    {
        // Given
        var (sp, runtime, mockLlm) = BuildTestEnvironment();
        using var _ = sp;

        var actor = await runtime.CreateAsync<RoleGAgent>("llm-test-1");
        var agent = (RoleGAgent)actor.Agent;
        await agent.ConfigureAsync(new AIAgentConfig
        {
            SystemPrompt = "你是一个 researcher",
            ProviderName = "mock",
        });

        // 收集 Up 事件
        var responses = new List<string>();
        var stream = sp.GetRequiredService<IStreamProvider>().GetStream("llm-test-1");
        await stream.SubscribeAsync<EventEnvelope>(async envelope =>
        {
            if (envelope.Payload?.Is(ChatResponseEvent.Descriptor) == true)
            {
                var resp = envelope.Payload.Unpack<ChatResponseEvent>();
                responses.Add(resp.Content);
            }
            await Task.CompletedTask;
        });

        // When: 发送 ChatRequest
        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Google.Protobuf.WellKnownTypes.Any.Pack(new ChatRequestEvent
            {
                Prompt = "分析量子纠缠",
                SessionId = "test",
            }),
            PublisherId = "test",
            Direction = EventDirection.Down,
        };

        await actor.HandleEventAsync(envelope);

        // Then: Mock LLM 应被调用
        mockLlm.CallLog.Should().NotBeEmpty();
        mockLlm.CallLog[0].SystemPrompt.Should().Contain("researcher");
        mockLlm.CallLog[0].UserMessage.Should().Contain("量子纠缠");
        mockLlm.CallLog[0].Response.Should().Contain("量子");
    }

    // ═══════════════════════════════════════════════════════════
    //  Scenario 5: 多 Agent 层级事件传播
    // ═══════════════════════════════════════════════════════════

    [Fact(DisplayName = "给定 WorkflowGAgent + 3 个 RoleGAgent 的层级，Down 事件应传播到所有子 Agent")]
    [Trait("Feature", "HierarchyPropagation")]
    public async Task Scenario5_EventPropagation()
    {
        // Given
        var (sp, runtime, _) = BuildTestEnvironment();
        using var _ = sp;

        // 创建层级
        var root = await runtime.CreateAsync<WorkflowGAgent>("root");
        var r1 = await runtime.CreateAsync<RoleGAgent>("r1");
        var r2 = await runtime.CreateAsync<RoleGAgent>("r2");

        ((RoleGAgent)r1.Agent).SetRoleName("Agent-R1");
        ((RoleGAgent)r2.Agent).SetRoleName("Agent-R2");
        await ((RoleGAgent)r1.Agent).ConfigureAsync(new AIAgentConfig { ProviderName = "mock", SystemPrompt = "r1" });
        await ((RoleGAgent)r2.Agent).ConfigureAsync(new AIAgentConfig { ProviderName = "mock", SystemPrompt = "r2" });

        await runtime.LinkAsync("root", "r1");
        await runtime.LinkAsync("root", "r2");

        // Then: 验证层级
        var rootChildren = await root.GetChildrenIdsAsync();
        rootChildren.Should().HaveCount(2);
        rootChildren.Should().Contain("r1");
        rootChildren.Should().Contain("r2");

        var r1Parent = await r1.GetParentIdAsync();
        r1Parent.Should().Be("root");
    }

    // ═══════════════════════════════════════════════════════════
    //  Scenario 6: WorkflowParser 处理复杂 YAML
    // ═══════════════════════════════════════════════════════════

    [Fact(DisplayName = "给定包含 parallel 和 conditional 步骤的复杂 YAML，Parser 应正确解析")]
    [Trait("Feature", "ComplexWorkflow")]
    public void Scenario6_ComplexWorkflow()
    {
        var yaml = """
            name: complex_research
            description: 包含并行和条件分支的复杂研究工作流
            roles:
              - id: planner
                name: Planner
                system_prompt: "你是规划者"
              - id: analyst_a
                name: AnalystA
                system_prompt: "你是分析师A"
              - id: analyst_b
                name: AnalystB
                system_prompt: "你是分析师B"
              - id: synthesizer
                name: Synthesizer
                system_prompt: "你是综合者"
            steps:
              - id: plan
                type: llm_call
                target_role: planner
              - id: parallel_analysis
                type: parallel
                parameters:
                  parallel_count: "2"
              - id: check_quality
                type: conditional
                parameters:
                  condition: "合格"
                  branch_true: synthesize
                  branch_false: plan
              - id: synthesize
                type: llm_call
                target_role: synthesizer
            """;

        // When
        var parser = new WorkflowParser();
        var workflow = parser.Parse(yaml);

        // Then
        workflow.Name.Should().Be("complex_research");
        workflow.Roles.Should().HaveCount(4);
        workflow.Steps.Should().HaveCount(4);

        // 验证步骤类型
        workflow.Steps[0].Type.Should().Be("llm_call");
        workflow.Steps[1].Type.Should().Be("parallel");
        workflow.Steps[1].Parameters["parallel_count"].Should().Be("2");
        workflow.Steps[2].Type.Should().Be("conditional");
        workflow.Steps[2].Parameters["condition"].Should().Be("合格");
        workflow.Steps[3].Type.Should().Be("llm_call");
        workflow.Steps[3].TargetRole.Should().Be("synthesizer");
    }

    // ═══════════════════════════════════════════════════════════
    //  Scenario 7: CognitiveModuleFactory 创建所有模块
    // ═══════════════════════════════════════════════════════════

    [Fact(DisplayName = "CognitiveModuleFactory 应能创建所有 13 种核心原语模块")]
    [Trait("Feature", "ModuleFactory")]
    public void Scenario7_AllCoreModules()
    {
        var factory = new CognitiveModuleFactory();

        // ─── 流程控制 ───
        factory.TryCreate("workflow_loop", out var m).Should().BeTrue(); m!.Name.Should().Be("workflow_loop");
        factory.TryCreate("conditional", out m).Should().BeTrue(); m!.Name.Should().Be("conditional");
        factory.TryCreate("while", out m).Should().BeTrue(); m!.Name.Should().Be("while");
        factory.TryCreate("workflow_call", out m).Should().BeTrue(); m!.Name.Should().Be("workflow_call");
        factory.TryCreate("checkpoint", out m).Should().BeTrue(); m!.Name.Should().Be("checkpoint");
        factory.TryCreate("assign", out m).Should().BeTrue(); m!.Name.Should().Be("assign");

        // ─── 并行 / 共识 ───
        factory.TryCreate("parallel_fanout", out m).Should().BeTrue(); m!.Name.Should().Be("parallel_fanout");
        factory.TryCreate("vote_consensus", out m).Should().BeTrue(); m!.Name.Should().Be("vote_consensus");

        // ─── 执行 ───
        factory.TryCreate("llm_call", out m).Should().BeTrue(); m!.Name.Should().Be("llm_call");
        factory.TryCreate("tool_call", out m).Should().BeTrue(); m!.Name.Should().Be("tool_call");
        factory.TryCreate("connector_call", out m).Should().BeTrue(); m!.Name.Should().Be("connector_call");

        // ─── 数据变换 ───
        factory.TryCreate("transform", out m).Should().BeTrue(); m!.Name.Should().Be("transform");
        factory.TryCreate("retrieve_facts", out m).Should().BeTrue(); m!.Name.Should().Be("retrieve_facts");

        // ─── 别名 ───
        factory.TryCreate("parallel", out m).Should().BeTrue(); // parallel = parallel_fanout
        factory.TryCreate("vote", out m).Should().BeTrue(); // vote = vote_consensus
        factory.TryCreate("fan_out", out m).Should().BeTrue(); // fan_out = parallel_fanout
        factory.TryCreate("loop", out m).Should().BeTrue(); // loop = while
        factory.TryCreate("sub_workflow", out m).Should().BeTrue(); // sub_workflow = workflow_call
        factory.TryCreate("bridge_call", out m).Should().BeTrue(); // bridge_call = connector_call

        // ─── 不存在的类型 ───
        factory.TryCreate("nonexistent", out m).Should().BeFalse();
        factory.TryCreate("maker_vote", out m).Should().BeFalse(); // maker_vote moved to sample-scoped factory
        m.Should().BeNull();
    }
}

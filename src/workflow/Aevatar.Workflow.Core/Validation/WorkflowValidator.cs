// ─────────────────────────────────────────────────────────────
// WorkflowValidator — 工作流校验器
// 校验工作流定义的完整性（名称、步骤、角色引用、后继引用）
// ─────────────────────────────────────────────────────────────

using Aevatar.Workflow.Core.Primitives;
using Aevatar.Workflow.Core.Agreement;
using Aevatar.Workflow.Core.Execution;
using Aevatar.Workflow.Abstractions;

namespace Aevatar.Workflow.Core.Validation;

/// <summary>
/// 工作流校验器。对工作流定义进行静态校验，返回错误信息列表。
/// </summary>
public static class WorkflowValidator
{
    /// <summary>
    /// 校验工作流定义。
    /// 检查 name 非空、至少一个步骤、步骤 ID 不重复、角色引用有效、next 引用有效。
    /// </summary>
    /// <param name="wf">待校验的工作流定义。</param>
    /// <returns>错误消息列表，无错误时返回空列表。</returns>
    public static List<string> Validate(WorkflowDefinition wf) =>
        Validate(wf, options: null, availableWorkflowNames: null);

    /// <summary>
    /// 校验工作流定义（可选开启额外闭包规则与跨 workflow 解析校验）。
    /// </summary>
    public static List<string> Validate(
        WorkflowDefinition wf,
        WorkflowValidationOptions? options,
        ISet<string>? availableWorkflowNames)
    {
        options ??= WorkflowValidationOptions.Default;
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(wf.Name)) errors.Add("缺少 name");
        if (wf.Steps.Count == 0) errors.Add("至少需要一个 step");

        var knownCanonicalStepTypes = options.RequireKnownStepTypes
            ? WorkflowPrimitiveCatalog.BuildCanonicalStepTypeSet(options.KnownStepTypes)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var allSteps = EnumerateSteps(wf.Steps).ToList();
        var stepIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var step in allSteps)
        {
            if (string.IsNullOrWhiteSpace(step.Id))
            {
                errors.Add("存在缺少 id 的步骤");
                continue;
            }

            if (!stepIds.Add(step.Id))
                errors.Add($"步骤 '{step.Id}' 重复");
        }

        var roleIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var role in wf.Roles)
        {
            if (string.IsNullOrWhiteSpace(role.Id))
            {
                errors.Add("存在缺少 id 的角色");
                continue;
            }

            roleIds.Add(role.Id);
        }

        ValidateValueLifecycles(wf.Steps, stepIds, nested: false, errors);

        foreach (var step in allSteps)
        {
            if (!string.IsNullOrWhiteSpace(step.TargetRole) &&
                !roleIds.Contains(step.TargetRole))
            {
                errors.Add($"步骤 '{step.Id}' 引用不存在的角色 '{step.TargetRole}'");
            }

            if (!string.IsNullOrWhiteSpace(step.Next) && !stepIds.Contains(step.Next))
                errors.Add($"步骤 '{step.Id}' 的 next 引用不存在的步骤 '{step.Next}'");

            if (!string.IsNullOrWhiteSpace(step.Compensation) && step.Compensation == step.Id)
                errors.Add($"步骤 '{step.Id}' 的 compensation 不能指向自身");

            if (!string.IsNullOrWhiteSpace(step.Compensation) && !stepIds.Contains(step.Compensation))
                errors.Add($"步骤 '{step.Id}' 的 compensation '{step.Compensation}' 引用不存在的步骤 '{step.Compensation}'");

            ValidateBranchTargets(step, stepIds, errors);
            ValidateTypeSpecificRules(step, availableWorkflowNames, knownCanonicalStepTypes, options, errors);
        }

        if (options.RequireExplicitLlmAgentToolScopes)
            ValidateLlmAgentToolScopes(wf, allSteps, errors);

        DetectCompensationCycles(allSteps, stepIds, errors);
        ValidateCompensationTargetsExcludedFromForwardPath(allSteps, errors);
        ValidateNestedCompensationTargets(allSteps, errors);

        return errors;
    }

    private static void ValidateLlmAgentToolScopes(
        WorkflowDefinition workflow,
        IReadOnlyList<StepDefinition> allSteps,
        List<string> errors)
    {
        var rolesById = workflow.Roles
            .Where(static role => !string.IsNullOrWhiteSpace(role.Id))
            .GroupBy(static role => role.Id.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var role in workflow.Roles)
        {
            if (role.AgentToolScope is not null)
                ValidateAgentToolScope($"Role '{role.Id}'", role.AgentToolScope, errors);
        }

        foreach (var step in allSteps)
        {
            if (step.AgentToolScope is not null)
                ValidateAgentToolScope($"Step '{step.Id}'", step.AgentToolScope, errors);

            if (!CanInvokeLlm(step))
                continue;

            var targetRole = WorkflowImplicitLlmRolePolicy.ResolveEffectiveTargetRole(workflow, step);
            var roleScope = rolesById.TryGetValue(targetRole, out var role)
                ? role.AgentToolScope
                : null;
            if (roleScope is null && step.AgentToolScope is null)
            {
                errors.Add(
                    $"Step '{step.Id}' can invoke llm_call and must declare an explicit allowed_tools scope " +
                    "on the step or its target role.");
            }
        }
    }

    private static bool CanInvokeLlm(StepDefinition step)
    {
        if (string.Equals(
                WorkflowPrimitiveCatalog.ToCanonicalType(step.Type),
                "llm_call",
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return step.Parameters.Any(static pair =>
            WorkflowPrimitiveCatalog.IsStepTypeParameterKey(pair.Key) &&
            string.Equals(
                WorkflowPrimitiveCatalog.ToCanonicalType(pair.Value),
                "llm_call",
                StringComparison.OrdinalIgnoreCase));
    }

    private static void ValidateAgentToolScope(
        string owner,
        WorkflowAgentToolScopeDefinition scope,
        List<string> errors)
    {
        if (!scope.RestrictAllowedToolNames)
        {
            errors.Add($"{owner} tool scope must explicitly declare allowed_tools (an empty list is valid).");
            return;
        }

        var normalizedNames = scope.AllowedToolNames
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Select(static name => name.Trim())
            .ToArray();
        if (normalizedNames.Distinct(StringComparer.OrdinalIgnoreCase).Count() != normalizedNames.Length)
            errors.Add($"{owner} allowed_tools contains duplicate names.");
    }

    private static void ValidateValueLifecycles(
        IEnumerable<StepDefinition> steps,
        ISet<string> stepIds,
        bool nested,
        List<string> errors)
    {
        foreach (var step in steps)
        {
            var lifecycle = step.ValueLifecycle;
            if (lifecycle != null)
            {
                if (nested)
                {
                    errors.Add($"步骤 '{step.Id}' 的 value_lifecycle 只允许声明在顶层步骤");
                }

                if (lifecycle.ReleaseVariablesAfterSuccess.Count == 0)
                {
                    errors.Add(
                        $"步骤 '{step.Id}' 的 value_lifecycle.release_variables_after_success 不能为空");
                }

                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var rawName in lifecycle.ReleaseVariablesAfterSuccess)
                {
                    var name = rawName?.Trim() ?? string.Empty;
                    if (name.Length == 0)
                    {
                        errors.Add($"步骤 '{step.Id}' 的 value_lifecycle 包含空变量名");
                        continue;
                    }

                    if (!names.Add(name))
                    {
                        errors.Add($"步骤 '{step.Id}' 的 value_lifecycle 重复释放变量 '{name}'");
                        continue;
                    }

                    if (!WorkflowExecutionValueStore.IsLifecycleReleaseVariableKey(name) ||
                        stepIds.Contains(name))
                    {
                        errors.Add(
                            $"步骤 '{step.Id}' 的 value_lifecycle 只能释放 author-owned 变量，'{name}' 是保留名称");
                    }
                }
            }

            if (step.Children is { Count: > 0 })
                ValidateValueLifecycles(step.Children, stepIds, nested: true, errors);
        }
    }

    private static void DetectCompensationCycles(
        IReadOnlyList<StepDefinition> allSteps,
        ISet<string> stepIds,
        List<string> errors)
    {
        var stepsById = BuildFirstStepById(allSteps);
        var inspectedStarts = new HashSet<string>(StringComparer.Ordinal);
        var reportedCycles = new HashSet<string>(StringComparer.Ordinal);

        foreach (var step in allSteps)
        {
            if (string.IsNullOrWhiteSpace(step.Id) || !inspectedStarts.Add(step.Id))
                continue;

            var path = new List<string>();
            var pathIndexes = new Dictionary<string, int>(StringComparer.Ordinal);
            var currentId = step.Id;

            while (stepsById.TryGetValue(currentId, out var currentStep))
            {
                pathIndexes[currentId] = path.Count;
                path.Add(currentId);

                var nextId = currentStep.Compensation;
                if (string.IsNullOrWhiteSpace(nextId) ||
                    !stepIds.Contains(nextId) ||
                    nextId == currentId)
                {
                    break;
                }

                if (pathIndexes.TryGetValue(nextId, out var cycleStartIndex))
                {
                    var cyclePath = path
                        .Skip(cycleStartIndex)
                        .Append(nextId)
                        .ToList();
                    var cycleKey = string.Join(
                        "\u001F",
                        cyclePath
                            .Take(cyclePath.Count - 1)
                            .OrderBy(id => id, StringComparer.Ordinal));

                    if (reportedCycles.Add(cycleKey))
                    {
                        errors.Add(
                            $"步骤 '{step.Id}' 的 compensation 链构成环：{string.Join(" -> ", cyclePath)}");
                    }
                    break;
                }

                currentId = nextId;
            }
        }
    }

    private static void ValidateCompensationTargetsExcludedFromForwardPath(
        IReadOnlyList<StepDefinition> allSteps,
        List<string> errors)
    {
        var forwardReferenced = new HashSet<string>(StringComparer.Ordinal);
        foreach (var step in allSteps)
        {
            if (!string.IsNullOrWhiteSpace(step.Next))
                forwardReferenced.Add(step.Next);

            if (step.Branches == null)
                continue;

            foreach (var target in step.Branches.Values)
            {
                if (!string.IsNullOrWhiteSpace(target))
                    forwardReferenced.Add(target);
            }
        }

        var reportedTargets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var step in allSteps)
        {
            if (string.IsNullOrWhiteSpace(step.Compensation) ||
                !forwardReferenced.Contains(step.Compensation) ||
                !reportedTargets.Add(step.Compensation))
            {
                continue;
            }

            errors.Add($"步骤 '{step.Compensation}' 既是 compensation 目标又出现在正向路径（next/branches），会被双重执行");
        }
    }

    private static void ValidateNestedCompensationTargets(
        IReadOnlyList<StepDefinition> allSteps,
        List<string> errors)
    {
        var stepsById = BuildFirstStepById(allSteps);
        var reportedTargets = new HashSet<string>(StringComparer.Ordinal);

        foreach (var step in allSteps)
        {
            if (string.IsNullOrWhiteSpace(step.Compensation) ||
                !reportedTargets.Add(step.Compensation) ||
                !stepsById.TryGetValue(step.Compensation, out var compensationStep) ||
                string.IsNullOrWhiteSpace(compensationStep.Compensation))
            {
                continue;
            }

            errors.Add($"步骤 '{step.Compensation}' 是 compensation 步骤，不允许再声明 compensation（不会在反向 walk 生效）");
        }
    }

    private static Dictionary<string, StepDefinition> BuildFirstStepById(IEnumerable<StepDefinition> allSteps)
    {
        var stepsById = new Dictionary<string, StepDefinition>(StringComparer.Ordinal);
        foreach (var step in allSteps)
        {
            if (!string.IsNullOrWhiteSpace(step.Id))
                stepsById.TryAdd(step.Id, step);
        }

        return stepsById;
    }

    private static void ValidateBranchTargets(
        StepDefinition step,
        ISet<string> stepIds,
        List<string> errors)
    {
        if (step.Branches == null)
            return;

        foreach (var branch in step.Branches)
        {
            if (string.IsNullOrWhiteSpace(branch.Value))
            {
                errors.Add($"步骤 '{step.Id}' 的分支 '{branch.Key}' 缺少目标步骤");
                continue;
            }

            if (!stepIds.Contains(branch.Value))
                errors.Add($"步骤 '{step.Id}' 的分支 '{branch.Key}' 引用不存在的步骤 '{branch.Value}'");
        }
    }

    private static void ValidateTypeSpecificRules(
        StepDefinition step,
        ISet<string>? availableWorkflowNames,
        ISet<string> knownCanonicalStepTypes,
        WorkflowValidationOptions options,
        List<string> errors)
    {
        var stepType = WorkflowPrimitiveCatalog.ToCanonicalType(step.Type);
        if (options.RequireKnownStepTypes &&
            !WorkflowPrimitiveCatalog.IsKnownStepType(stepType, knownCanonicalStepTypes))
        {
            errors.Add($"步骤 '{step.Id}' 使用未知原语 '{step.Type}'（canonical='{stepType}'）");
        }

        foreach (var (key, value) in step.Parameters)
        {
            if (!WorkflowPrimitiveCatalog.IsStepTypeParameterKey(key) ||
                string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var parameterStepType = WorkflowPrimitiveCatalog.ToCanonicalType(value);
            if (options.RequireKnownStepTypes &&
                !WorkflowPrimitiveCatalog.IsKnownStepType(parameterStepType, knownCanonicalStepTypes))
            {
                errors.Add(
                    $"步骤 '{step.Id}' 的参数 '{key}' 使用未知原语 '{value}'（canonical='{parameterStepType}'）");
            }
        }

        ValidateRawActorLifecycleParameters(step, errors);
        ValidatePresentationRules(step, stepType, errors);

        if (stepType == "conditional")
        {
            if (step.Branches == null || step.Branches.Count == 0)
            {
                errors.Add($"步骤 '{step.Id}'（conditional）必须定义 branches");
            }
            else
            {
                if (!step.Branches.ContainsKey("true"))
                    errors.Add($"步骤 '{step.Id}'（conditional）缺少 true 分支");
                if (!step.Branches.ContainsKey("false"))
                    errors.Add($"步骤 '{step.Id}'（conditional）缺少 false 分支");
            }
            return;
        }

        if (stepType == "switch")
        {
            if (step.Branches == null || step.Branches.Count == 0)
            {
                errors.Add($"步骤 '{step.Id}'（switch）必须定义 branches");
            }
            else if (!step.Branches.ContainsKey("_default"))
            {
                errors.Add($"步骤 '{step.Id}'（switch）必须定义 _default 分支");
            }
            return;
        }

        if (stepType == "while")
        {
            var hasCondition = step.Parameters.TryGetValue("condition", out var condition) &&
                               !string.IsNullOrWhiteSpace(condition);
            var hasMaxIterations = step.Parameters.TryGetValue("max_iterations", out var maxIterationsRaw);
            if (!hasCondition && !hasMaxIterations)
                errors.Add($"步骤 '{step.Id}'（while）至少需要 condition 或 max_iterations");

            if (hasMaxIterations &&
                (!int.TryParse(maxIterationsRaw, out var maxIterations) || maxIterations <= 0))
            {
                errors.Add($"步骤 '{step.Id}'（while）max_iterations 必须是正整数");
            }

            if (step.Parameters.TryGetValue("step", out var subStepType) &&
                string.IsNullOrWhiteSpace(subStepType))
            {
                errors.Add($"步骤 '{step.Id}'（while）step 参数不能为空");
            }
            return;
        }

        if (stepType == "workflow_call")
        {
            var workflowName = step.Parameters.GetValueOrDefault("workflow", "").Trim();
            if (string.IsNullOrEmpty(workflowName))
            {
                errors.Add($"步骤 '{step.Id}'（workflow_call）缺少 workflow 参数");
                return;
            }

            var lifecycle = step.Parameters.GetValueOrDefault("lifecycle", "").Trim();
            if (!WorkflowCallLifecycle.IsSupported(lifecycle))
            {
                errors.Add(
                    $"步骤 '{step.Id}'（workflow_call）lifecycle 仅支持 {WorkflowCallLifecycle.AllowedValuesText}，当前值 '{lifecycle}'");
            }

            if (options.RequireResolvableWorkflowCallTargets &&
                availableWorkflowNames != null &&
                !availableWorkflowNames.Contains(workflowName))
            {
                errors.Add($"步骤 '{step.Id}'（workflow_call）引用未注册工作流 '{workflowName}'");
            }
            return;
        }

        if (stepType == "dynamic_workflow" &&
            options.DisallowDynamicWorkflowStep)
        {
            errors.Add(
                $"步骤 '{step.Id}' 使用了保留原语 'dynamic_workflow'。请改为 workflow_call 或其他业务原语。");
        }

        if (stepType == "vote")
        {
            ValidateVoteAgreementStep(step, errors);
            return;
        }

        if (stepType == "lease")
        {
            ValidateLeaseStep(step, errors);
            return;
        }

        if (stepType == "parallel")
        {
            ValidateParallelVoteAgreement(step, errors);
        }
    }

    private static void ValidatePresentationRules(
        StepDefinition step,
        string stepType,
        List<string> errors)
    {
        if (StepPresentation.HasInteractionTemplateSpec(step.Presentation?.InteractionTemplateSpec) &&
            !string.Equals(stepType, "notify", StringComparison.Ordinal))
        {
            errors.Add($"步骤 '{step.Id}' 的 interaction_template_spec 只允许用于 notify 步骤");
        }
    }

    private static void ValidateVoteAgreementStep(StepDefinition step, List<string> errors)
    {
        if (!VoteAgreementRuleConfigurationParser.TryParse(step.Parameters, out var rule, out var error))
        {
            errors.Add($"步骤 '{step.Id}'（vote）{error}");
            return;
        }

        ValidateConfiguredDecisionBranches(step, rule, errors);
    }

    private static void ValidateParallelVoteAgreement(StepDefinition step, List<string> errors)
    {
        if (!TryGetParameter(step.Parameters, "vote_step_type", out var voteStepType) ||
            string.IsNullOrWhiteSpace(voteStepType) ||
            !string.Equals(WorkflowPrimitiveCatalog.ToCanonicalType(voteStepType), "vote", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var voteParameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in step.Parameters)
        {
            var voteParameterKey = VoteAgreementRuleConfigurationParser.StripVoteParameterPrefix(key);
            if (voteParameterKey != null)
                voteParameters[voteParameterKey] = value;
        }

        if (!VoteAgreementRuleConfigurationParser.TryParse(voteParameters, out _, out var error))
            errors.Add($"步骤 '{step.Id}'（parallel.vote）{error}");
    }

    private static void ValidateLeaseStep(StepDefinition step, List<string> errors)
    {
        var action = GetParameterOrDefault(step.Parameters, "action", "acquire").Trim().ToLowerInvariant();
        if (action is not "acquire" and not "renew" and not "release")
            errors.Add($"步骤 '{step.Id}'（lease）action 仅支持 acquire|renew|release，当前值 '{action}'");

        if (!TryGetParameter(step.Parameters, "key", out var key) || string.IsNullOrWhiteSpace(key))
            errors.Add($"步骤 '{step.Id}'（lease）缺少 key 参数");

        ValidateOptionalBoundedInt(
            step,
            "ttl_ms",
            WorkflowLeaseGAgent.MinLeaseTtlMs,
            WorkflowLeaseGAgent.MaxLeaseTtlMs,
            errors);
        ValidateOptionalBoundedInt(
            step,
            "wait_timeout_ms",
            WorkflowLeaseGAgent.MinWaitTimeoutMs,
            WorkflowLeaseGAgent.MaxWaitTimeoutMs,
            errors);

        if (TryGetParameter(step.Parameters, "on_conflict", out var onConflict) &&
            !string.IsNullOrWhiteSpace(onConflict) &&
            !string.Equals(onConflict.Trim(), "fail", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(onConflict.Trim(), "wait", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"步骤 '{step.Id}'（lease）on_conflict 仅支持 fail|wait，当前值 '{onConflict}'");
        }

        var hasHolderToken = TryGetParameter(step.Parameters, "holder_token", out var holderToken) &&
                             !string.IsNullOrWhiteSpace(holderToken);
        var hasGeneration = TryGetParameter(step.Parameters, "generation", out var generation) &&
                            !string.IsNullOrWhiteSpace(generation);
        if (action is "renew" or "release")
        {
            if (!hasHolderToken)
                errors.Add($"步骤 '{step.Id}'（lease）{action} 必须声明 holder_token");
            if (!hasGeneration)
                errors.Add($"步骤 '{step.Id}'（lease）{action} 必须声明 generation");
        }
        else
        {
            if (hasHolderToken)
                errors.Add($"步骤 '{step.Id}'（lease）acquire 不允许声明 holder_token");
            if (hasGeneration)
                errors.Add($"步骤 '{step.Id}'（lease）acquire 不允许声明 generation");
        }
    }

    private static void ValidateOptionalBoundedInt(
        StepDefinition step,
        string parameterName,
        int min,
        int max,
        List<string> errors)
    {
        if (!TryGetParameter(step.Parameters, parameterName, out var raw) ||
            string.IsNullOrWhiteSpace(raw))
        {
            return;
        }

        if (!int.TryParse(raw.Trim(), out var value) || value < min || value > max)
            errors.Add($"步骤 '{step.Id}'（lease）{parameterName} 必须是 {min}..{max} 范围内的整数");
    }

    private static void ValidateConfiguredDecisionBranches(
        StepDefinition step,
        VoteAgreementRule rule,
        List<string> errors)
    {
        ValidateDecisionBranch(step, rule.OnAgreed, "on_agreed", errors);
        ValidateDecisionBranch(step, rule.OnRejected, "on_rejected", errors);
        ValidateDecisionBranch(step, rule.OnInconclusive, "on_inconclusive", errors);
    }

    private static void ValidateDecisionBranch(
        StepDefinition step,
        string branchKey,
        string parameterName,
        List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(branchKey))
            return;

        if (step.Branches == null || !step.Branches.ContainsKey(branchKey))
            errors.Add($"步骤 '{step.Id}'（vote）参数 '{parameterName}' 指向未定义分支 '{branchKey}'");
    }
    private static void ValidateRawActorLifecycleParameters(StepDefinition step, List<string> errors)
    {
        if (TryGetParameter(step.Parameters, "agent_type", out _))
            errors.Add($"步骤 '{step.Id}' 的参数 'agent_type' 已废止；请在 roles.agent_kind 绑定 stable kind，并在步骤使用 target_role");

        if (TryGetParameter(step.Parameters, "agent_id", out _))
            errors.Add($"步骤 '{step.Id}' 的参数 'agent_id' 已废止；actor id 由 WorkflowRunGAgent 生命周期管理");
    }

    private static bool TryGetParameter(
        IReadOnlyDictionary<string, string> parameters,
        string key,
        out string value)
    {
        if (parameters.TryGetValue(key, out value!))
            return true;

        foreach (var (existingKey, existingValue) in parameters)
        {
            if (string.Equals(existingKey, key, StringComparison.OrdinalIgnoreCase))
            {
                value = existingValue;
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    private static string GetParameterOrDefault(
        IReadOnlyDictionary<string, string> parameters,
        string key,
        string fallback) =>
        TryGetParameter(parameters, key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : fallback;

    public sealed class WorkflowValidationOptions
    {
        public static WorkflowValidationOptions Default { get; } = new();

        /// <summary>
        /// 兼容保留字段；validator 不再基于 closed world 配置阻止 step type。
        /// </summary>
        public bool? ForceClosedWorldMode { get; init; }

        /// <summary>
        /// 当提供 availableWorkflowNames 时，是否强制校验 workflow_call 目标可解析。
        /// </summary>
        public bool RequireResolvableWorkflowCallTargets { get; init; } = true;

        /// <summary>
        /// 当 KnownStepTypes 提供且非空时，是否强制校验 step type（含 step type 参数位）必须可解析。
        /// </summary>
        public bool RequireKnownStepTypes { get; init; }

        /// <summary>
        /// 已注册的原语名称集合（可包含别名，校验时会 canonicalize）。
        /// </summary>
        public ISet<string>? KnownStepTypes { get; init; }

        /// <summary>
        /// 是否禁止在待校验 workflow 中使用 dynamic_workflow（该原语保留给引擎内部重配置流程）。
        /// </summary>
        public bool DisallowDynamicWorkflowStep { get; init; }

        /// <summary>
        /// Publication/binding gate for the current workflow tool-catalog policy. Normal parsing
        /// leaves this disabled so already-committed v0 runs can still be replayed unchanged.
        /// </summary>
        public bool RequireExplicitLlmAgentToolScopes { get; init; }
    }

    private static IEnumerable<StepDefinition> EnumerateSteps(IEnumerable<StepDefinition> steps)
    {
        foreach (var step in steps)
        {
            yield return step;
            if (step.Children == null || step.Children.Count == 0)
                continue;

            foreach (var child in EnumerateSteps(step.Children))
                yield return child;
        }
    }
}

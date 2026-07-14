local devloop_base = require("devloop.base")
local C = {}

local strings = {
  en = {
    convergence_suffix = " - no three-angle consensus; narrowing",
    narrowed_question_label = "Narrowed question: ",
    angle_stances_label = "Angle stances:",
    verdict_summary_label = "Three-angle verdicts: ",
    comment_evidence_empty = "(review rounds are recorded on the parent PR comments)",
    thinking_started = "github-devloop thinking: consensus started",
    decision_prefix = "github-devloop decision: ",
    convergence_round_prefix = "github-devloop convergence round ",
    pr_review_convergence_round_prefix = "github-devloop PR review convergence round ",
    reconcile_action_prefix = "github-devloop reconcile action: ",
    fix_reconcile_action_prefix = "github-devloop fix reconcile action: ",
    review_reconcile_action_prefix = "github-devloop review reconcile action: ",
    reason_block_label = "Reason:",
    reason_inline_label = "Reason: ",
    no_reason_provided = "(no reason provided)",
    implementation_output_published = "github-devloop implementation output published",
    worktree_label = "Worktree: ",
    branch_label = "Branch: ",
    head_label = "Head: ",
    base_branch_label = "Base branch: ",
    base_head_label = "Base head: ",
    implementation_failed_prefix = "github-devloop implementation failed: ",
    no_implementation_output = "(no implementation output)",
    pr_ready_for_review = "github-devloop PR is ready for review",
    pr_review_decision_prefix = "github-devloop PR review decision: ",
    blocking_gap_label = "Blocking gap: ",
    merge_gate_failed_prefix = "github-devloop merge gate failed: ",
    reproduce_locally_prefix = "Reproduce locally with `",
    reproduce_locally_suffix = "` from the repository root.",
    fix_round_summary_label = "Fix-round summary: ",
    fix_pushed_for_rereview = "github-devloop fix pushed for re-review",
    previous_reviewed_head_label = "Previous reviewed head: ",
    new_head_label = "New head: ",
    current_head_label = "Current head: ",
    pr_head_advanced = "github-devloop PR head advanced after merge approval; re-entering review",
    fix_escalated_to_review_meta_prefix = "github-devloop fix escalated to review-meta: ",
    review_meta_action_prefix = "github-devloop review-meta action: ",
    fix_reflection_prefix = "github-devloop fix-loop reflection: ",
    dependency_hold_prefix = "github-devloop dependency hold: ",
    dependency_release_prefix = "github-devloop dependency release: ",
    intake_decision_prefix = "github-devloop intake decision: ",
    intake_tracking_ack = "Acknowledged as a tracking umbrella. Individual waves should enter the pipeline as separate issues; this issue stays open for tracking.",
    is_merging_pr_prefix = "github-devloop is merging PR #",
    merged_pr_prefix = "github-devloop merged PR #",
    no_fix_output = "(no fix output)",
    decomposed_prefix = "github-devloop decomposed blocked PR into ",
    decomposed_suffix = " follow-up issue(s)",
  },
  zh = {
    convergence_suffix = " - 三角共识未达成，正在收窄",
    narrowed_question_label = "收窄问题：",
    angle_stances_label = "角度立场：",
    verdict_summary_label = "三角结论：",
    comment_evidence_empty = "（复审轮次记录在父 PR 评论中）",
    thinking_started = "github-devloop 思考：共识已开始",
    decision_prefix = "github-devloop 决策：",
    convergence_round_prefix = "github-devloop 收敛轮次 ",
    pr_review_convergence_round_prefix = "github-devloop PR 复审收敛轮次 ",
    reconcile_action_prefix = "github-devloop 调和动作：",
    fix_reconcile_action_prefix = "github-devloop 修复调和动作：",
    review_reconcile_action_prefix = "github-devloop 复审调和动作：",
    reason_block_label = "原因：",
    reason_inline_label = "原因：",
    no_reason_provided = "（未提供原因）",
    implementation_output_published = "github-devloop 实现输出已发布",
    worktree_label = "工作树：",
    branch_label = "分支：",
    head_label = "头提交：",
    base_branch_label = "基准分支：",
    base_head_label = "基准头提交：",
    implementation_failed_prefix = "github-devloop 实现失败：",
    no_implementation_output = "（无实现输出）",
    pr_ready_for_review = "github-devloop PR 已可复审",
    pr_review_decision_prefix = "github-devloop PR 复审决策：",
    blocking_gap_label = "阻塞缺口：",
    merge_gate_failed_prefix = "github-devloop 合并门失败：",
    reproduce_locally_prefix = "请在仓库根目录用 `",
    reproduce_locally_suffix = "` 本地复现。",
    fix_round_summary_label = "修复轮次摘要：",
    fix_pushed_for_rereview = "github-devloop 修复已推送，等待再次复审",
    previous_reviewed_head_label = "上一轮复审头提交：",
    new_head_label = "新头提交：",
    current_head_label = "当前头提交：",
    pr_head_advanced = "github-devloop PR 头提交在合并批准后前进，重新进入复审",
    fix_escalated_to_review_meta_prefix = "github-devloop 修复升级到 review-meta：",
    review_meta_action_prefix = "github-devloop review-meta 动作：",
    fix_reflection_prefix = "github-devloop 修复循环反思：",
    dependency_hold_prefix = "github-devloop 依赖暂停：",
    dependency_release_prefix = "github-devloop 依赖释放：",
    intake_decision_prefix = "github-devloop 入口决策：",
    intake_tracking_ack = "已确认这是跟踪 umbrella。各个 wave 应作为独立 issue 进入管线；此 issue 保持打开用于跟踪。",
    is_merging_pr_prefix = "github-devloop 正在合并 PR #",
    merged_pr_prefix = "github-devloop 已合并 PR #",
    no_fix_output = "（无修复输出）",
    decomposed_prefix = "github-devloop 已将阻塞 PR 拆分为 ",
    decomposed_suffix = " 个后续 issue",
  },
}

local human_comment_keys = {
  "convergence_suffix",
  "narrowed_question_label",
  "angle_stances_label",
  "verdict_summary_label",
  "comment_evidence_empty",
  "thinking_started",
  "decision_prefix",
  "convergence_round_prefix",
  "pr_review_convergence_round_prefix",
  "reconcile_action_prefix",
  "fix_reconcile_action_prefix",
  "review_reconcile_action_prefix",
  "reason_block_label",
  "reason_inline_label",
  "no_reason_provided",
  "implementation_output_published",
  "worktree_label",
  "branch_label",
  "head_label",
  "base_branch_label",
  "base_head_label",
  "implementation_failed_prefix",
  "no_implementation_output",
  "pr_ready_for_review",
  "pr_review_decision_prefix",
  "blocking_gap_label",
  "merge_gate_failed_prefix",
  "reproduce_locally_prefix",
  "reproduce_locally_suffix",
  "fix_round_summary_label",
  "fix_pushed_for_rereview",
  "previous_reviewed_head_label",
  "new_head_label",
  "current_head_label",
  "pr_head_advanced",
  "fix_escalated_to_review_meta_prefix",
  "review_meta_action_prefix",
  "fix_reflection_prefix",
  "dependency_hold_prefix",
  "dependency_release_prefix",
  "intake_decision_prefix",
  "intake_tracking_ack",
  "is_merging_pr_prefix",
  "merged_pr_prefix",
  "no_fix_output",
  "decomposed_prefix",
  "decomposed_suffix",
}

local template_audit = {
  { id = "github-devloop-marker-comments", classification = "machine" },
  { id = "dedup-key-parts", classification = "machine" },
  { id = "state-labels", classification = "machine" },
  { id = "ai-sentinel", classification = "machine" },
  { id = "pr-title-and-body", classification = "repo-policy" },
}

for _, key in ipairs(human_comment_keys) do
  table.insert(template_audit, { id = key, classification = "human" })
end

local configured_output_lang = setmetatable({}, { __mode = "k" })

local function normalize_output_lang(value)
  local lang = tostring(value or ""):lower()
  if lang:match("^zh") then
    return "zh"
  end
  return "en"
end

function C.configure_output_lang(M, lang)
  configured_output_lang[M] = lang and normalize_output_lang(lang) or nil
end

function C.output_lang(M, exec)
  if configured_output_lang[M] ~= nil then
    return configured_output_lang[M]
  end
  local ok, value = pcall(function()
    return devloop_base.read_env("FKST_OUTPUT_LANG", exec)
  end)
  if not ok then
    return "en"
  end
  return normalize_output_lang(value)
end

function C.comment_string(M, key, exec)
  local lang = C.output_lang(M, exec)
  local lang_strings = strings[lang] or strings.en
  return lang_strings[key] or strings.en[key] or tostring(key)
end

function C.comment_strings(M, lang)
  local normalized = normalize_output_lang(lang)
  return strings[normalized] or strings.en
end

function C.comment_template_audit(M)
  local copy = {}
  for _, row in ipairs(template_audit) do
    table.insert(copy, {
      id = row.id,
      classification = row.classification,
    })
  end
  return copy
end

return C

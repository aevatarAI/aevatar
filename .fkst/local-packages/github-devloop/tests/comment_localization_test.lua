local devloop_base = require("devloop.base")
local requests_lifecycle = require("devloop.requests.lifecycle")
local convergence_shared = require("devloop.convergence.shared")
local comment_strings = require("devloop.strings")
local h = require("tests.devloop_core_helpers")
local payloads_builders = require("devloop.payloads.builders")
local conv_rounds = require("devloop.convergence.rounds")
local conv_reconcile = require("devloop.convergence.reconcile")
local m_facts = require("devloop.markers.facts")
local m_builders = require("devloop.markers.builders")
local core = h.core
local t = h.t

local source_ref = h.source_ref
local reached = h.reached
local unresolved = h.unresolved

local ai_sentinel = string.char(226, 159, 166) .. "AI:FKST" .. string.char(226, 159, 167)
local cjk_probe = string.char(228, 184, 173)

local issue_proposal_id = "github-devloop/issue/owner/repo/42"
local issue_version = "ready/consensus-github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z"
local review_proposal_id = devloop_base.pr_review_proposal_id("owner/repo", 7, issue_version, "def456")
local review_dedup_key = "consensus:" .. review_proposal_id .. "/review"

local function ready_payload()
  return payloads_builders.build_devloop_ready_payload(core, reached())
end

local function collect_markers(body)
  local markers = {}
  for marker in tostring(body or ""):gmatch("<!%-%- fkst:github%-devloop:.-%-%->") do
    table.insert(markers, marker)
  end
  return table.concat(markers, "\n")
end

local function strip_markers(body)
  return tostring(body or ""):gsub("<!%-%- fkst:github%-devloop:.-%-%->", "")
end

local function extract_machine(body)
  local machine = collect_markers(body)
  if tostring(body or ""):find(ai_sentinel, 1, true) ~= nil then
    machine = machine .. "\n" .. ai_sentinel
  end
  return machine
end

local function comment_cases()
  local ready = ready_payload()
  local reached_with_angles = reached({
    angle_results = {
      { angle = "minimal", verdict = "approve" },
      { angle = "structural", verdict = "abstain" },
      { angle = "delete", verdict = "approve" },
    },
  })
  local converge_marker = conv_rounds.converge_round_marker(core,
    issue_proposal_id,
    reached_with_angles.dedup_key,
    convergence_shared.source_ref_digest(source_ref()),
    2,
    reached_with_angles.dedup_key .. "/loop/2",
    "Narrow question?",
    { { angle = "minimal", verdict = "abstain", digest = "digest" } }
  )
  local reconcile = conv_reconcile.build_devloop_reconcile_payload(core, unresolved(), 3, reached_with_angles.dedup_key)
  local gate = { kind = "waiting", reason = "waiting-on-dependency" }
  local dependency_marker = core.dependency_wait_marker(issue_proposal_id, issue_version, { 7 }, gate.kind, gate.reason)
  local dependency_void_gate = {
    kind = "satisfied",
    reason = "dependency-void",
    notes = {
      { kind = "dependency-void", blocker_number = 7, reason = "not_planned" },
    },
  }
  return {
    { id = "thinking", request = requests_lifecycle.build_observe_comment_request(core, { repo = "owner/repo", number = 42, source_ref = source_ref() }, { proposal_id = issue_proposal_id, dedup_key = "v1" }) },
    { id = "result", request = requests_lifecycle.build_result_comment_request(core, "owner/repo", "42", reached_with_angles) },
    { id = "converge", request = requests_lifecycle.build_converge_round_comment_request(core, "owner/repo", "42", unresolved({
      narrowed_question = "Narrow question?",
      angle_digests = { { angle = "minimal", verdict = "abstain", digest = "digest" } },
    }), 2, converge_marker) },
    { id = "reconcile", request = core.build_reconcile_comment_request("owner/repo", "42", reconcile, "drop", "no-actionable-framing") },
    { id = "implementing", request = requests_lifecycle.build_implementing_comment_request(core, "owner/repo", "42", ready, "/tmp/worktree", "devloop-owner-repo-42", "abc123", "dev", "abc123") },
    { id = "impl-failure", request = requests_lifecycle.build_impl_failure_comment_request(core, "owner/repo", "42", ready, "no-changes", "") },
    { id = "dependency-hold", request = requests_lifecycle.build_dependency_hold_comment_request(core, "owner/repo", "42", issue_proposal_id, issue_version, gate, dependency_marker, source_ref()) },
    { id = "dependency-release", request = requests_lifecycle.build_dependency_release_comment_request(core, "owner/repo", "42", issue_proposal_id, issue_version, dependency_void_gate, source_ref()) },
  }
end

local audited_english_skeletons = {
  "github-devloop thinking: consensus started",
  "github-devloop decision: ",
  "Three-angle verdicts: ",
  "github-devloop convergence round ",
  "Narrowed question: ",
  "Angle stances:",
  "github-devloop reconcile action: ",
  "Reason:",
  "(no reason provided)",
  "github-devloop implementation output published",
  "Worktree: ",
  "Branch: ",
  "Head: ",
  "Base branch: ",
  "Base head: ",
  "github-devloop implementation failed: ",
  "(no implementation output)",
  "github-devloop dependency hold: ",
  "github-devloop dependency release: ",
  "Acknowledged as a tracking umbrella. Individual waves should enter the pipeline as separate issues; this issue stays open for tracking.",
}

local function render_cases(lang)
  comment_strings.configure_output_lang(core, lang)
  local rendered = comment_cases()
  comment_strings.configure_output_lang(core, nil)
  return rendered
end

local function body_of(case)
  return case.request.body
end

return {
  test_comment_template_audit_has_complete_language_table = function()
    local en = comment_strings.comment_strings(core, "en")
    local zh = comment_strings.comment_strings(core, "zh")
    local human = 0
    for _, row in ipairs(comment_strings.comment_template_audit(core)) do
      if row.classification == "human" then
        human = human + 1
        t.is_true(en[row.id] ~= nil)
        t.is_true(zh[row.id] ~= nil)
        t.eq(zh[row.id] ~= en[row.id], true)
      else
        t.is_true(row.classification == "machine" or row.classification == "repo-policy")
      end
    end
    t.is_true(human >= #audited_english_skeletons - 2)
  end,

  test_zh_comments_localize_human_skeletons_and_keep_machine_tokens = function()
    local en_cases = render_cases("en")
    local zh_cases = render_cases("zh")
    t.eq(#en_cases, #zh_cases)
    local localized_count = 0
    for index, en_case in ipairs(en_cases) do
      local zh_case = zh_cases[index]
      t.eq(zh_case.id, en_case.id)
      t.eq(zh_case.request.dedup_key, en_case.request.dedup_key)
      t.eq(extract_machine(body_of(zh_case)), extract_machine(body_of(en_case)))
      if strip_markers(body_of(zh_case)) ~= strip_markers(body_of(en_case)) then
        localized_count = localized_count + 1
      end
      for _, english in ipairs(audited_english_skeletons) do
        t.eq(strip_markers(body_of(zh_case)):find(english, 1, true), nil)
      end
    end
    t.eq(localized_count, #zh_cases)
  end,

  test_parsers_anchor_on_machine_tokens_not_prose = function()
    local issue_comments = {
      {
        body = "lorem ipsum " .. cjk_probe .. "\n"
          .. core.state_marker(issue_proposal_id, "ready", issue_version)
          .. "\n" .. m_builders.result_marker(core, issue_proposal_id, "approve", "consensus:v1")
          .. "\n" .. core.dependency_wait_marker(issue_proposal_id, issue_version, { 7 }),
        author_login = devloop_base.trusted_bot_login(),
      },
    }
    local review_comments = {
      {
        body = "noise only " .. cjk_probe .. "\n"
          .. core.state_marker(issue_proposal_id, "fixing", issue_version .. "/fix/1")
          .. "\n"
          .. m_builders.review_result_marker(core, review_proposal_id, issue_proposal_id, "reject", review_dedup_key, 1, "missing guard")
          .. "\n" .. m_builders.merge_ready_marker(core, issue_proposal_id, 7, issue_version, review_proposal_id, review_dedup_key, "def456")
          .. "\n" .. m_builders.review_meta_marker(core, issue_proposal_id, review_dedup_key, "fix", issue_version .. "/fix/1", "missing guard")
          .. "\n" .. m_builders.merge_gate_marker(core, issue_proposal_id, 7, issue_version .. "/fix/1", review_proposal_id, review_dedup_key, "def456", "abc123", "rollup-red"),
        author_login = devloop_base.trusted_bot_login(),
      },
    }
    local implementation_comments = {
      {
        body = "more noise " .. cjk_probe .. "\n"
          .. m_builders.implementing_marker(core, issue_proposal_id, "impl:v1", "devloop-owner-repo-42", "abc123", "dev", "abc123")
          .. "\n" .. m_builders.pr_link_marker(core, issue_proposal_id, 7, "devloop-owner-repo-42", "impl:v1", "dev")
          .. "\n" .. core.impl_failure_marker(issue_proposal_id, "impl:v1", "codex-failed"),
        author_login = devloop_base.trusted_bot_login(),
      },
    }

    t.eq(core.current_state(issue_comments, issue_proposal_id).state, "ready")
    t.eq(core.has_result_marker(issue_comments, issue_proposal_id, "approve", "consensus:v1"), true)
    t.eq(core.dependency_hold_fact(issue_comments, issue_proposal_id).marker_kind, "dependency-wait")
    t.eq(core.dependency_waiver_fact({
      {
        body = "noise " .. cjk_probe .. "\n"
          .. core.dependency_waiver_marker(issue_proposal_id, issue_version, 7, "operator-waiver"),
        author_login = devloop_base.trusted_bot_login(),
      },
    }, issue_proposal_id, issue_version, 7).reason, "operator-waiver")
    t.eq(m_facts.review_reject_fact(core, review_comments, issue_proposal_id, issue_version .. "/fix/1").blocking_gap, "missing guard")
    t.eq(m_facts.review_meta_fix_fact(core, review_comments, issue_proposal_id, issue_version .. "/fix/1").blocking_gap, "missing guard")
    t.eq(m_facts.merge_gate_fix_fact(core, review_comments, issue_proposal_id, issue_version .. "/fix/1").reviewed_head_sha, "def456")
    t.eq(m_facts.merge_gate_fix_fact(core, review_comments, issue_proposal_id, issue_version .. "/fix/1").gate_baseline_sha, "abc123")
    t.eq(m_facts.merge_ready_fact(core, review_comments, issue_proposal_id, issue_version, 7).head_sha, "def456")
    t.eq(m_facts.implementing_fact(core, implementation_comments, issue_proposal_id, "impl:v1").branch, "devloop-owner-repo-42")
    t.eq(m_facts.pr_link_fact(core, implementation_comments, issue_proposal_id).pr_number, 7)
    t.eq(core.has_impl_failure_marker(implementation_comments, issue_proposal_id, "impl:v1"), true)
  end,
}

local convergence_shared = require("devloop.convergence.shared")
local h = require("tests.devloop_helpers")
local conv_rounds = require("devloop.convergence.rounds")
local conv_reconcile = require("devloop.convergence.reconcile")
local t = h.t
local core = h.core
local opts = h.opts
local unresolved = h.unresolved
local run_loop = h.run_loop
local mock_issue_loop = h.mock_issue_loop
local find_raise = h.find_raise
local config = require("devloop.config")

local function run_comment_handoff_from_request(request, comment_id, name)
  return t.run_department("departments/comment_handoff/main.lua", {
    queue = "github-proxy.github_comment_written",
    payload = {
      schema = "github-proxy.comment-written.v1",
      repo = request.repo,
      target = "issue",
      issue_number = request.issue_number,
      comment_id = comment_id,
      request_dedup_key = request.dedup_key,
      dedup_key = tostring(request.dedup_key) .. "/written/" .. tostring(comment_id),
      source_ref = request.source_ref,
      handoff = request.handoff,
    },
  }, opts(name))
end

return {
  test_loop_round_cap_uses_proposal_budget_when_version_and_source_ref_drift = function()
    local cap = config.max_converge_rounds(core)
    local base_version = "consensus:github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z"
    local drift_version = base_version .. "/drifted"
    local event = unresolved({
      dedup_key = base_version .. "/loop/6",
      round = 6,
      narrowed_question = "Question 6 current lineage",
      angle_digests = {
        { angle = "minimal", verdict = "abstain", digest = "digest-6" },
      },
    })
    local current_digest = convergence_shared.source_ref_digest(event.source_ref)
    local drift_digest = convergence_shared.source_ref_digest({ kind = "external", ref = "owner/repo#issue/42?drift=1" })
    mock_issue_loop({ "fkst-dev:thinking" }, {
      conv_rounds.converge_round_marker(core, event.proposal_id, drift_version, drift_digest, cap, drift_version .. "/loop/" .. tostring(cap), event.narrowed_question, event.angle_digests),
    })

    local result = run_loop(event, opts("loop-budget-drift-cap"))
    t.eq(result.exit_code, 0)
    t.eq(#result.raises, 1)
    t.eq(find_raise(result.raises, "consensus.proposal"), nil)
    t.eq(result.raises[1].queue, "github-proxy.github_issue_comment_request")
    t.is_true(result.raises[1].payload.body:find('round="' .. tostring(cap) .. '"', 1, true) ~= nil)
    t.eq(find_raise(result.raises, "devloop_reconcile"), nil)
    t.eq(result.raises[1].payload.handoff.kind, "github-devloop.reconcile")
    t.eq(result.raises[1].payload.handoff.round, cap)
    t.eq(result.raises[1].payload.handoff.base_version, base_version)
  end,

  test_loop_round_cap_uses_stable_proposal_facts_for_current_round_when_key_drifts = function()
    local cap = config.max_converge_rounds(core)
    local old_base = "consensus:github-devloop/issue/owner/repo/42/intake/old"
    local current_base = "consensus:github-devloop/issue/owner/repo/42/intake/current"
    local event = unresolved({
      dedup_key = current_base .. "/loop/6",
      round = 6,
      source_ref = { kind = "external", ref = "owner/repo#issue/42?current=1" },
      narrowed_question = "Question 6 current lineage",
      angle_digests = {
        { angle = "minimal", verdict = "abstain", digest = "digest-6" },
      },
    })
    local old_digest = convergence_shared.source_ref_digest({ kind = "external", ref = "owner/repo#issue/42?old=1" })
    mock_issue_loop({ "fkst-dev:thinking" }, {
      conv_rounds.converge_round_marker(core, event.proposal_id, old_base, old_digest, cap, old_base .. "/loop/" .. tostring(cap), event.narrowed_question, event.angle_digests),
    })

    local result = run_loop(event, opts("loop-stable-proposal-facts-cap"))
    t.eq(result.exit_code, 0)
    t.eq(#result.raises, 1)
    t.eq(find_raise(result.raises, "consensus.proposal"), nil)
    t.eq(result.raises[1].queue, "github-proxy.github_issue_comment_request")
    t.is_true(result.raises[1].payload.body:find('round="' .. tostring(cap) .. '"', 1, true) ~= nil)
    t.eq(find_raise(result.raises, "devloop_reconcile"), nil)
    t.eq(result.raises[1].payload.handoff.kind, "github-devloop.reconcile")
    t.eq(result.raises[1].payload.handoff.round, cap)
    t.eq(result.raises[1].payload.handoff.base_version, current_base)
  end,

  test_loop_true_stall_reconcile_runs_after_comment_handoff_ack = function()
    local cap = config.max_converge_rounds(core)
    local base_version = "consensus:github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z"
    local event = unresolved({
      dedup_key = base_version .. "/loop/" .. tostring(cap),
      round = cap,
      narrowed_question = "Same unresolved boundary",
      angle_digests = {
        { angle = "minimal", verdict = "abstain", digest = "same-digest" },
      },
    })
    mock_issue_loop({ "fkst-dev:thinking" }, {
      core.state_marker(event.proposal_id, "thinking", base_version),
    })

    local result = run_loop(event, opts("loop-round-cap-comment-handoff-reconcile"))
    t.eq(result.exit_code, 0)
    t.eq(find_raise(result.raises, "devloop_reconcile"), nil)
    local comment = find_raise(result.raises, "github-proxy.github_issue_comment_request")
    t.is_true(comment ~= nil)
    t.eq(comment.payload.handoff.kind, "github-devloop.reconcile")
    t.eq(comment.payload.handoff.proposal_id, event.proposal_id)
    t.eq(comment.payload.handoff.round, cap)
    t.eq(comment.payload.handoff.base_version, base_version)
    t.eq(comment.payload.handoff.source_ref.ref, event.source_ref.ref)

    local handoff = run_comment_handoff_from_request(
      comment.payload,
      "IC_converge_reconcile",
      "loop-round-cap-comment-handoff-reconcile-ack"
    )
    t.eq(handoff.exit_code, 0)
    t.eq(#handoff.raises, 1)
    local reconcile_raise = find_raise(handoff.raises, "devloop_reconcile")
    t.is_true(reconcile_raise ~= nil)
    local expected = conv_reconcile.build_devloop_reconcile_payload(core, event, cap, base_version)
    t.eq(reconcile_raise.payload.schema, expected.schema)
    t.eq(reconcile_raise.payload.proposal_id, expected.proposal_id)
    t.eq(reconcile_raise.payload.dedup_key, expected.dedup_key)
    t.eq(reconcile_raise.payload.round, expected.round)
    t.eq(reconcile_raise.payload.base_version, expected.base_version)
    t.eq(reconcile_raise.payload.source_ref.kind, expected.source_ref.kind)
    t.eq(reconcile_raise.payload.source_ref.ref, expected.source_ref.ref)
    t.eq(conv_reconcile.is_supported_reconcile(core, reconcile_raise.payload), true)
  end,

  test_loop_round_cap_preserves_question_verdict_boundary_when_key_drifts = function()
    local cap = config.max_converge_rounds(core)
    local old_base = "consensus:github-devloop/issue/owner/repo/42/intake/old"
    local current_base = "consensus:github-devloop/issue/owner/repo/42/intake/current"
    local event = unresolved({
      dedup_key = current_base .. "/loop/6",
      round = 6,
      source_ref = { kind = "external", ref = "owner/repo#issue/42?current=1" },
      narrowed_question = "Current question boundary",
      angle_digests = {
        { angle = "minimal", verdict = "abstain", digest = "current-digest" },
      },
    })
    local old_digest = convergence_shared.source_ref_digest({ kind = "external", ref = "owner/repo#issue/42?old=1" })
    mock_issue_loop({ "fkst-dev:thinking" }, {
      conv_rounds.converge_round_marker(core, event.proposal_id, old_base, old_digest, cap, old_base .. "/loop/" .. tostring(cap), "Unrelated old question", {
        { angle = "minimal", verdict = "approve", digest = "old-digest" },
      }),
    })

    local result = run_loop(event, opts("loop-boundary-preserved-across-proposal-facts"))
    t.eq(result.exit_code, 0)
    t.eq(#result.raises, 2)
    t.eq(result.raises[1].queue, "consensus.proposal")
    t.eq(result.raises[1].payload.dedup_key, "github-devloop/issue/owner/repo/42/intake/current/loop/7")
    t.eq(find_raise(result.raises, "devloop_reconcile"), nil)
    t.eq(result.raises[2].queue, "github-proxy.github_issue_comment_request")
    t.is_nil(result.raises[2].payload.handoff)
    t.is_true(result.raises[2].payload.body:find('round="6"', 1, true) ~= nil)

    local handoff = run_comment_handoff_from_request(
      result.raises[2].payload,
      "IC_normal_converge_round",
      "loop-normal-converge-comment-handoff-skip"
    )
    t.eq(handoff.exit_code, 0)
    t.eq(find_raise(handoff.raises, "devloop_reconcile"), nil)
  end,
}

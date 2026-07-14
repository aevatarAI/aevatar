local parsers_misc = require("devloop.parsers.misc")
local h = require("tests.devloop_helpers")
local t = h.t
local core = h.core

local function pr(extra)
  local value = {
    state = "OPEN",
    head_sha = "def456",
    head_ref_name = "integration/dev",
    base_ref_name = "dev",
    head_repository = "owner/repo",
    is_cross_repository = false,
    mergeable = "MERGEABLE",
    merge_state_status = "CLEAN",
    status_check_rollup = {
      { name = "ci", state = "COMPLETED", conclusion = "SUCCESS" },
    },
  }
  for key, field in pairs(extra or {}) do
    value[key] = field
  end
  return value
end

local expected = {
  repo = "owner/repo",
  head_sha = "def456",
  head_branch = "integration/dev",
  base_branch = "dev",
}

local function mock_check_runs(json)
  t.mock_command("gh api 'repos/owner/repo/commits/def456/check-runs'", {
    stdout = json,
    stderr = "",
    exit_code = 0,
  })
end

return {
  test_pr_identity_matches_true = function()
    local ok, reason = core.pr_identity_matches(pr(), expected)
    t.eq(ok, true)
    t.eq(reason, "pr-ok")
  end,

  test_pr_identity_matches_false_cases = function()
    local ok, reason = core.pr_identity_matches(pr({ state = "MERGED" }), expected)
    t.eq(ok, false)
    t.eq(reason, "pr-not-open")

    ok, reason = core.pr_identity_matches(pr({ head_sha = "aaaa1111" }), expected)
    t.eq(ok, false)
    t.eq(reason, "head-sha-mismatch")

    ok, reason = core.pr_identity_matches(pr({ head_ref_name = "feature/x" }), expected)
    t.eq(ok, false)
    t.eq(reason, "head-branch-mismatch")

    ok, reason = core.pr_identity_matches(pr({ base_ref_name = "main" }), expected)
    t.eq(ok, false)
    t.eq(reason, "base-branch-mismatch")

    ok, reason = core.pr_identity_matches(pr({ is_cross_repository = true }), expected)
    t.eq(ok, false)
    t.eq(reason, "foreign-head-repository")
  end,

  test_evaluate_ci_merge_gate_true = function()
    local ok, reason = core.evaluate_ci_merge_gate(pr())
    t.eq(ok, true)
    t.eq(reason, "merge-gate-ok")
  end,

  test_evaluate_ci_merge_gate_false_cases = function()
    local ok, reason = core.evaluate_ci_merge_gate(pr({
      status_check_rollup = {
        { name = "ci", state = "COMPLETED", conclusion = "FAILURE" },
      },
    }))
    t.eq(ok, false)
    t.eq(reason, "ci-unknown")

    ok, reason = core.evaluate_ci_merge_gate(pr({
      status_check_rollup = {
        { name = "ci", state = "COMPLETED", conclusion = "NEUTRAL" },
      },
    }))
    t.eq(ok, false)
    t.eq(reason, "ci-unknown")

    ok, reason = core.evaluate_ci_merge_gate(pr({ mergeable = "CONFLICTING" }))
    t.eq(ok, false)
    t.eq(reason, "mergeable-conflicting")
  end,

  test_merge_gate_reason_class_controls_pr_merge_ref_verification = function()
    t.eq(core.merge_gate_reason_requires_pr_merge_product("rollup-red"), false)
    t.eq(core.merge_gate_reason_requires_pr_merge_product("rollup-red: test: COMPLETED/FAILURE"), false)
    t.eq(core.merge_gate_reason_class("merge-state-unstable-with-failing-checks"), "ci-wait")
    t.eq(core.merge_gate_reason_requires_pr_merge_product("merge-state-unstable-with-failing-checks"), false)
    t.eq(core.merge_gate_reason_requires_pr_merge_product("mergeable-conflicting"), false)
    t.eq(core.merge_gate_reason_requires_pr_merge_product("rollup-pending"), false)
    t.eq(core.merge_gate_reason_class("own-ci-red"), "own-ci-red")
    t.eq(core.merge_gate_reason_class("rollup-red"), "ci-wait")
    t.eq(core.merge_gate_reason_class("mergeable-conflicting"), "mergeable-conflicting")
  end,

  test_unstable_with_completed_failure_routes_to_ci_red = function()
    local ok, reason = core.evaluate_ci_merge_gate(pr({
      merge_state_status = "UNSTABLE",
      status_check_rollup = {
        { name = "verify", state = "COMPLETED", conclusion = "FAILURE" },
      },
    }))
    t.eq(ok, false)
    t.eq(reason, "ci-unknown")
  end,

  test_unstable_with_pending_check_remains_transient_wait = function()
    local ok, reason = core.evaluate_ci_merge_gate(pr({
      merge_state_status = "UNSTABLE",
      status_check_rollup = {
        { name = "verify", state = "IN_PROGRESS", conclusion = "" },
      },
    }))
    t.eq(ok, false)
    t.eq(reason, "rollup-pending")
  end,

  test_unstable_without_rollup_remains_merge_state_wait = function()
    local ok, reason = core.evaluate_ci_merge_gate(pr({
      merge_state_status = "UNSTABLE",
      status_check_rollup = {},
    }), {
      repo = "owner/repo",
    })
    t.eq(ok, false)
    t.eq(reason, "merge-state-unstable")
    t.eq(#t.command_calls(), 0)
  end,

  test_rollup_failure_gate_sha_comes_from_failed_checks = function()
    local sha = "abc123"
    t.eq(parsers_misc.rollup_failure_gate_sha(core, pr({
      base_ref_oid = "base999",
      status_check_rollup = {
        { name = "test", state = "COMPLETED", conclusion = "FAILURE", headSha = sha },
        { name = "docs", state = "COMPLETED", conclusion = "SUCCESS", headSha = "docs999" },
      },
    })), sha)
    t.eq(parsers_misc.rollup_failure_gate_sha(core, pr({
      base_ref_oid = "base999",
      status_check_rollup = {
        { name = "test", state = "COMPLETED", conclusion = "FAILURE" },
      },
    })), nil)
    t.eq(parsers_misc.rollup_failure_gate_sha(core, pr({
      status_check_rollup = {
        { name = "test", state = "COMPLETED", conclusion = "FAILURE", headSha = "abc123" },
        { name = "lint", state = "COMPLETED", conclusion = "FAILURE", headSha = "def456" },
      },
    })), nil)
  end,

  test_empty_rollup_falls_back_to_required_commit_check_run_green = function()
    mock_check_runs('{"total_count":2,"check_runs":[{"name":"unrelated","status":"completed","conclusion":"success"},{"name":"test","status":"completed","conclusion":"success"}]}\n')
    local ok, reason = core.evaluate_ci_status_gate(pr({ status_check_rollup = {} }), {
      repo = "owner/repo",
      proposal_id = "github-devloop/issue/owner/repo/42",
    })
    t.eq(ok, true)
    t.eq(reason, "rollup-green")
  end,

  test_empty_rollup_fallback_red_required_commit_check_run = function()
    mock_check_runs('{"total_count":1,"check_runs":[{"name":"test","status":"completed","conclusion":"failure"}]}\n')
    local ok, reason = core.evaluate_ci_status_gate(pr({ status_check_rollup = {} }), {
      repo = "owner/repo",
    })
    t.eq(ok, false)
    t.eq(reason, "rollup-red")
  end,

  test_rollup_red_green_required_head_checks_is_external_ci_red = function()
    mock_check_runs('{"total_count":1,"check_runs":[{"name":"test","status":"completed","conclusion":"success","head_sha":"def456"}]}\n')
    local classification = core.classify_pr_ci_gate(pr({
      status_check_rollup = {
        { name = "shared-integration", state = "COMPLETED", conclusion = "FAILURE" },
      },
    }), {
      repo = "owner/repo",
      proposal_id = "github-devloop/issue/owner/repo/42",
    })
    t.eq(classification.kind, "EXTERNAL_CI_RED")
    t.eq(classification.merge_blocking, true)
    t.eq(classification.actionable, false)
    t.eq(classification.reason, "external-ci-red")
  end,

  test_rollup_red_red_required_head_check_is_own_ci_red = function()
    mock_check_runs('{"total_count":1,"check_runs":[{"name":"test","status":"completed","conclusion":"failure","head_sha":"def456"}]}\n')
    local classification = core.classify_pr_ci_gate(pr({
      status_check_rollup = {
        { name = "shared-integration", state = "COMPLETED", conclusion = "FAILURE" },
      },
    }), {
      repo = "owner/repo",
      proposal_id = "github-devloop/issue/owner/repo/42",
    })
    t.eq(classification.kind, "OWN_CI_RED")
    t.eq(classification.merge_blocking, true)
    t.eq(classification.actionable, true)
    t.eq(classification.reason, "own-ci-red")
  end,

  test_empty_rollup_fallback_pending_required_commit_check_run = function()
    mock_check_runs('{"total_count":1,"check_runs":[{"name":"test","status":"in_progress","conclusion":null}]}\n')
    local ok, reason = core.evaluate_ci_status_gate(pr({ status_check_rollup = {} }), {
      repo = "owner/repo",
    })
    t.eq(ok, false)
    t.eq(reason, "rollup-pending")
  end,

  test_empty_rollup_fallback_absent_commit_check_runs_stays_missing = function()
    mock_check_runs('{"total_count":0,"check_runs":[]}\n')
    local ok, reason = core.evaluate_ci_status_gate(pr({ status_check_rollup = {} }), {
      repo = "owner/repo",
    })
    t.eq(ok, false)
    t.eq(reason, "missing-status-rollup")
  end,

  test_empty_rollup_fallback_missing_required_check_stays_missing = function()
    mock_check_runs('{"total_count":1,"check_runs":[{"name":"docs","status":"completed","conclusion":"success"}]}\n')
    local ok, reason = core.evaluate_ci_status_gate(pr({ status_check_rollup = {} }), {
      repo = "owner/repo",
    })
    t.eq(ok, false)
    t.eq(reason, "missing-status-rollup")
  end,

  test_not_mergeable_pr_does_not_wait_on_missing_status = function()
    local ok, reason = core.evaluate_ci_merge_gate(pr({
      merge_state_status = "DIRTY",
      status_check_rollup = {},
    }), {
      repo = "owner/repo",
    })
    t.eq(ok, false)
    t.eq(reason, "merge-state-dirty")
    t.eq(#t.command_calls(), 0)
  end,

  test_missing_status_dispatch_eligibility_uses_first_observed_time = function()
    local eligible, reason, age = core.ci_missing_status_dispatch_eligible(pr({
      status_check_rollup = {},
    }), 600, 240, 300)
    t.eq(eligible, true)
    t.eq(reason, "missing-status-rollup")
    t.eq(age, 360)

    eligible, reason = core.ci_missing_status_dispatch_eligible(pr({
      status_check_rollup = {},
      updated_at = "2026-06-03T02:00:00Z",
    }), 600, 420, 300)
    t.eq(eligible, false)
    t.eq(reason, "missing-status-grace")

    eligible, reason = core.ci_missing_status_dispatch_eligible(pr({
      status_check_rollup = {
        { name = "ci", state = "IN_PROGRESS", conclusion = "" },
      },
    }), 600, 240, 300)
    t.eq(eligible, false)
    t.eq(reason, "rollup-pending")
  end,

  test_rerunnable_check_run_ids_for_head_are_deduplicated_and_head_bound = function()
    local ids = core.rerunnable_check_run_ids_for_head({
      { id = 101, head_sha = "def456" },
      { id = 101, head_sha = "def456" },
      { id = 202, check_suite = { head_sha = "def456" } },
      { id = 303, head_sha = "abc123" },
      { id = "not-numeric", head_sha = "def456" },
    }, "def456")
    t.eq(table.concat(ids, ","), "101,202")
  end,
}

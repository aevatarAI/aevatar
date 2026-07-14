local devloop_base = require("devloop.base")
local entity_lib = require("devloop.entity")
local h = require("tests.devloop_helpers")
local conv_attempts = require("devloop.convergence.attempts")
local t = h.t
local core = h.core
local opts = h.opts
local entity_read_mocks = require("tests.entity_read_mock_helpers")
local codex_status = require("tests.codex_status_helpers")
local decompose_lib = require("devloop.decompose")
local m_builders = require("devloop.markers.builders")

local repo = "owner/repo"
local proposal_id = "github-devloop/issue/owner/repo/42"
local version = "ready/consensus-github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z"

local function encode_json_string(value)
  return h.encode_json_string(value)
end

local function render_comment(comment)
  return h.render_comment(comment)
end

local function mock_repo()
  t.mock_command(devloop_base.read_env_command("FKST_GITHUB_REPO"), {
    stdout = repo,
    stderr = "",
    exit_code = 0,
  })
end

local function mock_issue_list(updated_at)
  t.mock_command(core.gh_issue_list_observe_cmd(repo), {
    stdout = '[{"number":42,"state":"open","updated_at":"' .. encode_json_string(updated_at or "2026-06-03T01:02:03Z") .. '"}]\n',
    stderr = "",
    exit_code = 0,
  })
end

local function mock_empty_pr_list()
  t.mock_command(core.gh_pr_list_observe_cmd(repo), {
    stdout = "[]\n",
    stderr = "",
    exit_code = 0,
  })
end

local function mock_issue_state(labels, comments, updated_at)
  entity_read_mocks.mock_issue_read_forms(t, {
    repo = repo,
    number = 42,
    title = "Issue 42",
    body = "",
    state = "OPEN",
    updated_at = updated_at or "2026-06-03T01:02:03Z",
    labels = labels,
    comments = comments,
    assignees = { "fkst-test-bot" },
    times = 1,
  })
end

local function mock_decompose_children()
  t.mock_command(core.gh_issue_list_decompose_children_cmd(repo, proposal_id), {
    stdout = "[]\n",
    stderr = "",
    exit_code = 0,
  })
end

local function run_liveness_scan(name, run_opts)
  return t.run_department("departments/liveness_scan/main.lua", {
    queue = "devloop_liveness_tick",
    payload = { schema = "github-devloop.tick.v1" },
    ts = "2026-06-03T01:32:03Z",
  }, run_opts or opts(name or "liveness-timeout-attempt"))
end

local function find_raise(result, queue)
  return h.find_raise(result.raises, queue)
end

local function count_raises(result, queue)
  local count = 0
  for _, raised in ipairs(result.raises or {}) do
    if raised.queue == queue then
      count = count + 1
    end
  end
  return count
end

local function state_comment(state_name, state_version, created_at)
  return {
    body = core.state_marker(proposal_id, state_name, state_version),
    author_login = "fkst-test-bot",
    created_at = created_at or "2026-06-03T00:00:00Z",
  }
end

local function issue_comment(body, created_at)
  return {
    body = body,
    author_login = "fkst-test-bot",
    created_at = created_at or "2026-06-03T00:00:00Z",
  }
end

local function assert_no_timeout_progress(result)
  t.eq(find_raise(result, "devloop_timeout_reconcile"), nil)
  t.eq(find_raise(result, "devloop_ready"), nil)
  local comment = find_raise(result, "github-proxy.github_issue_comment_request")
  t.eq(comment, nil)
end

return {
  test_timeout_attempt_not_counted_while_implement_codex_run_live = function()
    local event = h.ready()
    local live_opts = opts("liveness-live-implement-codex-run-no-timeout-count")
    local live_exec_ref = core.implement_exec_ref(event.proposal_id, event.dedup_key)
    codex_status.seed_implement_codex_run(live_opts, event.proposal_id, event.dedup_key)
    local comments = {
      state_comment("implementing", event.dedup_key, "2026-06-03T00:00:00Z"),
      issue_comment(core.implement_attempt_marker(event.proposal_id, event.dedup_key, 1, tostring(now() - 7201), live_exec_ref)),
    }
    mock_repo()
    mock_issue_list()
    mock_issue_state({ "fkst-dev:enabled", "fkst-dev:implementing" }, comments)
    mock_empty_pr_list()

    local scanned = run_liveness_scan("liveness-live-implement-codex-run-no-timeout-count", live_opts)
    t.eq(scanned.exit_code, 0)
    assert_no_timeout_progress(scanned)

    local dead_opts = opts("liveness-absent-implement-codex-run-counts")
    local dead_exec_ref = core.implement_exec_ref(event.proposal_id, event.dedup_key)
    local expired = {
      state_comment("implementing", event.dedup_key, "2026-06-03T00:00:00Z"),
      issue_comment(core.implement_attempt_marker(event.proposal_id, event.dedup_key, 1, tostring(now() - 60), dead_exec_ref)),
    }
    mock_repo()
    mock_issue_list("2026-06-03T01:02:04Z")
    mock_issue_state({ "fkst-dev:enabled", "fkst-dev:implementing" }, expired, "2026-06-03T01:02:04Z")
    mock_empty_pr_list()

    local redriven = run_liveness_scan("liveness-absent-implement-codex-run-counts", dead_opts)
    t.eq(redriven.exit_code, 0)
    t.eq(find_raise(redriven, "devloop_timeout_reconcile"), nil)
    t.eq(find_raise(redriven, "devloop_ready") ~= nil, true)
    local attempt = find_raise(redriven, "github-proxy.github_issue_comment_request")
    t.is_true(attempt ~= nil)
    t.is_true(attempt.payload.body:find("fkst:github-devloop:timeout-attempt:v2", 1, true) ~= nil)
    t.is_true(attempt.payload.body:find("fkst:github-devloop:timeout-attempt:latest:v1", 1, true) ~= nil)
    t.is_true(tostring(attempt.payload.replace_marker):find("fkst:github-devloop:timeout-attempt:latest:v1", 1, true) ~= nil)
    t.is_true(attempt.payload.body:find('state="implementing"', 1, true) ~= nil)
  end,

  test_timeout_attempt_not_counted_after_implement_delegates_to_pr_child = function()
    local event = h.ready()
    local run_opts = opts("liveness-delegated-implement-codex-run-no-timeout-count")
    local exec_ref = core.implement_exec_ref(event.proposal_id, event.dedup_key)
    local pr_proposal = entity_lib.pr_proposal_id(repo, 7)
    codex_status.seed_implement_codex_run(run_opts, event.proposal_id, event.dedup_key)
    local comments = {
      state_comment("implementing", event.dedup_key, "2026-06-03T00:00:00Z"),
      issue_comment(core.implement_attempt_marker(event.proposal_id, event.dedup_key, 1, tostring(now() - 60), exec_ref)),
      issue_comment(m_builders.pr_delegation_marker(core, event.proposal_id, pr_proposal, 7, event.dedup_key, "g1")),
    }
    mock_repo()
    mock_issue_list("2026-06-03T01:02:05Z")
    mock_issue_state({ "fkst-dev:enabled", "fkst-dev:implementing" }, comments, "2026-06-03T01:02:05Z")
    mock_empty_pr_list()

    local scanned = run_liveness_scan("liveness-delegated-implement-codex-run-no-timeout-count", run_opts)
    t.eq(scanned.exit_code, 0)
    assert_no_timeout_progress(scanned)
  end,

  test_blocked_decompose_exhaustion_reaches_non_recycling_terminal_stop = function()
    local review_proposal = devloop_base.pr_review_proposal_id(repo, 7, version, "def456")
    local comments = {
      state_comment("blocked", version, "2026-06-01T00:00:00Z"),
      m_builders.pr_link_marker(core, proposal_id, 7, "devloop-owner-repo-42-01HY", version, "dev"),
      decompose_lib.decomposed_marker(core, proposal_id, version, 7, 1),
      m_builders.review_result_marker(core, review_proposal, proposal_id, "reject", "consensus:" .. review_proposal .. "/review", 1, "missing decomposition"),
      issue_comment(conv_attempts.timeout_attempt_marker(core, proposal_id, version .. "/timeout-reconcile/blocked/1", "blocked", 1, entity_lib.issue_source_ref(repo, 42))),
      issue_comment(conv_attempts.timeout_attempt_marker(core, proposal_id, version .. "/timeout-reconcile/blocked/2", "blocked", 2, entity_lib.issue_source_ref(repo, 42))),
    }
    mock_repo()
    mock_issue_list()
    mock_issue_state({ "fkst-dev:enabled", "fkst-dev:blocked" }, comments)
    mock_decompose_children()
    mock_empty_pr_list()

    local exhausted = run_liveness_scan("liveness-blocked-decompose-exhausted")
    t.eq(exhausted.exit_code, 0)
    t.eq(find_raise(exhausted, "github-devloop-decompose.devloop_decompose"), nil)
    t.eq(find_raise(exhausted, "devloop_timeout_reconcile"), nil)
    t.eq(count_raises(exhausted, "github-proxy.github_issue_comment_request"), 1)
    local stop = find_raise(exhausted, "github-proxy.github_issue_comment_request")
    t.is_true(stop.payload.body:find(conv_attempts.decompose_exhausted_marker(core, proposal_id, version, 3, entity_lib.issue_source_ref(repo, 42)), 1, true) ~= nil)

    table.insert(comments, issue_comment(conv_attempts.decompose_exhausted_marker(core, proposal_id, version .. "/timeout-reconcile/blocked/3", 3, entity_lib.issue_source_ref(repo, 42))))
    mock_repo()
    mock_issue_list("2026-06-04T01:02:03Z")
    mock_issue_state({ "fkst-dev:enabled", "fkst-dev:blocked" }, comments, "2026-06-04T01:02:03Z")
    mock_empty_pr_list()

    local second = run_liveness_scan("liveness-blocked-decompose-exhausted-second-window")
    t.eq(second.exit_code, 0)
    t.eq(#second.raises, 0)
  end,

  test_impl_failed_retry_limit_replay_decline_climbs_to_timeout_reconcile_without_seeded_timeout_markers = function()
    local event = h.ready()
    local comments = {
      state_comment("impl-failed", event.dedup_key, "2026-06-01T00:00:00Z"),
      issue_comment(core.impl_failure_marker(event.proposal_id, event.dedup_key, "codex-failed", core._max_impl_auto_retry_attempts)),
    }

    for sweep = 1, 3 do
      mock_repo()
      mock_issue_list("2026-06-03T01:02:0" .. tostring(sweep) .. "Z")
      mock_issue_state({ "fkst-dev:enabled", "fkst-dev:impl-failed" }, comments, "2026-06-03T01:02:0" .. tostring(sweep) .. "Z")
      mock_empty_pr_list()

      local result = run_liveness_scan("liveness-impl-failed-retry-limit-stuck-sweep-" .. tostring(sweep))
      t.eq(result.exit_code, 0)
      t.eq(find_raise(result, "devloop_ready"), nil)
      if sweep < 3 then
        t.eq(find_raise(result, "devloop_timeout_reconcile"), nil)
        local attempt = find_raise(result, "github-proxy.github_issue_comment_request")
        t.is_true(attempt ~= nil)
        t.is_true(attempt.payload.body:find(conv_attempts.timeout_attempt_marker(core, event.proposal_id, event.dedup_key, "impl-failed", sweep, entity_lib.issue_source_ref(repo, 42)), 1, true) ~= nil)
        table.insert(comments, issue_comment(conv_attempts.timeout_attempt_marker(core, event.proposal_id, event.dedup_key, "impl-failed", sweep, entity_lib.issue_source_ref(repo, 42))))
      else
        t.eq(find_raise(result, "github-proxy.github_issue_comment_request"), nil)
        local reconcile = find_raise(result, "devloop_timeout_reconcile")
        t.is_true(reconcile ~= nil)
        t.eq(reconcile.payload.state, "impl-failed")
        t.eq(reconcile.payload.issue_version, event.dedup_key)
        t.eq(reconcile.payload.round, 3)
      end
    end
  end,

  test_blocked_missing_decomposed_replay_decline_climbs_to_decompose_exhausted_without_seeded_timeout_markers = function()
    local comments = {
      state_comment("blocked", version, "2026-06-01T00:00:00Z"),
      m_builders.pr_link_marker(core, proposal_id, 7, "devloop-owner-repo-42-01HY", version, "dev"),
    }

    for sweep = 1, 3 do
      mock_repo()
      mock_issue_list("2026-06-03T01:03:0" .. tostring(sweep) .. "Z")
      mock_issue_state({ "fkst-dev:enabled", "fkst-dev:blocked" }, comments, "2026-06-03T01:03:0" .. tostring(sweep) .. "Z")
      mock_decompose_children()
      mock_empty_pr_list()

      local result = run_liveness_scan("liveness-blocked-missing-decomposed-stuck-sweep-" .. tostring(sweep))
      t.eq(result.exit_code, 0)
      t.eq(find_raise(result, "github-devloop-decompose.devloop_decompose"), nil)
      t.eq(find_raise(result, "devloop_timeout_reconcile"), nil)
      local comment = find_raise(result, "github-proxy.github_issue_comment_request")
      t.is_true(comment ~= nil)
      if sweep < 3 then
        t.is_true(comment.payload.body:find(conv_attempts.timeout_attempt_marker(core, proposal_id, version, "blocked", sweep, entity_lib.issue_source_ref(repo, 42)), 1, true) ~= nil)
        table.insert(comments, issue_comment(conv_attempts.timeout_attempt_marker(core, proposal_id, version, "blocked", sweep, entity_lib.issue_source_ref(repo, 42))))
      else
        t.is_true(comment.payload.body:find(conv_attempts.decompose_exhausted_marker(core, proposal_id, version, 3, entity_lib.issue_source_ref(repo, 42)), 1, true) ~= nil)
      end
    end
  end,
}

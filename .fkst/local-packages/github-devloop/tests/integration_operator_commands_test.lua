local base_ids = require("devloop.base_ids")
local convergence_shared = require("devloop.convergence.shared")
local h = require("tests.devloop_helpers")
local payloads_builders = require("devloop.payloads.builders")
local conv_rounds = require("devloop.convergence.rounds")
local conv_reconcile = require("devloop.convergence.reconcile")
local m_builders = require("devloop.markers.builders")
local t = h.t
local core = h.core
local opts = h.opts
local reviewing = h.reviewing
local merge_ready = h.merge_ready
local issue = h.issue
local reached = h.reached
local run_observe_pr = h.run_observe_pr
local run_observe = h.run_observe
local run_review_pr = h.run_review_pr
local mock_issue_reviewing = h.mock_issue_reviewing
local mock_issue_review = h.mock_issue_review
local mock_issue_state = h.mock_issue_state
local mock_pr_origin = h.mock_pr_origin
local merge_comments = h.merge_comments
local find_raise = h.find_raise
local find_causal_raise = h.find_causal_raise

local function pr_event(updated_at)
  return {
    schema = "github-proxy.v1",
    type = "pr",
    repo = "owner/repo",
    number = 7,
    dedup_key = "owner/repo#pr#7@" .. tostring(updated_at or "2026-06-04T03:00:00Z"),
    source_ref = {
      kind = "external",
      ref = "owner/repo#pr/7",
    },
  }
end

local function trusted_command(id)
  return {
    id = id or "IC_rereview_1",
    body = "fkst: rereview\n\nCI was rerun.",
    author_login = "fkst-test-bot",
    created_at = "2026-06-04T03:00:00Z",
  }
end

local function trusted_issue_command(command, id)
  return {
    id = id or ("IC_" .. tostring(command) .. "_issue_1"),
    body = "fkst: " .. tostring(command),
    author_login = "fkst-test-bot",
    created_at = "2026-06-04T03:00:00Z",
  }
end

local function thinking_converge_comments(event, rounds, command)
  local proposal_id = base_ids.proposal_id(event.repo, event.number)
  local base_version = payloads_builders.build_proposal(core, event).dedup_key
  local sr_digest = convergence_shared.source_ref_digest(event.source_ref)
  local angle_digests = {
    { angle = "minimal", verdict = "abstain", digest = "same-digest" },
  }
  local comments = {
    core.state_marker(proposal_id, "thinking", base_version .. "/loop/" .. tostring(rounds)),
  }
  for n = 1, rounds do
    table.insert(comments, conv_rounds.converge_round_marker(core,
      proposal_id,
      base_version,
      sr_digest,
      n,
      base_version .. "/loop/" .. tostring(n),
      "Same narrowed question",
      angle_digests
    ))
  end
  if command ~= nil then
    table.insert(comments, command)
  end
  return comments, base_version
end

local function thinking_changing_converge_comments(event, rounds, command)
  local proposal_id = base_ids.proposal_id(event.repo, event.number)
  local base_version = payloads_builders.build_proposal(core, event).dedup_key
  local sr_digest = convergence_shared.source_ref_digest(event.source_ref)
  local comments = {
    core.state_marker(proposal_id, "thinking", base_version .. "/loop/" .. tostring(rounds)),
  }
  for n = 1, rounds do
    table.insert(comments, conv_rounds.converge_round_marker(core,
      proposal_id,
      base_version,
      sr_digest,
      n,
      base_version .. "/loop/" .. tostring(n),
      "Narrowed question " .. tostring(n),
      {
        { angle = "minimal", verdict = "abstain", digest = "digest-" .. tostring(n) },
      }
    ))
  end
  if command ~= nil then
    table.insert(comments, command)
  end
  return comments, base_version
end

local function find_issue_comment_raise(raises, needle)
  for _, raised in ipairs(raises or {}) do
    if raised.queue == "github-proxy.github_issue_comment_request"
      and raised.payload.body:find(needle, 1, true) ~= nil then
      return raised
    end
  end
  return nil
end

return {
  test_issue_rereview_command_reenters_thinking_converge = function()
    local event = issue()
    local command = trusted_issue_command("rereview", "IC_issue_rereview_stalled")
    local comments, base_version = thinking_converge_comments(event, 7, command)
    mock_issue_state({ "fkst-dev:enabled", "fkst-dev:thinking" }, "OPEN", comments)

    local result = run_observe(event, opts("operator-issue-rereview-thinking-converge"))
    t.eq(result.exit_code, 0)
    local comment_raise = find_raise(result.raises, "github-proxy.github_issue_comment_request")
    local proposal_raise = find_raise(result.raises, "consensus.proposal")
    t.is_true(comment_raise.payload.body:find("operator command accepted: rereview", 1, true) ~= nil)
    t.is_true(comment_raise.payload.body:find("fkst:github-devloop:operator-command:v1", 1, true) ~= nil)
    t.eq(proposal_raise.payload.dedup_key, base_version .. "/loop/8")
    t.eq(proposal_raise.payload.round, 8)
    t.eq(proposal_raise.payload.convergence_question, "Same narrowed question")
    t.eq(proposal_raise.payload.source_ref.ref, "owner/repo#issue/42")
  end,

  test_issue_rereview_command_replays_round_seven_converge_without_true_stall = function()
    local event = issue()
    local command = trusted_issue_command("rereview", "IC_issue_rereview_round_7")
    local comments, base_version = thinking_changing_converge_comments(event, 7, command)
    mock_issue_state({ "fkst-dev:enabled", "fkst-dev:thinking" }, "OPEN", comments)

    local result = run_observe(event, opts("operator-issue-rereview-round-7"))
    t.eq(result.exit_code, 0)
    local proposal_raise = find_raise(result.raises, "consensus.proposal")
    t.eq(proposal_raise.payload.dedup_key, base_version .. "/loop/8")
    t.eq(proposal_raise.payload.round, 8)
    t.eq(proposal_raise.payload.convergence_question, "Narrowed question 7")
    t.eq(proposal_raise.payload.prior_round_digests[1].digest, "digest-7")
  end,

  test_issue_rereview_command_reenters_stalled_plain_thinking = function()
    local event = issue({
      updated_at = "2026-06-03T04:05:06Z",
    })
    local command = trusted_issue_command("rereview", "IC_issue_rereview_plain_stalled")
    local base_version = payloads_builders.build_proposal(core, event).dedup_key
    mock_issue_state({ "fkst-dev:enabled", "fkst-dev:thinking" }, "OPEN", {
      core.state_marker(base_ids.proposal_id(event.repo, event.number), "thinking", base_version),
      command,
    })

    local result = run_observe(event, opts("operator-issue-rereview-plain-stalled"))
    t.eq(result.exit_code, 0)
    local comment_raise = find_raise(result.raises, "github-proxy.github_issue_comment_request")
    local proposal_raise = find_raise(result.raises, "consensus.proposal")
    t.is_true(comment_raise.payload.body:find("operator command accepted: rereview", 1, true) ~= nil)
    t.eq(proposal_raise.payload.dedup_key, base_version .. "/replay")
    t.eq(proposal_raise.payload.round, nil)
    t.eq(proposal_raise.payload.source_ref.ref, "owner/repo#issue/42")
  end,

  test_issue_rereview_command_active_thinking_refuses_once = function()
    local event = issue()
    local command = trusted_issue_command("rereview", "IC_issue_rereview_active")
    local base_version = payloads_builders.build_proposal(core, event).dedup_key
    mock_issue_state({ "fkst-dev:enabled", "fkst-dev:thinking" }, "OPEN", {
      {
        body = core.state_marker(base_ids.proposal_id(event.repo, event.number), "thinking", base_version),
        created_at = os.date("!%Y-%m-%dT%H:%M:%SZ", now()),
      },
      command,
    })

    local result = run_observe(event, opts("operator-issue-rereview-active"))
    t.eq(result.exit_code, 0)
    local comment_raise = find_raise(result.raises, "github-proxy.github_issue_comment_request")
    t.is_true(comment_raise.payload.body:find("operator command refused", 1, true) ~= nil)
    t.is_true(comment_raise.payload.body:find("stalled thinking state", 1, true) ~= nil)
    t.is_true(comment_raise.payload.body:find('outcome="refused"', 1, true) ~= nil)
    t.eq(find_raise(result.raises, "consensus.proposal"), nil)

    mock_issue_state({ "fkst-dev:enabled", "fkst-dev:thinking" }, "OPEN", {
      {
        body = core.state_marker(base_ids.proposal_id(event.repo, event.number), "thinking", base_version),
        created_at = os.date("!%Y-%m-%dT%H:%M:%SZ", now()),
      },
      command,
      comment_raise.payload.body,
    })
    local replay = run_observe(event, opts("operator-issue-rereview-active-replay"))
    t.eq(replay.exit_code, 0)
    t.eq(find_raise(replay.raises, "consensus.proposal"), nil)
    local replay_comment = find_raise(replay.raises, "github-proxy.github_issue_comment_request")
    t.is_true(replay_comment ~= nil)
    t.is_true(replay_comment.payload.body:find("operator command refused", 1, true) ~= nil)
    t.is_true(replay_comment.payload.body:find("stalled thinking state", 1, true) ~= nil)
    t.is_true(replay_comment.payload.body:find('outcome="refused"', 1, true) ~= nil)
  end,

  test_issue_reready_command_rechecks_dependency_gate = function()
    local event = reached()
    local command = trusted_issue_command("reready", "IC_issue_reready_release")
    mock_issue_state({ "fkst-dev:enabled", "fkst-dev:ready", "fkst-dev:blocked-on-dependency" }, "OPEN", {
      core.state_marker(event.proposal_id, "dependency_wait", event.dedup_key),
      "github-devloop dependency hold: unresolvable\n\nReason: gh-failed\n\n"
        .. core.dependency_unresolvable_marker(event.proposal_id, event.dedup_key, { 42 }),
      command,
    })

    local result = run_observe(issue({ labels = { "fkst-dev:enabled", "fkst-dev:ready" } }), opts("operator-issue-reready-release"))
    t.eq(result.exit_code, 0)
    local command_response = find_issue_comment_raise(result.raises, "operator command accepted: reready")
    t.is_true(command_response ~= nil)
    t.is_true(command_response.payload.body:find('outcome="applied"', 1, true) ~= nil)
    t.eq(find_raise(result.raises, "devloop_ready"), nil)
    t.is_true(find_raise(result.raises, "github-proxy.github_issue_comment_request", function(payload)
      return type(payload.handoff) == "table"
        and payload.handoff.kind == "github-devloop.ready"
    end) ~= nil)
    t.is_true(find_raise(result.raises, "github-proxy.github_issue_label_request") ~= nil)
  end,

  test_issue_reready_command_invalid_state_refuses = function()
    local event = issue()
    local command = trusted_issue_command("reready", "IC_issue_reready_invalid")
    mock_issue_state({ "fkst-dev:enabled", "fkst-dev:thinking" }, "OPEN", {
      core.state_marker(base_ids.proposal_id(event.repo, event.number), "thinking", payloads_builders.build_proposal(core, event).dedup_key),
      command,
    })

    local result = run_observe(event, opts("operator-issue-reready-invalid"))
    t.eq(result.exit_code, 0)
    local comment_raise = find_raise(result.raises, "github-proxy.github_issue_comment_request")
    t.is_true(comment_raise.payload.body:find("operator command refused", 1, true) ~= nil)
    t.is_true(comment_raise.payload.body:find("reready requires ready or dependency_wait state", 1, true) ~= nil)
    t.eq(find_raise(result.raises, "devloop_ready"), nil)
  end,

  test_issue_reready_command_reenters_timeout_reconcile_blocked_from_ready = function()
    local event = issue()
    local proposal_id = base_ids.proposal_id(event.repo, event.number)
    local ready_version = "consensus:github-devloop/issue/owner/repo/42/intake/1116/loop/1"
    local blocked_version = conv_reconcile.timeout_reconcile_state_version(core, ready_version, "ready", 3)
    local command = trusted_issue_command("reready", "IC_issue_reready_timeout_ready")
    mock_issue_state({ "fkst-dev:enabled", "fkst-dev:blocked" }, "OPEN", {
      core.state_marker(proposal_id, "ready", ready_version, "result-marker,ready-label,devloop-ready"),
      core.state_marker(proposal_id, "blocked", blocked_version),
      conv_reconcile.timeout_reconcile_marker(core, proposal_id, ready_version, "ready", 3, "drop", {
        terminal_version = blocked_version,
        from_state = "ready",
        from_version = ready_version,
        source_ref = event.source_ref,
      }),
      command,
    })

    local result = run_observe(event, opts("operator-issue-reready-timeout-ready"))
    t.eq(result.exit_code, 0)
    local command_response = find_issue_comment_raise(result.raises, "operator command accepted: reready")
    local ready_raise = find_raise(result.raises, "devloop_ready")
    t.is_true(command_response ~= nil)
    t.is_true(command_response.payload.body:find('outcome="applied"', 1, true) ~= nil)
    t.is_true(ready_raise ~= nil)
    t.eq(ready_raise.payload.proposal_id, proposal_id)
    t.eq(ready_raise.payload.ready_hand_off.marker_version, ready_version)
  end,

  test_issue_reready_command_refuses_blocked_without_timeout_reconcile = function()
    local event = issue()
    local command = trusted_issue_command("reready", "IC_issue_reready_blocked_plain")
    mock_issue_state({ "fkst-dev:enabled", "fkst-dev:blocked" }, "OPEN", {
      core.state_marker(base_ids.proposal_id(event.repo, event.number), "blocked", "manual-blocked"),
      command,
    })

    local result = run_observe(event, opts("operator-issue-reready-blocked-plain"))
    t.eq(result.exit_code, 0)
    local comment_raise = find_raise(result.raises, "github-proxy.github_issue_comment_request")
    t.is_true(comment_raise.payload.body:find("operator command refused", 1, true) ~= nil)
    t.is_true(comment_raise.payload.body:find("reready requires ready or dependency_wait state", 1, true) ~= nil)
    t.eq(find_raise(result.raises, "devloop_ready"), nil)
  end,

  test_issue_reready_command_refuses_timeout_reconcile_blocked_with_pr_link = function()
    local event = issue()
    local proposal_id = base_ids.proposal_id(event.repo, event.number)
    local ready_version = "consensus:github-devloop/issue/owner/repo/42/intake/1116/loop/1"
    local blocked_version = conv_reconcile.timeout_reconcile_state_version(core, ready_version, "ready", 3)
    local command = trusted_issue_command("reready", "IC_issue_reready_timeout_pr_link")
    mock_issue_state({ "fkst-dev:enabled", "fkst-dev:blocked" }, "OPEN", {
      core.state_marker(proposal_id, "ready", ready_version, "result-marker,ready-label,devloop-ready"),
      core.state_marker(proposal_id, "blocked", blocked_version),
      m_builders.pr_link_marker(core, proposal_id, "7", "devloop-owner-repo-42-01HY", ready_version, "dev"),
      conv_reconcile.timeout_reconcile_marker(core, proposal_id, ready_version, "ready", 3, "drop", {
        terminal_version = blocked_version,
        from_state = "ready",
        from_version = ready_version,
        source_ref = event.source_ref,
      }),
      command,
    })
    mock_pr_origin({
      m_builders.pr_origin_marker(core, proposal_id, "42", "devloop-owner-repo-42-01HY", ready_version, "dev"),
    }, "devloop-owner-repo-42-01HY", "feedface")

    local result = run_observe(event, opts("operator-issue-reready-timeout-pr-link"))
    t.eq(result.exit_code, 0)
    local comment_raise = find_raise(result.raises, "github-proxy.github_issue_comment_request")
    t.is_true(comment_raise.payload.body:find("operator command refused", 1, true) ~= nil)
    t.eq(find_raise(result.raises, "devloop_ready"), nil)
  end,

  test_issue_reimplement_command_reenters_impl_failed = function()
    local event = reached()
    local ready_version = payloads_builders.build_devloop_ready_payload(core, event).dedup_key
    local command = trusted_issue_command("reimplement", "IC_issue_reimplement")
    mock_issue_state({ "fkst-dev:enabled", "fkst-dev:impl-failed" }, "OPEN", {
      core.state_marker(event.proposal_id, "impl-failed", ready_version),
      core.impl_failure_marker(event.proposal_id, ready_version, "codex-failed"),
      command,
    })

    local result = run_observe(issue({ labels = { "fkst-dev:enabled", "fkst-dev:impl-failed" } }), opts("operator-issue-reimplement"))
    t.eq(result.exit_code, 0)
    local command_response = find_issue_comment_raise(result.raises, "operator command accepted: reimplement")
    local ready_raise = find_raise(result.raises, "devloop_ready")
    t.is_true(command_response ~= nil)
    t.is_true(command_response.payload.body:find('command="reimplement"', 1, true) ~= nil)
    t.is_true(ready_raise ~= nil)
    t.eq(ready_raise.payload.proposal_id, event.proposal_id)
    t.eq(ready_raise.payload.dedup_key, ready_version)
    t.eq(ready_raise.payload.impl_retry_attempt, 2)
  end,
}

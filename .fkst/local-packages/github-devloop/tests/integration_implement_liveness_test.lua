local h = require("tests.devloop_helpers")
local payloads_builders = require("devloop.payloads.builders")
local t = h.t
local core = h.core
local opts = h.opts
local ready = h.ready
local run_implement = h.run_implement
local run_observe = h.run_observe
local issue = h.issue
local mock_issue_implement = h.mock_issue_implement
local mock_issue_state = h.mock_issue_state
local deterministic_branch_for = h.deterministic_branch_for
local mock_fresh_implement_worktree = h.mock_fresh_implement_worktree
local mock_implement_codex = h.mock_implement_codex
local mock_git_status = h.mock_git_status
local mock_branch_diff_paths = h.mock_branch_diff_paths
local mock_git_commit = h.mock_git_commit
local count_calls = h.count_calls
local find_raise = h.find_raise
local codex_status = require("tests.codex_status_helpers")
local m_builders = require("devloop.markers.builders")

local function stale_attempt_started_at()
  return tostring(now() - 7201)
end

local function implement_attempt_marker(event, attempt, started_at, exec_ref)
  return core.implement_attempt_marker(event.proposal_id, event.dedup_key, attempt, started_at, exec_ref)
end

local function live_implement_attempt_marker(event, run_opts, attempt, started_at)
  local exec_ref = core.implement_exec_ref(event.proposal_id, event.dedup_key)
  codex_status.seed_implement_codex_run(run_opts, event.proposal_id, event.dedup_key)
  return implement_attempt_marker(event, attempt or 1, started_at or stale_attempt_started_at(), exec_ref)
end

local function implementing_comments(event, extra)
  local branch = deterministic_branch_for(event)
  local comments = {
    core.state_marker(event.proposal_id, "implementing", event.dedup_key),
  }
  for _, comment in ipairs(extra or {}) do
    table.insert(comments, comment)
  end
  return comments, branch
end

local function liveness_redrive_ready(event)
  return payloads_builders.build_devloop_ready_payload(core, {
    proposal_id = event.proposal_id,
    dedup_key = core.ready_payload_inner_version(event.dedup_key),
    source_ref = event.source_ref,
    impl_retry_attempt = core.implementation_retry_attempt(event.dedup_key),
  })
end

local function mock_missing_remote_branch(branch)
  t.mock_command("git fetch 'origin' '" .. tostring(branch) .. "'", {
    stdout = "",
    stderr = "fatal: couldn't find remote ref",
    exit_code = 128,
  })
end

local function mock_remote_branch(branch, head)
  t.mock_command("git fetch 'origin' '" .. tostring(branch) .. "'", {
    stdout = "",
    stderr = "",
    exit_code = 0,
  })
  t.mock_command("refs/remotes/'origin'/'" .. tostring(branch) .. "'^{commit}", {
    stdout = tostring(head or "def456") .. "\n",
    stderr = "",
    exit_code = 0,
  })
end

return {
  test_implementing_redelivery_reruns_when_no_progress_and_attempt_budget_remains = function()
    local event = ready()
    local comments, branch = implementing_comments(event, {
      core.implement_attempt_marker(event.proposal_id, event.dedup_key, 1, stale_attempt_started_at()),
    })
    mock_issue_implement({ "fkst-dev:implementing" }, comments)
    mock_missing_remote_branch(branch)
    t.mock_command("show-ref --verify --quiet", {
      stdout = "",
      stderr = "",
      exit_code = 1,
    })
    mock_fresh_implement_worktree()
    mock_implement_codex(0, "implemented after retry")
    mock_git_status(" M packages/github-devloop/core.lua\n")
    mock_git_commit("def456", branch)
    mock_issue_implement({ "fkst-dev:implementing" }, comments)

    local result = run_implement(event, opts("implement-liveness-rerun"))
    t.eq(result.exit_code, 0)
    t.eq(count_calls("codex exec"), 1)
    local comment = find_raise(result.raises, "github-proxy.github_issue_comment_request", function(payload)
      return tostring(payload.body or ""):find('attempt="2"', 1, true) ~= nil
    end).payload.body
    t.eq(core.implement_attempt_count({ comment }, event.proposal_id, event.dedup_key), 2)
  end,

  test_implementing_redelivery_sees_remote_branch_without_direct_open_pr = function()
    local event = ready()
    local branch = deterministic_branch_for(event)
    local fact = m_builders.implementing_marker(core, event.proposal_id, event.dedup_key, branch, "abc123", "dev", "abc123")
    local comments = {
      core.state_marker(event.proposal_id, "implementing", event.dedup_key),
      core.implement_attempt_marker(event.proposal_id, event.dedup_key, 1, stale_attempt_started_at()),
      fact,
    }
    mock_issue_implement({ "fkst-dev:implementing" }, comments)
    mock_remote_branch(branch, "abc123")

    local result = run_implement(event, opts("implement-liveness-remote-progress"))
    t.eq(result.exit_code, 0)
    t.eq(count_calls("codex exec"), 0)
  end,

  test_implementing_redelivery_skips_when_pr_link_exists = function()
    local event = ready()
    local branch = deterministic_branch_for(event)
    local comments = {
      core.state_marker(event.proposal_id, "implementing", event.dedup_key),
      core.implement_attempt_marker(event.proposal_id, event.dedup_key, 1, stale_attempt_started_at()),
      m_builders.pr_link_marker(core, event.proposal_id, 7, branch, event.dedup_key, "dev"),
    }
    mock_issue_implement({ "fkst-dev:implementing" }, comments)

    local result = run_implement(event, opts("implement-liveness-pr-link"))
    t.eq(result.exit_code, 0)
    t.eq(#result.raises, 0)
    t.eq(count_calls("codex exec"), 0)
  end,

  test_ready_redelivery_skips_after_worktree_ready_implementing_state = function()
    local event = ready()
    local run_opts = opts("implement-ready-redelivery-after-state")
    local comments = {
      core.state_marker(event.proposal_id, "implementing", event.dedup_key),
      live_implement_attempt_marker(event, run_opts, 1),
    }
    mock_issue_implement({ "fkst-dev:implementing" }, comments)

    local result = run_implement(event, run_opts)
    t.eq(result.exit_code, 0)
    t.eq(#result.raises, 0)
    t.eq(count_calls("codex exec"), 0)
    t.eq(count_calls("git fetch"), 0)
    t.eq(count_calls("git worktree add"), 0)
  end,

  test_implementing_redelivery_marks_failed_after_attempt_budget = function()
    local event = ready()
    local comments, branch = implementing_comments(event, {
      core.implement_attempt_marker(event.proposal_id, event.dedup_key, 2, stale_attempt_started_at()),
    })
    mock_issue_implement({ "fkst-dev:implementing" }, comments)
    mock_missing_remote_branch(branch)
    t.mock_command("git fetch 'origin' 'dev'", {
      stdout = "",
      stderr = "",
      exit_code = 0,
    })
    t.mock_command("refs/remotes/'origin'/'dev'^{commit}", {
      stdout = "abc123\n",
      stderr = "",
      exit_code = 0,
    })
    t.mock_command("show-ref --verify --quiet", {
      stdout = "",
      stderr = "",
      exit_code = 1,
    })

    local result = run_implement(event, opts("implement-liveness-exhausted"))
    t.eq(result.exit_code, 0)
    t.eq(count_calls("codex exec"), 0)
    t.eq(find_raise(result.raises, "github-proxy.github_issue_label_request").payload.add_labels[1], "fkst-dev:impl-failed")
  end,

  test_implementing_liveness_redrive_uses_current_marker_version = function()
    local current = ready()
    local event = liveness_redrive_ready(current)
    local comments, branch = implementing_comments(current, {
      core.implement_attempt_marker(current.proposal_id, current.dedup_key, 2, stale_attempt_started_at()),
    })
    mock_issue_implement({ "fkst-dev:implementing" }, comments)
    mock_missing_remote_branch(branch)
    t.mock_command("git fetch 'origin' 'dev'", {
      stdout = "",
      stderr = "",
      exit_code = 0,
    })
    t.mock_command("refs/remotes/'origin'/'dev'^{commit}", {
      stdout = "abc123\n",
      stderr = "",
      exit_code = 0,
    })
    t.mock_command("show-ref --verify --quiet", {
      stdout = "",
      stderr = "",
      exit_code = 1,
    })

    local result = run_implement(event, opts("implement-liveness-redrive-current-marker"))
    t.eq(result.exit_code, 0)
    t.eq(count_calls("codex exec"), 0)
    local label = find_raise(result.raises, "github-proxy.github_issue_label_request")
    t.eq(label.payload.add_labels[1], "fkst-dev:impl-failed")
    local comment = find_raise(result.raises, "github-proxy.github_issue_comment_request")
    t.is_true(comment.payload.body:find(core.state_marker(current.proposal_id, "impl-failed", current.dedup_key), 1, true) ~= nil)
  end,

  test_implementing_liveness_redrive_takes_over_orphaned_owner_marker = function()
    local current = ready()
    local event = liveness_redrive_ready(current)
    local branch = deterministic_branch_for(current)
    local comments = {
      core.state_marker(current.proposal_id, "implementing", current.dedup_key),
      m_builders.implementing_marker(core, current.proposal_id, current.dedup_key, branch, "abc123", "dev", "abc123"),
    }
    mock_issue_implement({ "fkst-dev:implementing" }, comments)
    mock_missing_remote_branch(branch)
    t.mock_command("show-ref --verify --quiet", {
      stdout = "",
      stderr = "",
      exit_code = 1,
    })
    mock_fresh_implement_worktree()
    mock_implement_codex(0, "implemented after orphan takeover")
    mock_git_status(" M packages/github-devloop/core.lua\n")
    mock_git_commit("def456", branch)
    mock_issue_implement({ "fkst-dev:implementing" }, comments)

    local result = run_implement(event, opts("implement-liveness-redrive-orphaned-owner"))
    t.eq(result.exit_code, 0)
    t.eq(count_calls("codex exec"), 1)
    local comment = find_raise(result.raises, "github-proxy.github_issue_comment_request").payload.body
    t.eq(core.implement_attempt_count({ comment }, current.proposal_id, current.dedup_key), 1)
  end,

  test_liveness_replayer_skips_live_implement_attempt_before_receiver = function()
    local current = ready()
    local run_opts = opts("observe-implement-live-attempt-budget-owner")
    local comments = {
      core.state_marker(current.proposal_id, "implementing", current.dedup_key),
      live_implement_attempt_marker(current, run_opts, 1),
    }

    mock_issue_state({ "fkst-dev:enabled", "fkst-dev:implementing" }, "OPEN", comments)
    local result = run_observe(issue({ labels = { "fkst-dev:enabled", "fkst-dev:implementing" } }), run_opts)
    t.eq(result.exit_code, 0)
    t.eq(find_raise(result.raises, "devloop_ready"), nil)
  end,

  test_implementing_redelivery_recovers_local_branch_before_attempt_budget = function()
    local event = ready()
    local comments, branch = implementing_comments(event, {
      core.implement_attempt_marker(event.proposal_id, event.dedup_key, 2, stale_attempt_started_at()),
    })
    mock_issue_implement({ "fkst-dev:implementing" }, comments)
    mock_missing_remote_branch(branch)
    t.mock_command("git fetch 'origin' 'dev'", {
      stdout = "",
      stderr = "",
      exit_code = 0,
    })
    t.mock_command("refs/remotes/'origin'/'dev'^{commit}", {
      stdout = "abc123\n",
      stderr = "",
      exit_code = 0,
    })
    t.mock_command("show-ref --verify --quiet", {
      stdout = "",
      stderr = "",
      exit_code = 0,
    })
    t.mock_command("rev-list --count", {
      stdout = "1\n",
      stderr = "",
      exit_code = 0,
    })
    t.mock_command("rev-parse --verify refs/heads/", {
      stdout = "def456\n",
      stderr = "",
      exit_code = 0,
    })
    mock_branch_diff_paths("packages/github-devloop/core.lua\n")

    local result = run_implement(event, opts("implement-liveness-local-progress-at-budget"))
    t.eq(result.exit_code, 0)
    t.eq(count_calls("codex exec"), 0)
    t.eq(find_raise(result.raises, "github-proxy.github_issue_label_request"), nil)
  end,

  test_second_retry_death_exhausts_after_observe_reraises = function()
    local event = ready()
    local comments, branch = implementing_comments(event, {
      core.implement_attempt_marker(event.proposal_id, event.dedup_key, 2, tostring(now() - 7201)),
    })
    mock_issue_state({ "fkst-dev:enabled", "fkst-dev:implementing" }, "OPEN", comments)

    local observed = run_observe(issue({ labels = { "fkst-dev:enabled", "fkst-dev:implementing" } }), opts("observe-implement-second-attempt-expired"))
    t.eq(observed.exit_code, 0)
    t.eq(find_raise(observed.raises, "devloop_ready").payload.proposal_id, event.proposal_id)

    mock_issue_implement({ "fkst-dev:implementing" }, comments)
    mock_missing_remote_branch(branch)
    t.mock_command("git fetch 'origin' 'dev'", {
      stdout = "",
      stderr = "",
      exit_code = 0,
    })
    t.mock_command("refs/remotes/'origin'/'dev'^{commit}", {
      stdout = "abc123\n",
      stderr = "",
      exit_code = 0,
    })
    t.mock_command("show-ref --verify --quiet", {
      stdout = "",
      stderr = "",
      exit_code = 1,
    })

    local retried = run_implement(event, opts("implement-second-attempt-exhausted"))
    t.eq(retried.exit_code, 0)
    t.eq(count_calls("codex exec"), 0)
    t.eq(find_raise(retried.raises, "github-proxy.github_issue_label_request").payload.add_labels[1], "fkst-dev:impl-failed")
  end,

  test_observe_reraises_implement_after_attempt_liveness_expires = function()
    local event = ready()
    local comments = {
      core.state_marker(event.proposal_id, "implementing", event.dedup_key),
      core.implement_attempt_marker(event.proposal_id, event.dedup_key, 1, tostring(now() - 7201)),
    }
    mock_issue_state({ "fkst-dev:enabled", "fkst-dev:implementing" }, "OPEN", comments)

    local result = run_observe(issue({ labels = { "fkst-dev:enabled", "fkst-dev:implementing" } }), opts("observe-implement-expired"))
    t.eq(result.exit_code, 0)
    local raised = find_raise(result.raises, "devloop_ready")
    t.eq(raised.payload.proposal_id, event.proposal_id)
    -- The re-raised ready reproduces the frozen implementing marker version
    -- EXACTLY (build_devloop_ready_payload re-wraps the inner version), so the
    -- implement receiver's recomputed marker version matches and the re-drive is
    -- accepted -- not double-wrapped to "ready/ready/..." which skip-staled
    -- forever (#718).
    t.eq(raised.payload.dedup_key, event.dedup_key)
  end,

  test_observe_skips_live_implement_attempt = function()
    local event = ready()
    local run_opts = opts("observe-implement-live")
    local comments = {
      core.state_marker(event.proposal_id, "implementing", event.dedup_key),
      live_implement_attempt_marker(event, run_opts, 1),
    }
    mock_issue_state({ "fkst-dev:enabled", "fkst-dev:implementing" }, "OPEN", comments)

    local result = run_observe(issue({ labels = { "fkst-dev:enabled", "fkst-dev:implementing" } }), run_opts)
    t.eq(result.exit_code, 0)
    t.eq(find_raise(result.raises, "devloop_ready"), nil)
  end,

  test_observe_reraises_old_implementing_marker_without_attempt_marker = function()
    local event = ready({
      dedup_key = "ready/consensus-github-devloop/issue/owner/repo/42/2026-01-01T00-00-00Z",
    })
    local branch = deterministic_branch_for(event)
    local comments = {
      core.state_marker(event.proposal_id, "implementing", event.dedup_key),
      m_builders.implementing_marker(core, event.proposal_id, event.dedup_key, branch, "abc123", "dev", "abc123"),
    }
    mock_issue_state({ "fkst-dev:enabled", "fkst-dev:implementing" }, "OPEN", comments)

    local result = run_observe(issue({ labels = { "fkst-dev:enabled", "fkst-dev:implementing" } }), opts("observe-implement-old-no-attempt"))
    t.eq(result.exit_code, 0)
    local raised = find_raise(result.raises, "devloop_ready")
    t.eq(raised.payload.proposal_id, event.proposal_id)
    -- The re-raised ready reproduces the frozen implementing marker version
    -- EXACTLY (build_devloop_ready_payload re-wraps the inner version), so the
    -- implement receiver's recomputed marker version matches and the re-drive is
    -- accepted -- not double-wrapped to "ready/ready/..." which skip-staled
    -- forever (#718).
    t.eq(raised.payload.dedup_key, event.dedup_key)
  end,

  -- Production-shaped round-trip (#718): the payload observe's liveness re-drive
  -- actually delivers must be ACCEPTED by the implement receiver, not skip-staled
  -- forever. The other tests feed a hand-built ready() straight into
  -- run_implement and never exercise the observe->implement chain production runs,
  -- so the double-wrap defect was invisible (the #550/#551 harness lesson).
  test_observe_reraised_ready_round_trips_into_implement_without_skip_stale = function()
    local event = ready()
    local branch = deterministic_branch_for(event)
    local stuck = {
      core.state_marker(event.proposal_id, "implementing", event.dedup_key),
      core.implement_attempt_marker(event.proposal_id, event.dedup_key, 1, tostring(now() - 7201)),
    }
    mock_issue_state({ "fkst-dev:enabled", "fkst-dev:implementing" }, "OPEN", stuck)
    local observed = run_observe(issue({ labels = { "fkst-dev:enabled", "fkst-dev:implementing" } }), opts("observe-718-roundtrip"))
    t.eq(observed.exit_code, 0)
    local reraised = find_raise(observed.raises, "devloop_ready")
    t.eq(reraised ~= nil, true)

    -- Feed the EXACT re-raised payload back into implement on the same stuck
    -- implementing marker. With the fix it advances (re-runs codex, opens a PR);
    -- before the fix it skip-staled (codex never runs, zero progress, forever).
    local rerun = {
      core.state_marker(event.proposal_id, "implementing", event.dedup_key),
      core.implement_attempt_marker(event.proposal_id, event.dedup_key, 1, stale_attempt_started_at()),
    }
    mock_issue_implement({ "fkst-dev:implementing" }, rerun)
    mock_missing_remote_branch(branch)
    t.mock_command("show-ref --verify --quiet", { stdout = "", stderr = "", exit_code = 1 })
    mock_fresh_implement_worktree()
    mock_implement_codex(0, "implemented after liveness re-drive")
    mock_git_status(" M packages/github-devloop/core.lua\n")
    mock_git_commit("def456", branch)
    mock_issue_implement({ "fkst-dev:implementing" }, rerun)

    local result = run_implement(reraised.payload, opts("implement-718-roundtrip"))
    t.eq(result.exit_code, 0)
    t.eq(count_calls("codex exec"), 1, "re-raised ready must re-run implement, not skip-stale forever")
  end,

  test_observe_reraises_reimplement_attempt_preserving_suffix = function()
    local event = ready()
    local retry_version = core.implementation_attempt_version(event.dedup_key, 2)
    local stuck = {
      core.state_marker(event.proposal_id, "implementing", retry_version),
      core.implement_attempt_marker(event.proposal_id, retry_version, 2, tostring(now() - 7201)),
    }
    mock_issue_state({ "fkst-dev:enabled", "fkst-dev:implementing" }, "OPEN", stuck)

    local result = run_observe(issue({ labels = { "fkst-dev:enabled", "fkst-dev:implementing" } }), opts("observe-721-reimplement-redrive"))
    t.eq(result.exit_code, 0)
    local raised = find_raise(result.raises, "devloop_ready")
    t.eq(raised.payload.proposal_id, event.proposal_id)
    t.eq(raised.payload.dedup_key, retry_version)
    t.eq(raised.payload.impl_retry_attempt, 2)
  end,

  test_observe_reraised_reimplement_ready_round_trips_into_implement_without_skip_stale = function()
    local event = ready()
    local retry_version = core.implementation_attempt_version(event.dedup_key, 2)
    local branch = deterministic_branch_for(event)
    local stuck = {
      core.state_marker(event.proposal_id, "implementing", retry_version),
      core.implement_attempt_marker(event.proposal_id, retry_version, 2, tostring(now() - 7201)),
    }
    mock_issue_state({ "fkst-dev:enabled", "fkst-dev:implementing" }, "OPEN", stuck)
    local observed = run_observe(issue({ labels = { "fkst-dev:enabled", "fkst-dev:implementing" } }), opts("observe-721-roundtrip"))
    t.eq(observed.exit_code, 0)
    local reraised = find_raise(observed.raises, "devloop_ready")
    t.eq(reraised ~= nil, true)

    local progress = {
      core.state_marker(event.proposal_id, "implementing", retry_version),
      core.implement_attempt_marker(event.proposal_id, retry_version, 2, stale_attempt_started_at()),
      m_builders.implementing_marker(core, event.proposal_id, retry_version, branch, "abc123", "dev", "abc123"),
    }
    mock_issue_implement({ "fkst-dev:implementing" }, progress)
    mock_remote_branch(branch, "abc123")

    local result = run_implement(reraised.payload, opts("implement-721-roundtrip"))
    t.eq(result.exit_code, 0)
    t.eq(count_calls("codex exec"), 0)
  end,

  test_double_wrapped_liveness_redrive_is_not_recovered = function()
    local event = ready()
    local double_wrapped = payloads_builders.build_devloop_ready_payload(core, {
      proposal_id = event.proposal_id,
      dedup_key = event.dedup_key,
      source_ref = event.source_ref,
    })
    local comments = {
      core.state_marker(event.proposal_id, "implementing", event.dedup_key),
      core.implement_attempt_marker(event.proposal_id, event.dedup_key, 1, stale_attempt_started_at()),
    }
    mock_issue_implement({ "fkst-dev:implementing" }, comments)

    local result = run_implement(double_wrapped, opts("implement-726-double-wrapped-redrive"))
    t.eq(result.exit_code, 1)
    t.eq(count_calls("codex exec"), 0)
    local comment = find_raise(result.raises, "github-proxy.github_issue_comment_request")
    t.eq(comment ~= nil, true)
    t.eq(core.implement_version_mismatch_attempt_count({ comment.payload.body }, event.proposal_id, double_wrapped.dedup_key, event.dedup_key), 1)
  end,

  test_implementing_version_mismatch_fails_closed_after_delivery_budget = function()
    local event = ready()
    local retry_version = core.implementation_attempt_version(event.dedup_key, 2)
    mock_issue_implement({ "fkst-dev:implementing" }, {
      core.state_marker(event.proposal_id, "implementing", retry_version),
      core.implement_attempt_marker(event.proposal_id, retry_version, 2, stale_attempt_started_at()),
      core.implement_version_mismatch_marker(event.proposal_id, event.dedup_key, retry_version, 1),
      core.implement_version_mismatch_marker(event.proposal_id, event.dedup_key, retry_version, 2),
    })

    local result = run_implement(event, opts("implement-721-version-mismatch-budget"))
    t.eq(result.exit_code, 1)
    t.eq(#result.raises, 0)
  end,

  test_implementing_version_mismatch_persists_skip_stale_attempt = function()
    local event = ready()
    local retry_version = core.implementation_attempt_version(event.dedup_key, 2)
    mock_issue_implement({ "fkst-dev:implementing" }, {
      core.state_marker(event.proposal_id, "implementing", retry_version),
      core.implement_attempt_marker(event.proposal_id, retry_version, 2, stale_attempt_started_at()),
    })

    local result = run_implement(event, opts("implement-721-version-mismatch-persist"))
    t.eq(result.exit_code, 1)
    local comment = find_raise(result.raises, "github-proxy.github_issue_comment_request")
    t.eq(comment ~= nil, true)
    t.eq(core.implement_version_mismatch_attempt_count({ comment.payload.body }, event.proposal_id, event.dedup_key, retry_version), 1)
  end,

  test_observe_skips_implementing_state_marker_without_progress_facts = function()
    local event = ready({
      dedup_key = "ready/consensus-github-devloop/issue/owner/repo/42/2026-01-01T00-00-00Z",
    })
    local comments = {
      core.state_marker(event.proposal_id, "implementing", event.dedup_key),
    }
    mock_issue_state({ "fkst-dev:enabled", "fkst-dev:implementing" }, "OPEN", comments)

    local result = run_observe(issue({ labels = { "fkst-dev:enabled", "fkst-dev:implementing" } }), opts("observe-implement-no-progress-facts"))
    t.eq(result.exit_code, 0)
    t.eq(find_raise(result.raises, "devloop_ready"), nil)
  end,

  test_observe_skips_recent_implementing_marker_without_attempt_marker = function()
    local event = ready({
      dedup_key = "ready/consensus-github-devloop/issue/owner/repo/42/2999-01-01T00-00-00Z",
    })
    local branch = deterministic_branch_for(event)
    local comments = {
      core.state_marker(event.proposal_id, "implementing", event.dedup_key),
      m_builders.implementing_marker(core, event.proposal_id, event.dedup_key, branch, "abc123", "dev", "abc123"),
    }
    mock_issue_state({ "fkst-dev:enabled", "fkst-dev:implementing" }, "OPEN", comments)

    local result = run_observe(issue({ labels = { "fkst-dev:enabled", "fkst-dev:implementing" } }), opts("observe-implement-recent-no-attempt"))
    t.eq(result.exit_code, 0)
    t.eq(find_raise(result.raises, "devloop_ready").payload.proposal_id, event.proposal_id)
  end,
}

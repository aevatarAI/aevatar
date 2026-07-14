local entity_lib = require("devloop.entity")
local h = require("tests.devloop_helpers")
local contract_time = require("contract.time")
local conv_attempts = require("devloop.convergence.attempts")
local m_rae = require("devloop.restart_actionable_epoch")
local t = h.t
local core = h.core
local ready = h.ready
local replay_fields = require("devloop.replay_fields")

local repo = "owner/repo"

local function restart_transition_row(state_name)
  return replay_fields.restart_transition_row(core.restart_transition_table(), state_name)
end

local function state_for(event, version)
  return {
    state = "implementing",
    version = version or event.dedup_key,
    proposal_id = event.proposal_id,
    marker_created_at = "2026-06-03T00:00:00Z",
  }
end

local function facts_for(event, comments, now_seconds)
  return {
    proposal_id = event.proposal_id,
    source_ref = event.source_ref,
    current = {
      comments = comments or {},
      labels = { "fkst-dev:enabled", "fkst-dev:implementing" },
    },
    fresh_current_state = state_for(event),
    now_seconds = now_seconds or contract_time.iso_timestamp_epoch_seconds("2026-06-03T03:00:00Z"),
  }
end

local function with_codex_run_status(status, fn)
  local original = fkst.codex_runs
  fkst.codex_runs = function()
    return status or { running = {}, recent = {} }
  end
  local ok, err = pcall(fn)
  fkst.codex_runs = original
  if not ok then
    error(err)
  end
end

local function with_codex_runs(running, fn)
  return with_codex_run_status({ running = running or {}, recent = {} }, fn)
end

local function capture_raises(fn)
  local raised = {}
  local original = core.log_raise
  core.log_raise = function(_, _, queue, payload)
    table.insert(raised, { queue = queue, payload = payload })
  end
  local ok, err = pcall(fn)
  core.log_raise = original
  if not ok then
    error(err)
  end
  return raised
end

local function captured_raise(raised, queue, predicate)
  for _, item in ipairs(raised or {}) do
    if item.queue == queue and (predicate == nil or predicate(item.payload, item)) then
      return item
    end
  end
  return nil
end

local function trusted_comment(body)
  return {
    body = body,
    author_login = "fkst-test-bot",
    created_at = "2026-06-03T00:00:00Z",
  }
end

local function assert_no_timeout_effects(raised)
  t.eq(captured_raise(raised, "devloop_ready"), nil)
  t.eq(captured_raise(raised, "devloop_timeout_reconcile"), nil)
  t.eq(captured_raise(raised, "github-proxy.github_issue_comment_request", function(payload)
    return tostring(payload.body or ""):find("fkst:github-devloop:timeout-attempt", 1, true) ~= nil
  end), nil)
end

local function run_timeout(row, state, facts)
  return capture_raises(function()
    local handled = core.maybe_timeout_redrive_from_table("liveness_scan", {
      repo = repo,
      number = 42,
      source_ref = entity_lib.issue_source_ref(repo, 42),
    }, state, row, facts)
    t.eq(handled, true)
  end)
end

return {
  test_implement_live_codex_run_defers_without_attempt_marker = function()
    local event = ready()
    local row = restart_transition_row("implementing")
    local state = state_for(event)
    local comments = {
      core.state_marker(event.proposal_id, "implementing", event.dedup_key),
    }
    local facts = facts_for(event, comments)
    with_codex_runs({
      {
        run_id = "implement-live",
        role = "implement",
        proposal_id = event.proposal_id,
        dedup_key = event.dedup_key,
        status = "running",
        started_at = "2026-06-03T02:30:00Z",
        timeout_seconds = 3600,
      },
    }, function()
      local receiver = core.restart_row_receiver_liveness(row, state, facts, facts.now_seconds)
      t.eq(receiver.action, "defer")
      t.eq(receiver.signal.family, "codex_run:v1")
      assert_no_timeout_effects(run_timeout(row, state, facts))
    end)
  end,

  test_implement_live_codex_run_within_deadline_defers = function()
    local event = ready()
    local row = restart_transition_row("implementing")
    local state = state_for(event)
    local facts = facts_for(event, {
      core.state_marker(event.proposal_id, "implementing", event.dedup_key),
    }, contract_time.iso_timestamp_epoch_seconds("2026-06-03T00:59:00Z"))
    with_codex_runs({
      {
        run_id = "implement-live-within-deadline",
        role = "implement",
        proposal_id = event.proposal_id,
        dedup_key = event.dedup_key,
        status = "running",
        started_at = "2026-06-03T00:00:00Z",
        timeout_seconds = 3600,
      },
    }, function()
      local signal = core.restart_row_liveness_signal(row, state, facts, facts.now_seconds)
      t.eq(signal.live, true)
      t.eq(signal.reason, "codex-run-running")
      t.eq(signal.deadline_source, "started_at_plus_timeout_seconds")
      local receiver = core.restart_row_receiver_liveness(row, state, facts, facts.now_seconds)
      t.eq(receiver.action, "defer")
      assert_no_timeout_effects(run_timeout(row, state, facts))
    end)
  end,

  test_implement_hung_codex_run_past_deadline_terminates_after_budget = function()
    local event = ready()
    local row = restart_transition_row("implementing")
    local timeout_version = event.dedup_key .. "/timeout/implementing/2"
    local state = state_for(event, timeout_version)
    local facts = facts_for(event, {
      core.state_marker(event.proposal_id, "implementing", timeout_version),
    }, contract_time.iso_timestamp_epoch_seconds("2026-06-03T03:00:00Z"))
    with_codex_runs({
      {
        run_id = "implement-live-past-deadline",
        role = "implement",
        proposal_id = event.proposal_id,
        dedup_key = event.dedup_key,
        status = "running",
        started_at = "2026-06-03T00:00:00Z",
        timeout_seconds = 3600,
      },
    }, function()
      local eval = m_rae.actionable_epoch_resolve(core, row, state, facts, facts.now_seconds)
      t.eq(eval.status, "actionable")
      t.eq(eval.signal.reason, "codex-run-deadline-expired")
      table.insert(facts.current.comments, trusted_comment(conv_attempts.timeout_attempt_v2_marker(core,
        event.proposal_id,
        row.from_state,
        row.liveness_class_id,
        eval.generation_key,
        1,
        event.source_ref
      )))
      table.insert(facts.current.comments, trusted_comment(conv_attempts.timeout_attempt_v2_marker(core,
        event.proposal_id,
        row.from_state,
        row.liveness_class_id,
        eval.generation_key,
        2,
        event.source_ref
      )))
      local due, age = core.liveness_timeout_due_with_facts(row, state, facts, facts.now_seconds)
      t.eq(due, true)
      t.eq(age, 180)
      local raised = run_timeout(row, state, facts)
      t.eq(captured_raise(raised, "devloop_ready"), nil)
      local reconcile = captured_raise(raised, "devloop_timeout_reconcile")
      t.is_true(reconcile ~= nil)
      t.eq(reconcile.payload.state, "implementing")
      t.eq(reconcile.payload.round, 3)
    end)
  end,

  test_implement_recent_codex_run_within_handoff_window_defers = function()
    local event = ready()
    local row = restart_transition_row("implementing")
    local state = state_for(event)
    local facts = facts_for(event, {
      core.state_marker(event.proposal_id, "implementing", event.dedup_key),
    }, contract_time.iso_timestamp_epoch_seconds("2026-06-03T00:59:00Z"))
    with_codex_run_status({
      running = {},
      recent = {
        {
          run_id = "implement-recent-within-deadline",
          role = "implement",
          proposal_id = event.proposal_id,
          dedup_key = event.dedup_key,
          status = "done",
          started_at = "2026-06-03T00:00:00Z",
          timeout_seconds = 3600,
          exit_code = 0,
        },
      },
    }, function()
      local signal = core.restart_row_liveness_signal(row, state, facts, facts.now_seconds)
      t.eq(signal.live, true)
      t.eq(signal.reason, "codex-run-recent-handoff")
      t.eq(signal.collection, "recent")
      local receiver = core.restart_row_receiver_liveness(row, state, facts, facts.now_seconds)
      t.eq(receiver.action, "defer")
      assert_no_timeout_effects(run_timeout(row, state, facts))
    end)
  end,

  test_implement_no_codex_run_over_budget_remains_actionable = function()
    local event = ready()
    local row = restart_transition_row("implementing")
    local timeout_version = event.dedup_key .. "/timeout/implementing/2"
    local state = state_for(event, timeout_version)
    local facts = facts_for(event, {
      core.state_marker(event.proposal_id, "implementing", timeout_version),
    }, contract_time.iso_timestamp_epoch_seconds("2026-06-03T03:00:00Z"))
    with_codex_runs({}, function()
      local eval = m_rae.actionable_epoch_resolve(core, row, state, facts, facts.now_seconds)
      t.eq(eval.status, "actionable")
      t.eq(eval.signal.reason, "codex-run-not-running")
      t.eq(eval.codex_runs_fallback, false)
      t.eq(eval.indeterminate, false)
      local due, age = core.liveness_timeout_due_with_facts(row, state, facts, facts.now_seconds)
      t.eq(due, true)
      t.eq(age, 180)
      local receiver = core.restart_row_receiver_liveness(row, state, facts, facts.now_seconds)
      t.eq(receiver.action, "stuck")
    end)
  end,

  test_implement_codex_runs_unavailable_before_budget_defers_without_timeout_effects = function()
    local event = ready()
    local row = restart_transition_row("implementing")
    local state = state_for(event)
    local facts = facts_for(event, {
      core.state_marker(event.proposal_id, "implementing", event.dedup_key),
    }, contract_time.iso_timestamp_epoch_seconds("2026-06-03T01:00:00Z"))
    local original = fkst.codex_runs
    fkst.codex_runs = function()
      error("synthetic codex_runs failure")
    end
    local ok, err = pcall(function()
      local eval = m_rae.actionable_epoch_resolve(core, row, state, facts, facts.now_seconds)
      t.eq(eval.status, "deferred")
      t.eq(eval.signal.reason, "codex-runs-unavailable")
      t.eq(eval.signal.codex_runs_fallback, true)
      local due, age = core.liveness_timeout_due_with_facts(row, state, facts, facts.now_seconds)
      t.eq(due, false)
      t.eq(age, nil)
      local receiver = core.restart_row_receiver_liveness(row, state, facts, facts.now_seconds)
      t.eq(receiver.action, "defer")
      local raised = run_timeout(row, state, facts)
      assert_no_timeout_effects(raised)
    end)
    fkst.codex_runs = original
    if not ok then
      error(err)
    end
  end,

  test_implement_codex_runs_unavailable_past_budget_escalates = function()
    local event = ready()
    local row = restart_transition_row("implementing")
    local timeout_version = event.dedup_key .. "/timeout/implementing/2"
    local state = state_for(event, timeout_version)
    local facts = facts_for(event, {
      core.state_marker(event.proposal_id, "implementing", timeout_version),
    }, contract_time.iso_timestamp_epoch_seconds("2026-06-03T03:00:00Z"))
    local original = fkst.codex_runs
    fkst.codex_runs = function()
      error("synthetic codex_runs failure")
    end
    local ok, err = pcall(function()
      local eval = m_rae.actionable_epoch_resolve(core, row, state, facts, facts.now_seconds)
      t.eq(eval.status, "actionable")
      t.eq(eval.reason, "codex run liveness indeterminate over row budget")
      t.eq(eval.signal.reason, "codex-runs-unavailable")
      t.eq(eval.codex_runs_fallback, true)
      table.insert(facts.current.comments, trusted_comment(conv_attempts.timeout_attempt_v2_marker(core,
        event.proposal_id,
        row.from_state,
        row.liveness_class_id,
        eval.generation_key,
        1,
        event.source_ref
      )))
      table.insert(facts.current.comments, trusted_comment(conv_attempts.timeout_attempt_v2_marker(core,
        event.proposal_id,
        row.from_state,
        row.liveness_class_id,
        eval.generation_key,
        2,
        event.source_ref
      )))
      local due, age = core.liveness_timeout_due_with_facts(row, state, facts, facts.now_seconds)
      t.eq(due, true)
      t.eq(age, 180)
      local receiver = core.restart_row_receiver_liveness(row, state, facts, facts.now_seconds)
      t.eq(receiver.action, "stuck")
      local raised = run_timeout(row, state, facts)
      t.eq(captured_raise(raised, "devloop_ready"), nil)
      local reconcile = captured_raise(raised, "devloop_timeout_reconcile")
      t.is_true(reconcile ~= nil)
      t.eq(reconcile.payload.state, "implementing")
      t.eq(reconcile.payload.round, 3)
    end)
    fkst.codex_runs = original
    if not ok then
      error(err)
    end
  end,

  test_implement_running_codex_run_without_deadline_before_budget_defers_then_recovers = function()
    local event = ready()
    local row = restart_transition_row("implementing")
    local state = state_for(event)
    local facts = facts_for(event, {
      core.state_marker(event.proposal_id, "implementing", event.dedup_key),
    }, contract_time.iso_timestamp_epoch_seconds("2026-06-03T01:00:00Z"))
    with_codex_runs({
      {
        run_id = "implement-running-missing-deadline",
        role = "implement",
        proposal_id = event.proposal_id,
        dedup_key = event.dedup_key,
        status = "running",
      },
    }, function()
      local eval = m_rae.actionable_epoch_resolve(core, row, state, facts, facts.now_seconds)
      t.eq(eval.status, "deferred")
      t.eq(eval.signal.reason, "codex-run-deadline-unavailable")
      t.eq(eval.signal.indeterminate, true)
      local due, age = core.liveness_timeout_due_with_facts(row, state, facts, facts.now_seconds)
      t.eq(due, false)
      t.eq(age, nil)
      local receiver = core.restart_row_receiver_liveness(row, state, facts, facts.now_seconds)
      t.eq(receiver.action, "defer")
      local raised = run_timeout(row, state, facts)
      assert_no_timeout_effects(raised)
    end)
    with_codex_runs({}, function()
      local recovered = facts_for(event, {
        core.state_marker(event.proposal_id, "implementing", event.dedup_key),
      }, contract_time.iso_timestamp_epoch_seconds("2026-06-03T01:00:00Z"))
      local eval = m_rae.actionable_epoch_resolve(core, row, state, recovered, recovered.now_seconds)
      t.eq(eval.status, "actionable")
      t.eq(eval.signal.reason, "codex-run-not-running")
      local due, age = core.liveness_timeout_due_with_facts(row, state, recovered, recovered.now_seconds)
      t.eq(due, false)
      t.eq(age, 60)
      local receiver = core.restart_row_receiver_liveness(row, state, recovered, recovered.now_seconds)
      t.eq(receiver.action, "stuck")
    end)
  end,

  test_implement_running_codex_run_without_deadline_past_budget_escalates = function()
    local event = ready()
    local row = restart_transition_row("implementing")
    local timeout_version = event.dedup_key .. "/timeout/implementing/2"
    local state = state_for(event, timeout_version)
    local facts = facts_for(event, {
      core.state_marker(event.proposal_id, "implementing", timeout_version),
    }, contract_time.iso_timestamp_epoch_seconds("2026-06-03T03:00:00Z"))
    with_codex_runs({
      {
        run_id = "implement-running-missing-deadline",
        role = "implement",
        proposal_id = event.proposal_id,
        dedup_key = event.dedup_key,
        status = "running",
      },
    }, function()
      local eval = m_rae.actionable_epoch_resolve(core, row, state, facts, facts.now_seconds)
      t.eq(eval.status, "actionable")
      t.eq(eval.reason, "codex run liveness indeterminate over row budget")
      t.eq(eval.signal.reason, "codex-run-deadline-unavailable")
      t.eq(eval.signal.indeterminate, true)
      table.insert(facts.current.comments, trusted_comment(conv_attempts.timeout_attempt_v2_marker(core,
        event.proposal_id,
        row.from_state,
        row.liveness_class_id,
        eval.generation_key,
        1,
        event.source_ref
      )))
      table.insert(facts.current.comments, trusted_comment(conv_attempts.timeout_attempt_v2_marker(core,
        event.proposal_id,
        row.from_state,
        row.liveness_class_id,
        eval.generation_key,
        2,
        event.source_ref
      )))
      local due, age = core.liveness_timeout_due_with_facts(row, state, facts, facts.now_seconds)
      t.eq(due, true)
      t.eq(age, 180)
      local receiver = core.restart_row_receiver_liveness(row, state, facts, facts.now_seconds)
      t.eq(receiver.action, "stuck")
      local raised = run_timeout(row, state, facts)
      t.eq(captured_raise(raised, "devloop_ready"), nil)
      local reconcile = captured_raise(raised, "devloop_timeout_reconcile")
      t.is_true(reconcile ~= nil)
      t.eq(reconcile.payload.state, "implementing")
      t.eq(reconcile.payload.round, 3)
    end)
  end,

  test_implement_codex_run_match_preserves_reimplement_suffix = function()
    local event = ready()
    local retry_version = core.implementation_attempt_version(event.dedup_key, 2)
    local row = restart_transition_row("implementing")
    local state = state_for(event, retry_version)
    local facts = facts_for(event, {
      core.state_marker(event.proposal_id, "implementing", retry_version),
    })
    with_codex_runs({
      {
        run_id = "base-only-wrong",
        role = "implement",
        proposal_id = event.proposal_id,
        dedup_key = event.dedup_key,
        status = "running",
      },
    }, function()
      local signal = core.restart_row_liveness_signal(row, state, facts, facts.now_seconds)
      t.eq(signal.live, false)
      t.eq(signal.expected_dedup_key, retry_version)
    end)
  end,
}

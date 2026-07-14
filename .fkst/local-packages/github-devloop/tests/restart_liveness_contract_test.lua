local devloop_base = require("devloop.base")
local entity_lib = require("devloop.entity")
local h = require("tests.devloop_core_helpers")
local core = h.core
local contract_time = require("contract.time")
local m_builders = require("devloop.markers.builders")
local m_rae = require("devloop.restart_actionable_epoch")
local t = h.t

local function copy_value(value)
  if type(value) ~= "table" then
    return value
  end
  local out = {}
  for key, nested in pairs(value) do
    out[key] = copy_value(nested)
  end
  return out
end

local function copy_rows(rows)
  local copied = {}
  for index, row in ipairs(rows or {}) do
    copied[index] = copy_value(row)
  end
  return copied
end

local function rows_by_state(rows)
  local by_state = {}
  for _, row in ipairs(rows or {}) do
    by_state[row.from_state] = row
  end
  return by_state
end

local function joined_errors(errors)
  return table.concat(errors or {}, "\n")
end

local function contains_error(errors, needle)
  return joined_errors(errors):find(needle, 1, true) ~= nil
end

local function assert_inventory_errors(inventory, state, expected)
  local listed = inventory[state]
  t.eq(type(listed), "table", state)
  local count = 0
  for err, enabled in pairs(listed) do
    t.eq(enabled, true, err)
    t.is_true(expected[err] == true, err)
    count = count + 1
  end
  local expected_count = 0
  for err, _ in pairs(expected) do
    t.is_true(listed[err] == true, err)
    expected_count = expected_count + 1
  end
  t.eq(count, expected_count, state)
end

local function assert_codex_run_row(row, expected_role, state)
  t.eq(row.actionable_epoch.source, "codex_run:v1", state)
  t.eq(row.defer.kind, "codex_run", state)
  t.eq(row.defer.producer, nil, state)
  t.eq(row.defer.live_marker, nil, state)
  t.eq(row.defer.freshness_ms, nil, state)
  t.eq(row.liveness_contract.signal, nil, state)
  t.eq(row.liveness_contract.real_execution.primitive, "fkst.codex_runs", state)
  t.eq(row.liveness_contract.real_execution.match.role, expected_role, state)
  t.eq(row.liveness_contract.real_execution.match.proposal_id, "state.proposal_id", state)
  t.eq(row.liveness_contract.real_execution.match.dedup_key, "state.version", state)
  t.eq(row.liveness_contract.real_execution.status, "running", state)
  t.eq(row.liveness_contract.real_execution.on_error, "defer", state)
  t.eq(row.liveness_contract.real_execution.indeterminate_timeout, "row-budget", state)
  t.eq(row.watchdog.on_stale.op, "redrive_receiver", state)
  t.eq(row.watchdog.on_stale.producer, nil, state)
  t.eq(#core.strict_restart_liveness_contract_errors({ row }), 0, state)
end

local function synthetic_live_defer_row()
  return {
    from_state = "synthetic-live-defer-bad",
    terminal = false,
    liveness_class_id = "synthetic.bad",
    watchdog = {
      mode = "live-defer",
      budget_ms = 45 * 60 * 1000,
    },
    defer = {
      kind = "release_gate",
      live_marker = "synthetic-live:v1",
      freshness_ms = 60 * 60 * 1000,
      clear_fact = "synthetic-clear:v1",
      observed_fact = "synthetic-observed:v1",
      clear_opens_generation = true,
    },
    budget = {
      minutes = 45,
      receiver_max_work_justification = "Synthetic fixture only.",
    },
  }
end

local function synthetic_heartbeat_row()
  return {
    from_state = "synthetic-heartbeat",
    terminal = false,
    liveness_class_id = "synthetic.heartbeat",
    watchdog = {
      mode = "live-defer",
      budget_ms = 45 * 60 * 1000,
      on_stale = {
        op = "redrive_receiver",
        producer = "converge-round",
      },
    },
    actionable_epoch = {
      source = "live_defer_heartbeat:v1",
      generation_source = "same_as_actionable_epoch",
      live_marker = "converge-round:v1",
      producer = "converge-round",
    },
    defer = {
      kind = "heartbeat",
      live_marker = "converge-round:v1",
      producer = "converge-round",
      freshness_ms = 45 * 60 * 1000,
      redrive_opens_generation = true,
    },
    budget = {
      minutes = 45,
      receiver_max_work_justification = "Synthetic fixture only.",
    },
    liveness_contract = {
      mode = "live-defer",
      signal = {
        family = "converge-round",
        producer = "converge-round",
        surface = "issue-comment-stream",
        version_form = "raw",
        max_age_minutes = 45,
      },
    },
  }
end

local function install_generic_restart_liveness_model(row)
  local model = {
    restart_package_name = "synthetic",
    restart_lifecycle_states = { row.from_state },
    is_state = function(state)
      return state == row.from_state or state == "blocked"
    end,
    restart_transition_table = function()
      return { row }
    end,
    restart_durable_marker_fields = function()
      return {
        ["implement-attempt"] = true,
        state = true,
      }
    end,
    restart_responsibility_inventory_errors = function()
      return {}
    end,
  }
  require("workflow.restart_liveness_contract").install(model, {
    workflow_ports = {
      dependency_release_marker = core.dependency_release_marker,
      restart_transition_table = function(...)
        return model.restart_transition_table(...)
      end,
      trusted_bot_login = devloop_base.trusted_bot_login,
    },
  })
  return model
end

return {
  test_primitive_epoch_source_registry_matches_contract = function()
    local sources = core.restart_liveness_epoch_sources()
    t.eq(sources["state_entry:v1"].durable, true)
    t.eq(sources["state_entry:v1"].opens_generation, true)
    t.eq(sources["state_entry:v1"].excludes_deferred_time, false)
    t.eq(sources["state_entry:v1"].allowed_when, "no_defer_possible")
    t.eq(sources["liveness_substate_entry:v1"].durable, true)
    t.eq(sources["liveness_substate_entry:v1"].opens_generation, true)
    t.eq(sources["liveness_substate_entry:v1"].excludes_deferred_time, true)
    t.eq(sources["liveness_substate_entry:v1"].allowed_when, "hierarchical_liveness_substate")
    t.eq(sources["defer_clear_fact:v1"].durable, true)
    t.eq(sources["defer_clear_fact:v1"].opens_generation, true)
    t.eq(sources["defer_clear_fact:v1"].excludes_deferred_time, true)
    t.eq(sources["defer_clear_fact:v1"].requires_clear_fact, true)
    t.eq(sources["live_defer_epoch:v1"].durable, true)
    t.eq(sources["live_defer_epoch:v1"].opens_generation, true)
    t.eq(sources["live_defer_epoch:v1"].excludes_deferred_time, true)
    t.eq(sources["live_defer_epoch:v1"].requires_live_marker, true)
    t.eq(sources["live_defer_epoch:v1"].requires_clear_fact, true)
    t.eq(sources["live_defer_epoch:v1"].requires_observed_fact, true)
    t.eq(sources["live_defer_heartbeat:v1"].durable, true)
    t.eq(sources["live_defer_heartbeat:v1"].opens_generation, "spawn_or_redrive_only")
    t.eq(sources["live_defer_heartbeat:v1"].excludes_deferred_time, true)
    t.eq(sources["live_defer_heartbeat:v1"].requires_live_marker, true)
    t.eq(sources["live_defer_heartbeat:v1"].requires_producer, true)
    t.eq(sources["live_defer_heartbeat:v1"].requires_freshness_ms, true)
    t.eq(sources["live_defer_heartbeat:v1"].requires_redrive_opens_generation, true)
    t.eq(sources["live_defer_heartbeat:v1"].forbids_clear_fact, true)
    t.eq(sources["live_defer_heartbeat:v1"].forbids_observed_fact, true)
    t.eq(sources["live_defer_heartbeat:v1"].forbids_clear_opens_generation, true)
    t.eq(sources["codex_run:v1"].durable, true)
    t.eq(sources["codex_run:v1"].opens_generation, "spawn_or_redrive_only")
    t.eq(sources["codex_run:v1"].excludes_deferred_time, false)
    t.eq(sources["codex_run:v1"].requires_real_execution, true)
    t.eq(sources["codex_run:v1"].real_execution_primitive, "fkst.codex_runs")
    t.eq(sources["codex_run:v1"].forbids_freshness_ms, true)
    t.eq(sources["child_workflow_wait:v1"].durable, true)
    t.eq(sources["child_workflow_wait:v1"].opens_generation, true)
    t.eq(sources["child_workflow_wait:v1"].excludes_deferred_time, true)
    t.eq(sources["child_workflow_wait:v1"].requires_live_marker, true)
    t.eq(sources["child_workflow_wait:v1"].requires_delegation_marker, true)
    t.eq(sources["child_workflow_wait:v1"].requires_terminal_states, true)
  end,

  test_row_budget_rows_declare_state_entry_actionable_epoch = function()
    local by_state = rows_by_state(core.restart_transition_table())
    for _, state in ipairs({ "ready", "impl-failed", "blocked" }) do
      local row = by_state[state]
      t.is_true(type(row.liveness_class_id) == "string" and row.liveness_class_id ~= "", state)
      t.eq(row.watchdog.mode, "row-budget-bounds-receiver")
      t.eq(row.watchdog.budget_ms, row.budget.minutes * 60 * 1000)
      t.eq(row.actionable_epoch.source, "state_entry:v1")
      t.eq(row.actionable_epoch.generation_source, "same_as_actionable_epoch")
      t.eq(row.defer, nil)
    end
  end,

  test_known_liveness_contract_violations_inventory_is_exact = function()
    local inventory = core.known_liveness_contract_violations()
    local count = 0
    for _ in pairs(inventory) do
      count = count + 1
    end
    t.eq(count, 0)
  end,

  test_inventory_ratchet_keeps_main_conformance_green = function()
    t.eq(#core.liveness_contract_errors(), 0)
    local strict = core.strict_restart_liveness_contract_errors()
    for _, state in ipairs({ "implementing", "thinking" }) do
      t.eq(core.liveness_contract_inventory_is_listed_violation(state, strict), false, state)
    end
    t.eq(core.liveness_contract_inventory_is_listed_violation("ready", strict), false)
    t.eq(core.liveness_contract_inventory_is_listed_violation("awaiting-pr", strict), false)
  end,

  test_inventory_ratchet_rejects_unlisted_and_stale_entries = function()
    local rows = copy_rows(core.restart_transition_table())
    local by_state = rows_by_state(rows)
    by_state.ready.actionable_epoch = nil
    local errors = core.restart_liveness_inventory_errors(rows)
    t.is_true(contains_error(errors, "ready: non-terminal row must declare actionable_epoch.source"))

    errors = core.restart_liveness_inventory_errors(core.restart_transition_table(), {
      thinking = {
        ["thinking: live-defer row must declare actionable_epoch.source"] = true,
      },
    })
    t.is_true(contains_error(errors, "thinking: listed known_liveness_contract_violations entry is stale and must be removed"))
  end,

  test_inventory_ratchet_rejects_extra_error_on_listed_state = function()
    local rows = copy_rows(core.restart_transition_table())
    local by_state = rows_by_state(rows)
    by_state.thinking.liveness_class_id = ""
    local errors = core.restart_liveness_inventory_errors(rows)
    t.is_true(contains_error(errors, "thinking: non-terminal row must declare liveness_class_id"))
  end,

  test_dependency_wait_model_fixture_uses_dependency_release_epoch = function()
    local row = rows_by_state(core.restart_transition_table()).dependency_wait
    t.eq(row.defer.kind, "release_gate")
    local errors = core.strict_restart_liveness_contract_errors({ row })
    t.eq(#errors, 0)

    local now_seconds = contract_time.iso_timestamp_epoch_seconds("2026-06-03T10:33:02Z")
    local due, age = core.liveness_timeout_due_with_facts(row, {
      state = "dependency_wait",
      version = "ready/887",
      proposal_id = "github-devloop/issue/owner/repo/887",
      marker_created_at = "2026-06-03T09:45:00Z",
    }, {
      proposal_id = "github-devloop/issue/owner/repo/887",
      current = {
        comments = {
          {
            author_login = "fkst-test-bot",
            created_at = "2026-06-03T09:45:01Z",
            body = core.dependency_wait_marker("github-devloop/issue/owner/repo/887", "ready/887", { 7 }),
          },
          {
            author_login = "fkst-test-bot",
            created_at = "2026-06-03T10:33:00Z",
            body = core.dependency_release_marker("github-devloop/issue/owner/repo/887", "ready/887"),
          },
        },
      },
    }, now_seconds)
    t.eq(due, false)
    t.eq(age, 0)
  end,

  test_awaiting_pr_child_workflow_wait_defers_on_non_terminal_child_state = function()
    local row = rows_by_state(core.restart_transition_table())["awaiting-pr"]
    local now_seconds = contract_time.iso_timestamp_epoch_seconds("2026-06-03T10:33:02Z")
    local parent_proposal_id = "github-devloop/issue/owner/repo/1248"
    local child_pr_proposal_id = entity_lib.pr_proposal_id("owner/repo", 7)
    local version = "ready/1248"
    local due, age = core.liveness_timeout_due_with_facts(row, {
      state = "awaiting-pr",
      version = version,
      proposal_id = parent_proposal_id,
      marker_created_at = "2026-06-03T09:45:00Z",
    }, {
      proposal_id = parent_proposal_id,
      current = {
        comments = {
          {
            author_login = "fkst-test-bot",
            created_at = "2026-06-03T09:45:00Z",
            body = m_builders.pr_delegation_marker(core, parent_proposal_id, child_pr_proposal_id, 7, version, "delegate-owner-repo-7"),
          },
        },
      },
      current_pr = {
        comments = {
          {
            author_login = "fkst-test-bot",
            created_at = "2026-06-03T10:31:00Z",
            body = core.state_marker(parent_proposal_id, "reviewing", version),
          },
          {
            author_login = "fkst-test-bot",
            created_at = "2026-06-03T10:30:00Z",
            body = core.state_marker(entity_lib.pr_proposal_id("owner/repo", 99), "merged", version),
          },
        },
      },
    }, now_seconds)
    t.eq(due, false)
    t.eq(age, nil)
  end,

  test_awaiting_pr_child_workflow_wait_actionable_on_terminal_child_state = function()
    local row = rows_by_state(core.restart_transition_table())["awaiting-pr"]
    local parent_proposal_id = "github-devloop/issue/owner/repo/1248"
    local child_pr_proposal_id = entity_lib.pr_proposal_id("owner/repo", 7)
    local version = "ready/1248"
    local facts = {
      proposal_id = parent_proposal_id,
      current = {
        comments = {
          {
            author_login = "fkst-test-bot",
            created_at = "2026-06-03T09:45:00Z",
            body = m_builders.pr_delegation_marker(core, parent_proposal_id, child_pr_proposal_id, 7, version, "delegate-owner-repo-7"),
          },
        },
      },
      current_pr = {
        comments = {
          {
            author_login = "fkst-test-bot",
            created_at = "2026-06-03T10:30:00Z",
            body = core.state_marker(parent_proposal_id, "merged", version),
          },
        },
      },
    }
    local eval = m_rae.actionable_epoch_resolve(core, row, {
      state = "awaiting-pr",
      version = version,
      proposal_id = parent_proposal_id,
      marker_created_at = "2026-06-03T09:45:00Z",
    }, facts, contract_time.iso_timestamp_epoch_seconds("2026-06-03T10:33:02Z"))
    t.eq(eval.status, "actionable")
    t.eq(eval.epoch_source, "child_workflow_wait:v1")
  end,

  test_runtime_provenance_rejects_declared_source_drift = function()
    local original = core.actionable_epoch_resolve
    core.actionable_epoch_resolve = function(row, state)
      return {
        status = "actionable",
        epoch_ms = contract_time.iso_timestamp_epoch_seconds(state.marker_created_at) * 1000,
        epoch_source = "state_entry:v1",
        generation_key = "bad-generation",
        generation_opened_by = "bad",
        reason = "test drift",
      }
    end
    local ok, errors = pcall(core.strict_restart_liveness_contract_errors, { rows_by_state(core.restart_transition_table()).dependency_wait })
    core.actionable_epoch_resolve = original
    if not ok then
      error(errors)
    end
    t.is_true(contains_error(errors, "dependency_wait: actionable_epoch runtime provenance must match declared source"))
  end,

  test_negative_control_live_defer_without_actionable_epoch_fails = function()
    local errors = core.strict_restart_liveness_contract_errors({ synthetic_live_defer_row() })
    t.is_true(contains_error(errors, "synthetic-live-defer-bad: live-defer row must declare actionable_epoch.source"))
  end,

  test_negative_control_live_defer_with_state_entry_epoch_fails = function()
    local row = synthetic_live_defer_row()
    row.actionable_epoch = {
      source = "state_entry:v1",
      generation_source = "same_as_actionable_epoch",
    }
    local errors = core.strict_restart_liveness_contract_errors({ row })
    t.is_true(contains_error(errors, "synthetic-live-defer-bad: live-defer row declares state_entry epoch source which cannot exclude deferred time"))
    t.is_true(contains_error(errors, "synthetic-live-defer-bad: state_entry:v1 is illegal for live-defer rows because deferred time can accrue before actionability"))
  end,

  test_live_defer_rows_pass_strict_contract = function()
    local by_state = rows_by_state(core.restart_transition_table())
    assert_codex_run_row(by_state.thinking, "consensus", "thinking")
    assert_codex_run_row(by_state.implementing, "implement", "implementing")
  end,

  test_codex_run_defer_rejects_age_based_signal = function()
    local row = copy_value(rows_by_state(core.restart_transition_table()).implementing)
    row.liveness_contract.signal = {
      family = "implement-attempt",
      producer = "implement-attempt",
      max_age_minutes = 45,
    }
    local errors = core.strict_restart_liveness_contract_errors({ row })
    t.is_true(contains_error(errors, "implementing: codex_run defer must not declare liveness_contract.signal"), joined_errors(errors))
    t.is_true(contains_error(errors, "implementing: codex_run defer signal must not declare max_age_minutes"), joined_errors(errors))
  end,

  test_codex_run_defer_rejects_freshness_ms = function()
    local row = copy_value(rows_by_state(core.restart_transition_table()).implementing)
    row.defer.freshness_ms = 60 * 60 * 1000
    local errors = core.strict_restart_liveness_contract_errors({ row })
    t.is_true(contains_error(errors, "implementing: codex_run defer must not declare freshness_ms"), joined_errors(errors))
  end,

  test_blocking_codex_receiver_rejects_self_reported_heartbeat = function()
    local row = copy_value(rows_by_state(core.restart_transition_table()).implementing)
    row.watchdog = {
      mode = "live-defer",
      budget_ms = 45 * 60 * 1000,
      on_stale = {
        op = "redrive_receiver",
        producer = "implement-attempt",
      },
    }
    row.actionable_epoch = {
      source = "live_defer_heartbeat:v1",
      generation_source = "same_as_actionable_epoch",
      live_marker = "implement-attempt:v1",
      producer = "implement-attempt",
    }
    row.defer = {
      kind = "heartbeat",
      live_marker = "implement-attempt:v1",
      producer = "implement-attempt",
      freshness_ms = 45 * 60 * 1000,
      redrive_opens_generation = true,
    }
    row.liveness_contract = {
      mode = "live-defer",
      signal = {
        family = "implement-attempt",
        producer = "implement-attempt",
        surface = "issue-comment-stream",
        version_form = "raw",
        max_age_minutes = 45,
      },
    }
    local errors = core.strict_restart_liveness_contract_errors({ row })
    t.is_true(contains_error(errors, "implementing: blocking spawn_codex_sync receiver must use codex_run:v1 liveness"), joined_errors(errors))
  end,

  test_codex_run_defer_rejects_missing_real_execution = function()
    local row = copy_value(rows_by_state(core.restart_transition_table()).implementing)
    row.liveness_contract.real_execution = nil
    local errors = core.strict_restart_liveness_contract_errors({ row })
    t.is_true(contains_error(errors, "implementing: codex_run defer must declare liveness_contract.real_execution"), joined_errors(errors))
  end,

  test_codex_run_defer_rejects_wrong_real_execution_match = function()
    local row = copy_value(rows_by_state(core.restart_transition_table()).implementing)
    row.liveness_contract.real_execution.primitive = "marker-age"
    row.liveness_contract.real_execution.match.role = ""
    row.liveness_contract.real_execution.match.proposal_id = "marker.proposal"
    row.liveness_contract.real_execution.match.dedup_key = "marker.dedup"
    row.liveness_contract.real_execution.status = "recent"
    row.liveness_contract.real_execution.on_error = "marker-budget-fallback"
    row.liveness_contract.real_execution.indeterminate_timeout = nil
    local errors = core.strict_restart_liveness_contract_errors({ row })
    t.is_true(contains_error(errors, "implementing: codex_run defer real_execution.primitive must be fkst.codex_runs"), joined_errors(errors))
    t.is_true(contains_error(errors, "implementing: codex_run defer real_execution.match.role must be non-empty"), joined_errors(errors))
    t.is_true(contains_error(errors, "implementing: codex_run defer real_execution.match.proposal_id must be state.proposal_id"), joined_errors(errors))
    t.is_true(contains_error(errors, "implementing: codex_run defer real_execution.match.dedup_key must be state.version"), joined_errors(errors))
    t.is_true(contains_error(errors, "implementing: codex_run defer real_execution.status must be running"), joined_errors(errors))
    t.is_true(contains_error(errors, "implementing: codex_run defer real_execution.on_error must be defer"), joined_errors(errors))
    t.is_true(contains_error(errors, "implementing: codex_run defer real_execution.indeterminate_timeout must be row-budget"), joined_errors(errors))
  end,

  test_heartbeat_defer_rejects_clear_fact_shape = function()
    local row = synthetic_heartbeat_row()
    row.defer.clear_fact = "synthetic-clear:v1"
    local errors = core.strict_restart_liveness_contract_errors({ row })
    t.is_true(contains_error(errors, "synthetic-heartbeat: heartbeat defer must not declare clear_fact"))
  end,

  test_release_gate_still_requires_clear_fact_and_passes_when_declared = function()
    local dependency_wait = rows_by_state(core.restart_transition_table()).dependency_wait
    t.eq(#core.strict_restart_liveness_contract_errors({ dependency_wait }), 0)
    local row = copy_value(dependency_wait)
    row.defer.clear_fact = nil
    local errors = core.strict_restart_liveness_contract_errors({ row })
    t.is_true(contains_error(errors, "dependency_wait: release_gate defer must declare durable clear_fact"))
  end,

  test_heartbeat_defer_rejects_missing_redrive_generation = function()
    local row = synthetic_heartbeat_row()
    row.defer.redrive_opens_generation = nil
    local errors = core.strict_restart_liveness_contract_errors({ row })
    t.is_true(contains_error(errors, "synthetic-heartbeat: heartbeat defer.redrive_opens_generation must be true"))
  end,

  test_heartbeat_defer_rejects_missing_stale_redrive = function()
    local row = synthetic_heartbeat_row()
    row.watchdog.on_stale = nil
    local errors = core.strict_restart_liveness_contract_errors({ row })
    t.is_true(contains_error(errors, "synthetic-heartbeat: heartbeat defer must declare watchdog.on_stale.op=redrive_receiver"))
  end,

  test_generic_restart_liveness_requires_injected_policy_for_product_defer_kinds = function()
    local codex_row = copy_value(rows_by_state(core.restart_transition_table()).implementing)
    codex_row.from_state = "synthetic-codex-run"
    local codex_model = install_generic_restart_liveness_model(codex_row)
    local codex_errors = codex_model.strict_restart_liveness_contract_errors({ codex_row })
    t.is_true(
      contains_error(codex_errors, "synthetic-codex-run: policy not injected for defer kind codex_run field primitive"),
      joined_errors(codex_errors)
    )

    local child_row = copy_value(rows_by_state(core.restart_transition_table())["awaiting-pr"])
    child_row.from_state = "synthetic-child-wait"
    local child_model = install_generic_restart_liveness_model(child_row)
    local child_errors = child_model.strict_restart_liveness_contract_errors({ child_row })
    t.is_true(
      contains_error(child_errors, "synthetic-child-wait: policy not injected for defer kind child_workflow_wait field live_marker"),
      joined_errors(child_errors)
    )
  end,
}

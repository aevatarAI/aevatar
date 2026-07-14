local S = {}
local contract_time = require("contract.time")
local liveness_shared = require("workflow.liveness.shared")
local Ports = require("workflow.ports")
local installed = setmetatable({}, { __mode = "k" })

local function installed_function(M, name)
  local binding = installed[M]
  return binding and binding[name] or nil
end

function S.restart_liveness_inventory_errors(M, rows, inventory)
  local fn = installed_function(M, "restart_liveness_inventory_errors")
  if type(fn) ~= "function" then
    error("workflow.restart_liveness_contract: restart_liveness_inventory_errors not installed")
  end
  return fn(rows, inventory)
end

function S.install(M, resolved)
resolved = resolved or {}
local deps = Ports.restart_liveness_contract(resolved)

local epoch_sources = {
  ["state_entry:v1"] = {
    durable = true,
    opens_generation = true,
    excludes_deferred_time = false,
    allowed_when = "no_defer_possible",
  },
  ["liveness_substate_entry:v1"] = {
    durable = true,
    opens_generation = true,
    excludes_deferred_time = true,
    allowed_when = "hierarchical_liveness_substate",
  },
  ["defer_clear_fact:v1"] = {
    durable = true,
    opens_generation = true,
    excludes_deferred_time = true,
    requires_clear_fact = true,
  },
  ["live_defer_epoch:v1"] = {
    durable = true,
    opens_generation = true,
    excludes_deferred_time = true,
    requires_live_marker = true,
    requires_clear_fact = true,
    requires_observed_fact = true,
  },
  ["live_defer_heartbeat:v1"] = {
    durable = true,
    opens_generation = "spawn_or_redrive_only",
    excludes_deferred_time = true,
    requires_live_marker = true,
    requires_producer = true,
    requires_freshness_ms = true,
    requires_redrive_opens_generation = true,
    forbids_clear_fact = true,
    forbids_observed_fact = true,
    forbids_clear_opens_generation = true,
  },
  ["codex_run:v1"] = {
    durable = true,
    opens_generation = "spawn_or_redrive_only",
    -- Deferred time is not yet excluded; bounded by an oversized budget until a no-live-onset epoch is added.
    excludes_deferred_time = false,
    requires_real_execution = true,
    real_execution_primitive = "fkst.codex_runs",
    forbids_freshness_ms = true,
    forbids_clear_fact = true,
    forbids_observed_fact = true,
    forbids_clear_opens_generation = true,
  },
  ["child_workflow_wait:v1"] = {
    durable = true,
    opens_generation = true,
    excludes_deferred_time = true,
    requires_live_marker = true,
    requires_producer = true,
    requires_freshness_ms = true,
    requires_redrive_opens_generation = true,
    requires_delegation_marker = true,
    requires_terminal_states = true,
    forbids_clear_fact = true,
    forbids_observed_fact = true,
    forbids_clear_opens_generation = true,
  },
}

local known_liveness_contract_violations = resolved.known_liveness_contract_violations or {}
local provenance = resolved.runtime_provenance or {}
local codex_run_policy = resolved.codex_run or {}
local child_workflow_wait_policy = resolved.child_workflow_wait or {}
local default_provenance_proposal_id = "workflow/restart-liveness/provenance/1"
local default_provenance_version = "restart-liveness-provenance"
local default_provenance_marker_created_at = "2026-06-03T00:00:00Z"

local function copy_table(map)
  local out = {}
  for key, value in pairs(map or {}) do
    if type(value) == "table" then
      local nested = {}
      for nested_key, nested_value in pairs(value) do
        nested[nested_key] = nested_value
      end
      out[key] = nested
    else
      out[key] = value
    end
  end
  return out
end

local function restart_liveness_epoch_sources()
  return copy_table(epoch_sources)
end
rawset(M, "restart_liveness_epoch_sources", restart_liveness_epoch_sources)

local function known_liveness_contract_violations_fn()
  return copy_table(known_liveness_contract_violations)
end
rawset(M, "known_liveness_contract_violations", known_liveness_contract_violations_fn)

local function state_name(row)
  return tostring(row and (row.from_state or row.state) or "?")
end

local function non_empty_string(value)
  return type(value) == "string" and value ~= ""
end

local function require_policy(policy, kind, fields, errors, state)
  if type(policy) ~= "table" then
    table.insert(errors, state .. ": policy not injected for defer kind " .. kind)
    return nil
  end
  local missing = false
  for _, field in ipairs(fields) do
    if policy[field] == nil then
      missing = true
      table.insert(errors, state .. ": policy not injected for defer kind " .. kind .. " field " .. field)
    end
  end
  if missing then
    return nil
  end
  return policy
end

local function watchdog_budget_ms(row)
  return tonumber(row and row.watchdog and row.watchdog.budget_ms)
end

local function validate_watchdog(row, errors)
  local state = state_name(row)
  local watchdog = row and row.watchdog or nil
  if type(watchdog) ~= "table" then
    table.insert(errors, state .. ": non-terminal row must declare watchdog")
    return nil
  end
  if watchdog.mode ~= "row-budget-bounds-receiver" and watchdog.mode ~= "live-defer" then
    table.insert(errors, state .. ": watchdog.mode must be row-budget-bounds-receiver or live-defer")
  end
  local budget_ms = watchdog_budget_ms(row)
  if budget_ms == nil or budget_ms <= 0 then
    table.insert(errors, state .. ": watchdog.budget_ms must be a positive number")
  end
  local budget_minutes = tonumber(row and row.budget and row.budget.minutes)
  if budget_minutes ~= nil and budget_ms ~= nil and budget_ms ~= budget_minutes * 60 * 1000 then
    table.insert(errors, state .. ": watchdog.budget_ms must match budget.minutes")
  end
  return watchdog
end

local function validate_epoch(row, errors)
  local state = state_name(row)
  local epoch = row and row.actionable_epoch or nil
  if type(epoch) ~= "table" or not non_empty_string(epoch.source) then
    local prefix = "non-terminal row"
    if row and row.watchdog and row.watchdog.mode == "live-defer" then
      prefix = "live-defer row"
    end
    table.insert(errors, state .. ": " .. prefix .. " must declare actionable_epoch.source")
    return nil, nil
  end
  local source = epoch_sources[epoch.source]
  if source == nil then
    table.insert(errors, state .. ": actionable_epoch.source is not registered: " .. tostring(epoch.source))
    return epoch, nil
  end
  if source.durable ~= true then
    table.insert(errors, state .. ": actionable_epoch.source must be durable: " .. tostring(epoch.source))
  end
  if source.opens_generation ~= true and source.opens_generation ~= "spawn_or_redrive_only" then
    table.insert(errors, state .. ": actionable_epoch.source must open a generation: " .. tostring(epoch.source))
  end
  if epoch.generation_source ~= "same_as_actionable_epoch" then
    table.insert(errors, state .. ": actionable_epoch.generation_source must be same_as_actionable_epoch")
  end
  return epoch, source
end

local function validate_release_gate_defer(row, source, errors)
  local state = state_name(row)
  local defer = row and row.defer or nil
  if not non_empty_string(defer.live_marker) then
    table.insert(errors, state .. ": release_gate defer must declare live_marker")
  end
  if tonumber(defer.freshness_ms) == nil or tonumber(defer.freshness_ms) <= 0 then
    table.insert(errors, state .. ": release_gate defer must declare freshness_ms")
  end
  if not non_empty_string(defer.clear_fact) then
    table.insert(errors, state .. ": release_gate defer must declare durable clear_fact")
  end
  if not non_empty_string(defer.observed_fact) then
    table.insert(errors, state .. ": release_gate defer must declare durable observed_fact")
  end
  if defer.clear_opens_generation ~= true then
    table.insert(errors, state .. ": release_gate defer.clear_opens_generation must be true")
  end
  if defer.redrive_opens_generation ~= nil then
    table.insert(errors, state .. ": release_gate defer must not declare redrive_opens_generation")
  end
  local epoch_source = row and row.actionable_epoch and row.actionable_epoch.source
  if epoch_source ~= "live_defer_epoch:v1" and epoch_source ~= "defer_clear_fact:v1" then
    table.insert(errors, state .. ": release_gate defer must use live_defer_epoch:v1 or defer_clear_fact:v1")
  end
  if source ~= nil and source.excludes_deferred_time ~= true then
    table.insert(errors, state .. ": live-defer row declares state_entry epoch source which cannot exclude deferred time")
  end
  if source ~= nil and source.allowed_when == "no_defer_possible" then
    table.insert(errors, state .. ": state_entry:v1 is illegal for live-defer rows because deferred time can accrue before actionability")
  end
end

local function registered_heartbeat_producer(row, defer)
  local signal = row and row.liveness_contract and row.liveness_contract.signal
  if type(signal) ~= "table" then
    return false
  end
  if signal.producer ~= defer.producer then
    return false
  end
  if signal.family ~= defer.producer then
    return false
  end
  local binding = liveness_shared.liveness_signal_producer_contract(M, defer.producer)
  return type(binding) == "table"
end

local function validate_heartbeat_defer(row, errors)
  local state = state_name(row)
  local defer = row and row.defer or nil
  local epoch = row and row.actionable_epoch or nil
  if not non_empty_string(defer.live_marker) then
    table.insert(errors, state .. ": heartbeat defer must declare live_marker")
  end
  if not non_empty_string(defer.producer) then
    table.insert(errors, state .. ": heartbeat defer must declare producer")
  end
  if tonumber(defer.freshness_ms) == nil or tonumber(defer.freshness_ms) <= 0 then
    table.insert(errors, state .. ": heartbeat defer must declare freshness_ms")
  end
  if defer.redrive_opens_generation ~= true then
    table.insert(errors, state .. ": heartbeat defer.redrive_opens_generation must be true")
  end
  if epoch == nil or epoch.source ~= "live_defer_heartbeat:v1" then
    table.insert(errors, state .. ": heartbeat defer must use live_defer_heartbeat:v1")
  end
  if defer.clear_fact ~= nil then
    table.insert(errors, state .. ": heartbeat defer must not declare clear_fact")
  end
  if defer.observed_fact ~= nil then
    table.insert(errors, state .. ": heartbeat defer must not declare observed_fact")
  end
  if defer.clear_opens_generation ~= nil then
    table.insert(errors, state .. ": heartbeat defer must not declare clear_opens_generation")
  end
  local on_stale = row and row.watchdog and row.watchdog.on_stale
  if type(on_stale) ~= "table" or on_stale.op ~= "redrive_receiver" then
    table.insert(errors, state .. ": heartbeat defer must declare watchdog.on_stale.op=redrive_receiver")
  end
  if type(on_stale) == "table" and on_stale.producer ~= nil and on_stale.producer ~= defer.producer then
    table.insert(errors, state .. ": heartbeat defer watchdog.on_stale producer must match defer.producer")
  end
  if not registered_heartbeat_producer(row, defer) then
    table.insert(errors, state .. ": heartbeat defer producer is not a registered live-defer producer: " .. tostring(defer.producer))
  end
end

local function validate_codex_run_defer(row, errors)
  local state = state_name(row)
  local defer = row and row.defer or nil
  local epoch = row and row.actionable_epoch or nil
  local real_execution = row and row.liveness_contract and row.liveness_contract.real_execution or nil
  if defer.freshness_ms ~= nil then
    table.insert(errors, state .. ": codex_run defer must not declare freshness_ms")
  end
  if epoch == nil or epoch.source ~= "codex_run:v1" then
    table.insert(errors, state .. ": codex_run defer must use codex_run:v1")
  end
  if defer.clear_fact ~= nil then
    table.insert(errors, state .. ": codex_run defer must not declare clear_fact")
  end
  if defer.observed_fact ~= nil then
    table.insert(errors, state .. ": codex_run defer must not declare observed_fact")
  end
  if defer.clear_opens_generation ~= nil then
    table.insert(errors, state .. ": codex_run defer must not declare clear_opens_generation")
  end
  local on_stale = row and row.watchdog and row.watchdog.on_stale
  if type(on_stale) ~= "table" or on_stale.op ~= "redrive_receiver" then
    table.insert(errors, state .. ": codex_run defer must declare watchdog.on_stale.op=redrive_receiver")
  end
  local signal = row and row.liveness_contract and row.liveness_contract.signal or nil
  if signal ~= nil then
    table.insert(errors, state .. ": codex_run defer must not declare liveness_contract.signal")
    if type(signal) == "table" and signal.max_age_minutes ~= nil then
      table.insert(errors, state .. ": codex_run defer signal must not declare max_age_minutes")
    end
  end
  local policy = require_policy(codex_run_policy, "codex_run", {
    "primitive",
    "status",
    "on_error",
    "indeterminate_timeout",
  }, errors, state)
  if policy == nil then
    return
  end
  local expected_primitive = policy.primitive
  local expected_status = policy.status
  local expected_on_error = policy.on_error
  local expected_indeterminate_timeout = policy.indeterminate_timeout
  if type(real_execution) ~= "table" then
    table.insert(errors, state .. ": codex_run defer must declare liveness_contract.real_execution")
    return
  end
  if real_execution.primitive ~= expected_primitive then
    table.insert(errors, state .. ": codex_run defer real_execution.primitive must be " .. tostring(expected_primitive))
  end
  local match = real_execution.match
  if type(match) ~= "table" then
    table.insert(errors, state .. ": codex_run defer real_execution.match must declare role, proposal_id, and dedup_key")
    return
  end
  if not non_empty_string(match.role) then
    table.insert(errors, state .. ": codex_run defer real_execution.match.role must be non-empty")
  end
  if match.proposal_id ~= "state.proposal_id" then
    table.insert(errors, state .. ": codex_run defer real_execution.match.proposal_id must be state.proposal_id")
  end
  if match.dedup_key ~= "state.version" then
    table.insert(errors, state .. ": codex_run defer real_execution.match.dedup_key must be state.version")
  end
  if real_execution.status ~= expected_status then
    table.insert(errors, state .. ": codex_run defer real_execution.status must be " .. tostring(expected_status))
  end
  if real_execution.on_error ~= expected_on_error then
    table.insert(errors, state .. ": codex_run defer real_execution.on_error must be " .. tostring(expected_on_error))
  end
  if real_execution.indeterminate_timeout ~= expected_indeterminate_timeout then
    table.insert(errors, state .. ": codex_run defer real_execution.indeterminate_timeout must be " .. tostring(expected_indeterminate_timeout))
  end
end

local function blocking_codex_receiver(row)
  local span = row and row.span_contract or nil
  if type(span) ~= "table" then
    return false
  end
  return non_empty_string(span.spawn_function)
end

local function validate_child_workflow_wait_defer(row, errors)
  local state = state_name(row)
  local defer = row and row.defer or nil
  local epoch = row and row.actionable_epoch or nil
  local signal = row and row.liveness_contract and row.liveness_contract.signal or nil
  if not non_empty_string(defer.live_marker) then
    table.insert(errors, state .. ": child_workflow_wait defer must declare live_marker")
  end
  local policy = require_policy(child_workflow_wait_policy, "child_workflow_wait", {
    "live_marker",
    "delegation_marker",
    "signal_family",
    "signal_resolver",
    "surface",
  }, errors, state)
  if policy == nil then
    return
  end
  local expected_live_marker = policy.live_marker
  local expected_delegation_marker = policy.delegation_marker
  local expected_signal_family = policy.signal_family
  local expected_signal_resolver = policy.signal_resolver
  local expected_surface = policy.surface
  if defer.live_marker ~= expected_live_marker then
    table.insert(errors, state .. ": child_workflow_wait defer live_marker must be " .. tostring(expected_live_marker))
  end
  if not non_empty_string(defer.producer) then
    table.insert(errors, state .. ": child_workflow_wait defer must declare producer")
  end
  if tonumber(defer.freshness_ms) == nil or tonumber(defer.freshness_ms) <= 0 then
    table.insert(errors, state .. ": child_workflow_wait defer must declare freshness_ms")
  end
  if defer.redrive_opens_generation ~= true then
    table.insert(errors, state .. ": child_workflow_wait defer.redrive_opens_generation must be true")
  end
  if not non_empty_string(defer.delegation_marker) then
    table.insert(errors, state .. ": child_workflow_wait defer must declare delegation_marker")
  end
  if defer.delegation_marker ~= expected_delegation_marker then
    table.insert(errors, state .. ": child_workflow_wait defer delegation_marker must be " .. tostring(expected_delegation_marker))
  end
  if type(defer.terminal_states) ~= "table" or #defer.terminal_states == 0 then
    table.insert(errors, state .. ": child_workflow_wait defer must declare terminal_states")
  end
  if epoch == nil or epoch.source ~= "child_workflow_wait:v1" then
    table.insert(errors, state .. ": child_workflow_wait defer must use child_workflow_wait:v1")
  end
  if defer.clear_fact ~= nil then
    table.insert(errors, state .. ": child_workflow_wait defer must not declare clear_fact")
  end
  if defer.observed_fact ~= nil then
    table.insert(errors, state .. ": child_workflow_wait defer must not declare observed_fact")
  end
  if defer.clear_opens_generation ~= nil then
    table.insert(errors, state .. ": child_workflow_wait defer must not declare clear_opens_generation")
  end
  local on_stale = row and row.watchdog and row.watchdog.on_stale
  if type(on_stale) ~= "table" or on_stale.op ~= "redrive_receiver" then
    table.insert(errors, state .. ": child_workflow_wait defer must declare watchdog.on_stale.op=redrive_receiver")
  end
  if type(on_stale) == "table" and on_stale.producer ~= nil and on_stale.producer ~= defer.producer then
    table.insert(errors, state .. ": child_workflow_wait defer watchdog.on_stale producer must match defer.producer")
  end
  if type(signal) ~= "table" then
    table.insert(errors, state .. ": child_workflow_wait defer must declare liveness_contract.signal")
    return
  end
  local resolver = signal.resolver or signal.family
  if signal.family ~= expected_signal_family or resolver ~= expected_signal_resolver or signal.producer ~= defer.producer then
    table.insert(errors, state .. ": child_workflow_wait defer signal must resolve the PR child state marker")
  end
  if signal.surface ~= expected_surface then
    table.insert(errors, state .. ": child_workflow_wait defer signal must use " .. tostring(expected_surface))
  end
  local binding = liveness_shared.liveness_signal_producer_contract(M, signal.producer)
  if type(binding) ~= "table" or binding.resolver ~= expected_signal_resolver then
    table.insert(errors, state .. ": child_workflow_wait defer producer must bind the " .. tostring(expected_signal_resolver) .. " resolver")
  end
end

local function validate_defer(row, source, errors)
  local state = state_name(row)
  local defer = row and row.defer or nil
  if type(defer) ~= "table" then
    table.insert(errors, state .. ": live-defer row must declare defer")
    return
  end
  if defer.kind == "release_gate" then
    validate_release_gate_defer(row, source, errors)
    return
  end
  if defer.kind == "heartbeat" then
    validate_heartbeat_defer(row, errors)
    return
  end
  if defer.kind == "codex_run" then
    validate_codex_run_defer(row, errors)
    return
  end
  if defer.kind == "child_workflow_wait" then
    validate_child_workflow_wait_defer(row, errors)
    return
  end
  table.insert(errors, state .. ": live-defer defer.kind must be release_gate, heartbeat, codex_run, or child_workflow_wait")
end

local function validate_row(row, errors)
  if row == nil or row.terminal == true then
    return
  end
  local state = state_name(row)
  if not non_empty_string(row.liveness_class_id) then
    table.insert(errors, state .. ": non-terminal row must declare liveness_class_id")
  end
  local watchdog = validate_watchdog(row, errors)
  local epoch, source = validate_epoch(row, errors)
  local mode = watchdog and watchdog.mode
  if mode == "live-defer" then
    validate_defer(row, source, errors)
  elseif mode == "row-budget-bounds-receiver" then
    if row.defer ~= nil then
      table.insert(errors, state .. ": row-budget-bounds-receiver row must not declare defer")
    end
  end
  if blocking_codex_receiver(row)
    and not (mode == "live-defer" and epoch ~= nil and epoch.source == "codex_run:v1") then
    table.insert(errors, state .. ": blocking spawn_codex_sync receiver must use codex_run:v1 liveness")
  end
  if epoch ~= nil and epoch.source == "state_entry:v1" then
    if mode == "live-defer" then
      table.insert(errors, state .. ": state_entry:v1 is illegal for live-defer rows because deferred time can accrue before actionability")
    end
    if row.defer ~= nil then
      table.insert(errors, state .. ": state_entry:v1 rows must not declare defer")
    end
  end
end

local function validate_runtime_provenance(row, errors)
  if row == nil or row.terminal == true or type(row.actionable_epoch) ~= "table" then
    return
  end
  if epoch_sources[row.actionable_epoch.source] == nil then
    return
  end
  if type(M.actionable_epoch_resolve) ~= "function" then
    return
  end
  local state = state_name(row)
  local comments = {}
  local now_seconds = 0
  if row.actionable_epoch.source == "live_defer_epoch:v1" then
    now_seconds = contract_time.iso_timestamp_epoch_seconds("2026-06-03T00:00:01Z")
    local proposal_id = provenance.proposal_id or default_provenance_proposal_id
    local version = provenance.version or default_provenance_version
    local marker_created_at = provenance.marker_created_at or default_provenance_marker_created_at
    comments = {
      {
        author_login = deps.ports.trusted_bot_login(),
        created_at = marker_created_at,
        body = deps.ports.dependency_release_marker(proposal_id, version),
      },
    }
  elseif row.actionable_epoch.source == "live_defer_heartbeat:v1" then
    now_seconds = contract_time.iso_timestamp_epoch_seconds("2026-06-03T00:00:01Z")
  elseif row.actionable_epoch.source == "codex_run:v1" then
    now_seconds = contract_time.iso_timestamp_epoch_seconds("2026-06-03T00:00:01Z")
  elseif row.actionable_epoch.source == "child_workflow_wait:v1" then
    now_seconds = contract_time.iso_timestamp_epoch_seconds("2026-06-03T00:00:01Z")
  end
  local ok, eval = pcall(M.actionable_epoch_resolve, row, {
    state = row.from_state,
    version = provenance.version or default_provenance_version,
    proposal_id = provenance.proposal_id or default_provenance_proposal_id,
    marker_created_at = provenance.marker_created_at or default_provenance_marker_created_at,
  }, {
    proposal_id = provenance.proposal_id or default_provenance_proposal_id,
    current = { comments = comments },
  }, now_seconds)
  if not ok or type(eval) ~= "table" then
    table.insert(errors, state .. ": actionable_epoch resolver failed runtime provenance check")
    return
  end
  if eval.status == "actionable" and eval.epoch_source ~= row.actionable_epoch.source then
    table.insert(errors, state .. ": actionable_epoch runtime provenance must match declared source")
  end
end

local function normalized_restart_liveness_rows(rows)
  local normalized = {}
  for _, row in ipairs(rows or deps.ports.restart_transition_table()) do
    table.insert(normalized, row)
  end
  return normalized
end
rawset(M, "normalized_restart_liveness_rows", normalized_restart_liveness_rows)

local function strict_restart_liveness_contract_errors(rows)
  local errors = {}
  for _, row in ipairs(normalized_restart_liveness_rows(rows)) do
    validate_row(row, errors)
    validate_runtime_provenance(row, errors)
  end
  return errors
end
rawset(M, "strict_restart_liveness_contract_errors", strict_restart_liveness_contract_errors)

local function error_state(error_text)
  local state = tostring(error_text or ""):match("^([^:]+):")
  return state
end

local function restart_liveness_inventory_errors(rows, inventory)
  local strict_errors = strict_restart_liveness_contract_errors(rows)
  local listed = inventory or known_liveness_contract_violations
  local observed_listed_errors = {}
  local errors = {}
  for _, err in ipairs(strict_errors) do
    local state = error_state(err)
    local expected = state ~= nil and listed[state] or nil
    if type(expected) == "table" and expected[err] == true then
      observed_listed_errors[err] = true
      goto continue
    end
    table.insert(errors, err)
    ::continue::
  end
  for state, expected_errors in pairs(listed) do
    for expected_error, enabled in pairs(expected_errors or {}) do
      if enabled == true and observed_listed_errors[expected_error] ~= true then
        table.insert(errors, state .. ": listed known_liveness_contract_violations entry is stale and must be removed: " .. expected_error)
      end
    end
  end
  return errors
end
rawset(M, "restart_liveness_inventory_errors", restart_liveness_inventory_errors)

local function liveness_contract_inventory_is_listed_violation(state, errors)
  for _, err in ipairs(errors or {}) do
    if error_state(err) == state then
      return true
    end
  end
  return false
end
rawset(M, "liveness_contract_inventory_is_listed_violation", liveness_contract_inventory_is_listed_violation)

installed[M] = {
  restart_liveness_inventory_errors = restart_liveness_inventory_errors,
}

end

return S

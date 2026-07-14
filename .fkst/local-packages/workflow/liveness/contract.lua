local S = {}
local restart_liveness_contract = require("workflow.restart_liveness_contract")

function S.install(M, shared, resolved)
resolved = resolved or {}
local has_required_table = shared.has_required_table
local valid_budget = shared.valid_budget
local reachable_lifecycle_states = shared.reachable_lifecycle_states
local valid_timeout = shared.valid_timeout
local liveness_resolver_families = shared.liveness_resolver_families
local liveness_signal_producers = shared.liveness_signal_producers
local allowed_signal_surfaces = shared.allowed_signal_surfaces
local signal_max_age_optional_resolvers = shared.signal_max_age_optional_resolvers
local numeric_minutes = shared.numeric_minutes
local liveness_bound_minutes = shared.liveness_bound_minutes
local source_contains = shared.source_contains
local pr_recovery_policy = resolved.pr_recovery or {}

local function validate_restart_totality(M, rows, errors)
  local reachable = reachable_lifecycle_states(M)
  local seen = {}
  for _, row in ipairs(rows or {}) do
    local state = row and row.from_state
    if type(state) ~= "string" or state == "" then
      table.insert(errors, "restart_transition_table: row missing from_state")
    elseif reachable[state] ~= true then
      table.insert(errors, tostring(state) .. ": restart row is not a reachable lifecycle state")
    elseif seen[state] == true then
      table.insert(errors, tostring(state) .. ": duplicate restart_transition_table row")
    else
      seen[state] = true
    end
  end
  for state, _ in pairs(reachable) do
    if seen[state] ~= true then
      table.insert(errors, tostring(state) .. ": reachable lifecycle state is missing a restart_transition_table row")
    end
  end
end

local liveness_contract_margin_minutes = 30

local function validate_liveness_signal_producer(M, state, signal, family, resolver, errors)
  local producer_key = signal.producer
  if type(producer_key) ~= "string" or producer_key == "" then
    table.insert(errors, state .. ": live-defer signal must declare a producer binding")
    return
  end
  local binding = liveness_signal_producers[producer_key]
  if binding == nil then
    table.insert(errors, state .. ": live-defer signal producer binding does not exist: " .. tostring(producer_key))
    return
  end
  local binding_family = binding.marker_family or producer_key
  if binding_family ~= family then
    table.insert(errors, state .. ": live-defer producer binding family mismatch: " .. tostring(producer_key))
  end
  if binding.resolver ~= resolver then
    table.insert(errors, state .. ": live-defer producer binding resolver mismatch: " .. tostring(producer_key))
  end
  if binding.surface ~= signal.surface then
    table.insert(errors, state .. ": live-defer producer binding surface mismatch: " .. tostring(producer_key))
  end
  if binding.version_form ~= signal.version_form then
    table.insert(errors, state .. ": live-defer producer binding version_form mismatch: " .. tostring(producer_key))
  end
  if liveness_resolver_families[resolver] == nil or liveness_resolver_families[resolver][family] ~= true then
    table.insert(errors, state .. ": live-defer resolver does not read marker family: " .. tostring(resolver) .. "/" .. tostring(family))
  end
  if binding.observe_only == true then
    return
  end
  if M.restart_durable_marker_fields()[family] == nil then
    return
  end
  local marker_source = binding.marker_source or "core/requests.lua"
  local request_source = binding.request_source or "core/requests.lua"
  if not (source_contains(binding.producer, binding.marker_builder) or source_contains(marker_source, binding.marker_builder))
    or not source_contains(request_source, binding.request_builder)
    or not source_contains(binding.producer, binding.request_builder) then
    table.insert(errors, state .. ": live-defer producer binding is not reachable from declared producer: " .. tostring(producer_key))
  end
  if not source_contains(binding.producer, binding.queue) then
    table.insert(errors, state .. ": live-defer producer binding does not emit declared queue: " .. tostring(producer_key))
  end
end

local function validate_liveness_signal_shape(M, state, signal, label, errors)
  local family = signal.family
  local resolver = signal.resolver or family
  if type(family) ~= "string" or family == "" then
    table.insert(errors, state .. ": " .. label .. " must declare an existing marker family")
  elseif M.restart_durable_marker_fields()[family] == nil then
    table.insert(errors, state .. ": " .. label .. " marker family does not exist: " .. tostring(family))
  end
  if liveness_resolver_families[resolver] == nil then
    table.insert(errors, state .. ": " .. label .. " has no resolver: " .. tostring(resolver))
  end
  local resolver = signal.resolver or signal.family
  if signal_max_age_optional_resolvers[resolver] == true then
    if signal.max_age_minutes ~= nil then
      table.insert(errors, state .. ": " .. label .. " must not declare max_age_minutes for codex-run liveness")
    end
  elseif numeric_minutes(signal.max_age_minutes) == nil then
    table.insert(errors, state .. ": " .. label .. " must declare finite max_age_minutes")
  end
  if allowed_signal_surfaces[signal.surface] ~= true then
    table.insert(errors, state .. ": " .. label .. " must declare surface")
  end
  if signal.version_form ~= "raw" and signal.version_form ~= "safe_version_segment" then
    table.insert(errors, state .. ": " .. label .. " must declare version_form")
  end
  validate_liveness_signal_producer(M, state, signal, family, resolver, errors)
end

local function validate_real_execution_signal(state, real_execution, errors)
  if type(real_execution) ~= "table" then
    table.insert(errors, state .. ": live-defer codex_run must declare real_execution")
    return
  end
  if real_execution.primitive ~= "fkst.codex_runs" then
    table.insert(errors, state .. ": live-defer codex_run real_execution.primitive must be fkst.codex_runs")
  end
  local match = real_execution.match
  if type(match) ~= "table" then
    table.insert(errors, state .. ": live-defer codex_run real_execution.match must declare role, proposal_id, and dedup_key")
    return
  end
  if type(match.role) ~= "string" or match.role == "" then
    table.insert(errors, state .. ": live-defer codex_run real_execution.match.role must be non-empty")
  end
  if match.proposal_id ~= "state.proposal_id" then
    table.insert(errors, state .. ": live-defer codex_run real_execution.match.proposal_id must be state.proposal_id")
  end
  if match.dedup_key ~= "state.version" then
    table.insert(errors, state .. ": live-defer codex_run real_execution.match.dedup_key must be state.version")
  end
  if real_execution.status ~= "running" then
    table.insert(errors, state .. ": live-defer codex_run real_execution.status must be running")
  end
  if real_execution.on_error ~= "defer" then
    table.insert(errors, state .. ": live-defer codex_run real_execution.on_error must be defer")
  end
end

local function validate_liveness_contract(M, row, errors)
  local state = tostring(row.from_state or "?")
  local contract = row.liveness_contract
  if type(contract) ~= "table" then
    table.insert(errors, state .. ": non-terminal row must declare exactly one liveness_contract")
    return
  end
  local mode = contract.mode
  if mode ~= "row-budget-bounds-receiver" and mode ~= "live-defer" then
    table.insert(errors, state .. ": liveness_contract must declare exactly one supported mode")
    return
  end
  if mode == "row-budget-bounds-receiver" then
    local bound = liveness_bound_minutes(contract)
    if bound == nil then
      table.insert(errors, state .. ": row-budget-bounds-receiver must declare receiver_bound_minutes")
      return
    end
    local budget_minutes = tonumber(row.budget and row.budget.minutes)
    if budget_minutes == nil or budget_minutes < bound + liveness_contract_margin_minutes then
      table.insert(errors, state .. ": budget.minutes must be at least max(declared receiver/external bounds) + margin")
    end
    if contract.progress_signal ~= nil then
      validate_liveness_signal_shape(M, state, contract.progress_signal, "row-budget progress_signal", errors)
      local signal_max_age = numeric_minutes(contract.progress_signal.max_age_minutes)
      if budget_minutes ~= nil and signal_max_age ~= nil and signal_max_age >= budget_minutes then
        table.insert(errors, state .. ": row-budget progress_signal max_age_minutes must be less than budget.minutes")
      end
    end
    return
  end

  if row.actionable_epoch and row.actionable_epoch.source == "codex_run:v1" then
    if contract.signal ~= nil then
      table.insert(errors, state .. ": live-defer codex_run must not declare marker signal")
    end
    validate_real_execution_signal(state, contract.real_execution, errors)
    return
  end

  local signal = contract.signal
  if type(signal) ~= "table" then
    table.insert(errors, state .. ": live-defer must declare a signal")
    return
  end
  validate_liveness_signal_shape(M, state, signal, "live-defer signal", errors)
end

	local function liveness_contract_errors(rows)
	  local errors = {}
	  local table_rows = rows or M.restart_transition_table()
  validate_restart_totality(M, table_rows, errors)
  for _, row in ipairs(table_rows) do
    if type(row.from_state) ~= "string" or row.from_state == "" then
      table.insert(errors, "row: missing from_state")
    end
    if type(row.terminal) ~= "boolean" then
      table.insert(errors, tostring(row.from_state or "?") .. ": terminal must be boolean")
    end
    if row.terminal == true then
      if row.output_obligation ~= nil then
        table.insert(errors, tostring(row.from_state or "?") .. ": terminal row must not declare output_obligation")
      end
    else
      if not has_required_table(row, "output_obligation") then
        table.insert(errors, tostring(row.from_state or "?") .. ": non-terminal row must declare output_obligation")
      end
      if not valid_budget(row) then
        table.insert(errors, tostring(row.from_state or "?") .. ": non-terminal row must declare a positive budget with receiver_max_work_justification")
      end
      if not valid_timeout(row) then
        table.insert(errors, tostring(row.from_state or "?") .. ": non-terminal row must declare redrive on_timeout for its driving queue plus force-terminate on_escalate to blocked")
      end
      if type(row.observe_surfaces) ~= "table" or next(row.observe_surfaces) == nil then
        table.insert(errors, tostring(row.from_state or "?") .. ": non-terminal row must declare observe_surfaces")
      else
        for surface, enabled in pairs(row.observe_surfaces) do
          if surface ~= "issue" and surface ~= "pr" and surface ~= "liveness_scan" then
            table.insert(errors, tostring(row.from_state or "?") .. ": unsupported observe surface " .. tostring(surface))
          end
          if enabled ~= true then
            table.insert(errors, tostring(row.from_state or "?") .. ": observe surface must be true: " .. tostring(surface))
          end
        end
      end
      if row.pr_recovery ~= nil then
        if type(row.pr_recovery) ~= "table" then
          table.insert(errors, tostring(row.from_state or "?") .. ": pr_recovery must be a table")
        else
          local allowed = pr_recovery_policy.allowed or {}
          for name, recovery in pairs(row.pr_recovery) do
            local policy = allowed[name]
            if policy == nil then
              table.insert(errors, tostring(row.from_state or "?") .. ": unsupported pr_recovery " .. tostring(name))
            elseif type(recovery) ~= "table"
              or recovery.to_state ~= policy.to_state
              or recovery.queue ~= policy.queue then
              table.insert(errors, tostring(row.from_state or "?") .. ": " .. tostring(name) .. " pr_recovery must target " .. tostring(policy.to_state) .. " via " .. tostring(policy.queue))
            end
          end
        end
      end
      if row.timeout_surfaces ~= nil then
        if type(row.timeout_surfaces) ~= "table" then
          table.insert(errors, tostring(row.from_state or "?") .. ": timeout_surfaces must be a table")
        else
          for surface, enabled in pairs(row.timeout_surfaces) do
            if surface ~= "issue" and surface ~= "issue_liveness_scan" and surface ~= "pr" and surface ~= "liveness_scan" then
              table.insert(errors, tostring(row.from_state or "?") .. ": unsupported timeout surface " .. tostring(surface))
            end
            if enabled ~= true then
              table.insert(errors, tostring(row.from_state or "?") .. ": timeout surface must be true: " .. tostring(surface))
            end
          end
        end
      end
      validate_liveness_contract(M, row, errors)
      if (type(row.to_states) ~= "table" or #row.to_states == 0)
        and (type(row.reentry_commands) ~= "table" or #row.reentry_commands == 0) then
        table.insert(errors, tostring(row.from_state or "?") .. ": non-terminal row must declare at least one next state")
      end
    end
    for _, next_state in ipairs(row.to_states or {}) do
      if M.is_state ~= nil and not M.is_state(next_state) then
        table.insert(errors, tostring(row.from_state or "?") .. ": unknown next state " .. tostring(next_state))
      end
    end
	  end
	  if #errors == 0 then
	    for _, inventory_errors in ipairs({
	      restart_liveness_contract.restart_liveness_inventory_errors(M, table_rows),
	      M.restart_responsibility_inventory_errors(table_rows),
	    }) do
	      for _, err in ipairs(inventory_errors) do table.insert(errors, err) end
	    end
	  end
	  return errors
	end
	rawset(M, "liveness_contract_errors", liveness_contract_errors)
	
	local function liveness_terminal_states(rows)
	  local terminals = {}
	  for _, row in ipairs(rows or M.restart_transition_table()) do
    if row.terminal == true then
      table.insert(terminals, row.from_state)
    end
	  end
	  return terminals
	end
	rawset(M, "liveness_terminal_states", liveness_terminal_states)
	
	local function issue_marker_liveness_sweep_states(rows)
	  local states = {}
	  for _, row in ipairs(rows or M.restart_transition_table()) do
    if row.terminal == false then
      states[row.from_state] = true
    end
	  end
	  return states
	end
	rawset(M, "issue_marker_liveness_sweep_states", issue_marker_liveness_sweep_states)
	
	local function issue_marker_liveness_sweep_contract_errors(rows, sweep_states)
	  local errors = {}
	  local declared_states = sweep_states or issue_marker_liveness_sweep_states(rows)
	  for _, row in ipairs(rows or M.restart_transition_table()) do
    if row.terminal == false and declared_states[row.from_state] ~= true then
      table.insert(errors, tostring(row.from_state or "?") .. ": non-terminal issue-marker state is not reachable by liveness sweep")
    end
    if row.terminal == true and declared_states[row.from_state] == true then
      table.insert(errors, tostring(row.from_state or "?") .. ": terminal issue-marker state must not be re-driven by liveness sweep")
    end
	  end
	  return errors
	end
	rawset(M, "issue_marker_liveness_sweep_contract_errors", issue_marker_liveness_sweep_contract_errors)

end

return S

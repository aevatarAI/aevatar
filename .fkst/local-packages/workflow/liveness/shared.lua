local S = {}
local contract_time = require("contract.time")
local transition_version = require("contract.transition_version")
local installed = setmetatable({}, { __mode = "k" })

local function strip_liveness_timeout_suffixes(version)
  local text = tostring(version or "")
  local previous = nil
  while previous ~= text do
    previous = text
    text = text
      :gsub("/timeout%-reconcile/[%w%-]+/%d+$", "")
      :gsub("%-timeout%-reconcile%-[%w%-]+%-%d+$", "")
      :gsub("/timeout/[%w%-]+/%d+$", "")
      :gsub("%-timeout%-[%w%-]+%-%d+$", "")
  end
  return text
end

local function liveness_heartbeat_version(version, contract)
  local heartbeat_version = strip_liveness_timeout_suffixes(version)
  if contract and contract.version_form == "safe_version_segment" then
    return transition_version.safe_version_segment(heartbeat_version)
  end
  return transition_version.strip_suffixes(heartbeat_version)
end

function S.liveness_signal_producer_contract(M, family)
  local binding = installed[M]
  if binding == nil then
    return nil
  end
  return binding.liveness_signal_producer_contract(family)
end

function S.liveness_heartbeat_version(_M, version, contract)
  return liveness_heartbeat_version(version, contract)
end

function S.install(M, resolved)
resolved = resolved or {}
local shared = {}
local max_timeout_attempts = 3
shared.max_timeout_attempts = max_timeout_attempts
local restart_package_name_value = resolved.restart_package_name
local restart_source_root_value = resolved.restart_source_root

local function has_required_table(row, field)
  return type(row[field]) == "table" and next(row[field]) ~= nil
end
shared.has_required_table = has_required_table

local function valid_budget(row)
  return type(row.budget) == "table"
    and tonumber(row.budget.minutes) ~= nil
    and tonumber(row.budget.minutes) > 0
    and type(row.budget.receiver_max_work_justification) == "string" and row.budget.receiver_max_work_justification ~= ""
end
shared.valid_budget = valid_budget

local function reachable_lifecycle_states(M)
  local function add_to(seen, state)
    if type(state) == "string" and state ~= "" and state ~= "unmanaged" then
      seen[state] = true
    end
  end
  if type(M.restart_lifecycle_states) == "table" then
    local scoped = {}
    for _, state in ipairs(M.restart_lifecycle_states) do
      add_to(scoped, state)
    end
    return scoped
  end
  if type(M.lifecycle_state_set) == "function" then
    return M.lifecycle_state_set()
  end
  local seen = {}
  local function add(state)
    add_to(seen, state)
  end
  return seen
end
shared.reachable_lifecycle_states = reachable_lifecycle_states

local function valid_timeout(row)
  if type(row.on_timeout) ~= "table" then
    return false
  end
  if row.on_timeout.action ~= "redrive" or row.on_timeout.queue ~= row.driving_queue then
    return false
  end
  if tonumber(row.on_timeout.escalate_after_attempts) == nil
    or tonumber(row.on_timeout.escalate_after_attempts) <= 0 then
    return false
  end
  local terminal = row.on_timeout.on_escalate
  return type(terminal) == "table"
    and terminal.action == "force-terminate" and terminal.terminal_state == "blocked"
    and type(terminal.reason) == "string" and terminal.reason ~= ""
end
shared.valid_timeout = valid_timeout

local package_name = restart_package_name_value or "workflow"
local liveness_resolver_families = resolved.liveness_resolver_families or {}
shared.liveness_resolver_families = liveness_resolver_families

local liveness_signal_producers = assert(resolved.liveness_signal_producers, package_name .. ": missing resolved liveness_signal_producers")
shared.liveness_signal_producers = liveness_signal_producers
shared.allowed_signal_surfaces = resolved.allowed_signal_surfaces or {}
shared.signal_max_age_optional_resolvers = resolved.signal_max_age_optional_resolvers or {}

local function liveness_signal_producer_contract(family)
  return liveness_signal_producers[tostring(family or "")]
end
shared.liveness_signal_producer_contract = liveness_signal_producer_contract
rawset(M, "liveness_signal_producer_contract", liveness_signal_producer_contract)

shared.strip_liveness_timeout_suffixes = strip_liveness_timeout_suffixes

shared.liveness_heartbeat_version = liveness_heartbeat_version
rawset(M, "liveness_heartbeat_version", liveness_heartbeat_version)
installed[M] = {
  liveness_signal_producer_contract = liveness_signal_producer_contract,
}

local function numeric_minutes(value)
  local minutes = tonumber(value)
  if minutes == nil or minutes <= 0 then
    return nil
  end
  return minutes
end
shared.numeric_minutes = numeric_minutes

local function non_negative_minutes(value)
  local minutes = tonumber(value)
  if minutes == nil or minutes < 0 then
    return nil
  end
  return minutes
end
shared.non_negative_minutes = non_negative_minutes

local function liveness_bound_minutes(contract)
  local receiver = non_negative_minutes(contract and contract.receiver_bound_minutes)
  local external = non_negative_minutes(contract and contract.external_wait_bound_minutes)
  if receiver == nil then
    return nil
  end
  if external ~= nil and external > receiver then
    return external
  end
  return receiver
end
shared.liveness_bound_minutes = liveness_bound_minutes

local function source_contains(path, needle)
  if type(path) ~= "string" or path == "" or type(needle) ~= "string" or needle == "" then
    return false
  end
  local source_path = path
  if path:sub(1, 10) ~= "libraries/" then
    source_path = tostring(restart_source_root_value or "") .. path
  end
  local ok, text = pcall(file.read, source_path)
  return ok and tostring(text or ""):find(needle, 1, true) ~= nil
end
shared.source_contains = source_contains

local function signal_age_from_created_at(M, created_at, now_seconds)
  local created_seconds = contract_time.iso_timestamp_epoch_seconds(created_at)
  local current_seconds = tonumber(now_seconds)
  if created_seconds ~= nil and current_seconds ~= nil and current_seconds >= created_seconds then
    return math.floor((current_seconds - created_seconds) / 60)
  end
  return nil
end
shared.signal_age_from_created_at = signal_age_from_created_at

local function marker_attr(marker, name)
  return tostring(marker or ""):match(name .. '="([^"]*)"')
end
shared.marker_attr = marker_attr

local function liveness_contract_signal(contract)
  if type(contract) ~= "table" then
    return nil
  end
  if contract.mode == "live-defer" then
    return contract.signal
  end
  if contract.mode == "row-budget-bounds-receiver" then
    return contract.progress_signal
  end
  return nil
end
shared.liveness_contract_signal = liveness_contract_signal

local function row_liveness_signal(row)
  return liveness_contract_signal(row and row.liveness_contract)
end
shared.row_liveness_signal = row_liveness_signal

return shared
end

return S

local sweep_bounds = {}
local gh_exec = require("devloop.gh_exec")
local sweep = require("workflow.sweep")

local default_call_timeout = 10
local default_wall_clock_budget = 90

function sweep_bounds.sweep_deadline(now_seconds, limits)
  local base = tonumber(now_seconds) or now()
  local budget = sweep.positive_integer(limits and limits.wall_clock_budget, default_wall_clock_budget, 1, 3600)
  return base + budget
end

function sweep_bounds.sweep_remaining_seconds(deadline)
  local remaining = math.floor((tonumber(deadline) or 0) - now())
  if remaining < 1 then
    return 0
  end
  return remaining
end

function sweep_bounds.sweep_call_timeout(limits, deadline)
  local configured = sweep.positive_integer(limits and limits.call_timeout, default_call_timeout, 1, 300)
  local remaining = sweep_bounds.sweep_remaining_seconds(deadline)
  if remaining == 0 then
    return 0
  end
  if remaining < configured then
    return remaining
  end
  return configured
end

function sweep_bounds.sweep_has_budget(deadline)
  return sweep_bounds.sweep_remaining_seconds(deadline) > 0
end

function sweep_bounds.sweep_exec(cmd_or_opts, limits, deadline, error_class, exec)
  local timeout = sweep_bounds.sweep_call_timeout(limits, deadline)
  if timeout <= 0 then
    return sweep_bounds.sweep_deadline_deferred_result(error_class)
  end
  if type(exec) == "function" then
    local opts
    if type(cmd_or_opts) == "table" then
      opts = {}
      for key, value in pairs(cmd_or_opts) do
        opts[key] = value
      end
      opts.timeout = opts.timeout or timeout
    else
      opts = { cmd = cmd_or_opts, timeout = timeout }
    end
    return exec(opts)
  end
  if type(cmd_or_opts) == "table" and type(cmd_or_opts.run) == "function" then
    return cmd_or_opts.run(cmd_or_opts.timeout or timeout)
  end
  local opts
  if type(cmd_or_opts) == "table" then
    opts = {}
    for key, value in pairs(cmd_or_opts) do
      opts[key] = value
    end
    opts.timeout = opts.timeout or timeout
  else
    opts = { cmd = cmd_or_opts, timeout = timeout }
  end
  return gh_exec.gh_exec(opts, nil, exec)
end

function sweep_bounds.sweep_run_cmd(cmd, limits, deadline, error_class, exec)
  local result = sweep_bounds.sweep_exec(cmd, limits, deadline, error_class, exec)
  if sweep_bounds.sweep_result_deferred(result) then
    return result
  end
  if result.exit_code ~= 0 then
    error("github-devloop: " .. tostring(error_class or "sweep command") .. " failed: " .. tostring(result.stderr))
  end
  return result
end

function sweep_bounds.sweep_rotation_seed(event)
  if event and event.ts ~= nil then
    return tostring(event.ts)
  end
  local payload = event and event.payload
  if type(payload) == "table" then
    for _, key in ipairs({ "tick", "generated_at", "ts" }) do
      if payload[key] ~= nil then
        return tostring(payload[key])
      end
    end
  end
  return tostring(math.floor(now() / 60))
end

sweep_bounds.sweep_rotation_offset = sweep.rotation_offset

function sweep_bounds.sweep_rotate(items, seed)
  local source = items or {}
  local count = #source
  if count <= 1 then
    local copy = {}
    for _, item in ipairs(source) do
      table.insert(copy, item)
    end
    return copy
  end
  local offset = sweep_bounds.sweep_rotation_offset(count, seed)
  local rotated = {}
  for i = 1, count do
    local index = ((offset + i - 1) % count) + 1
    table.insert(rotated, source[index])
  end
  return rotated
end

function sweep_bounds.sweep_batch(items, seed, cap, default_cap)
  local source = items or {}
  local bounded_cap = sweep.positive_integer(cap, default_cap or 25, 1, 1000)
  if #source <= bounded_cap then
    local all_items = {}
    for _, item in ipairs(source) do
      table.insert(all_items, item)
    end
    return all_items, 0
  end
  local rotated = sweep_bounds.sweep_rotate(source, seed)
  local selected = {}
  for i, item in ipairs(rotated) do
    if i > bounded_cap then
      break
    end
    table.insert(selected, item)
  end
  return selected, math.max(0, #source - #selected)
end

sweep_bounds.sweep_cursor_batch = sweep.cursor_batch
sweep_bounds.sweep_cursor_advance = sweep.cursor_advance
sweep_bounds.sweep_deadline_deferred_result = sweep.deadline_deferred_result
sweep_bounds.sweep_result_deferred = sweep.result_deferred
sweep_bounds.sweep_positive_integer = sweep.positive_integer

return sweep_bounds

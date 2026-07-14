local M = {}

local allowed_lineage_fields = {
  proposal_id = true,
  issue_number = true,
  impl_version = true,
  branch = true,
  base_branch = true,
}

local facts_caps = setmetatable({}, { __mode = "k" })
local facts_methods = {}

function facts_methods.reached(self, milestone, opts)
  local caps = facts_caps[self]
  if caps == nil then
    error("devloop.gate: invalid facts capability")
  end
  return caps.reached(milestone, opts) == true
end

function facts_methods.lineage_equals(self, field, expected)
  local caps = facts_caps[self]
  if caps == nil then
    error("devloop.gate: invalid facts capability")
  end
  return caps.lineage_equals(field, expected) == true
end

local facts_meta = {
  __index = facts_methods,
  __newindex = function()
    error("devloop.gate: facts capability is read-only")
  end,
  __metatable = "devloop.gate.facts",
}

local function copy_lineage(lineage)
  if lineage == nil then
    return nil
  end
  if type(lineage) ~= "table" or getmetatable(lineage) ~= nil then
    error("devloop.gate: lineage must be a plain data table")
  end
  local copied = {}
  for field, required in pairs(lineage) do
    if allowed_lineage_fields[field] ~= true then
      error("devloop.gate: unsupported lineage field")
    end
    if required ~= true then
      error("devloop.gate: lineage requirements must be positive")
    end
    copied[field] = true
  end
  return copied
end

local function copy_opts(opts)
  if opts == nil then
    return {}
  end
  if type(opts) ~= "table" or getmetatable(opts) ~= nil then
    error("devloop.gate: options must be a plain data table")
  end
  local copied = {}
  for key, value in pairs(opts) do
    if key == "domain" or key == "milestone_domain" then
      copied[key] = tostring(value)
    elseif key == "lineage" then
      copied.lineage = copy_lineage(value)
    else
      error("devloop.gate: unsupported gate option")
    end
  end
  return copied
end

local function assert_no_smuggled_executable(value, seen)
  local value_type = type(value)
  if value_type == "function" or value_type == "thread" or value_type == "userdata" then
    error("devloop.gate: gate spec must be data-only")
  end
  if value_type ~= "table" then
    return
  end
  if getmetatable(value) ~= nil then
    error("devloop.gate: gate spec must not carry metatables")
  end
  seen = seen or {}
  if seen[value] then
    return
  end
  seen[value] = true
  for key, nested in pairs(value) do
    assert_no_smuggled_executable(key, seen)
    assert_no_smuggled_executable(nested, seen)
  end
end

local function assert_allowed_keys(value, allowed)
  for key in pairs(value) do
    if allowed[key] ~= true then
      error("devloop.gate: gate spec has non-AST fields")
    end
  end
end

local function assert_dense_gate_list(gates)
  if type(gates) ~= "table" or getmetatable(gates) ~= nil then
    error("devloop.gate: all gate requires a plain gate list")
  end
  local count = 0
  local max_index = 0
  for key in pairs(gates) do
    if type(key) ~= "number" or key < 1 or key % 1 ~= 0 then
      error("devloop.gate: all gate list must use contiguous integer indexes")
    end
    count = count + 1
    if key > max_index then
      max_index = key
    end
  end
  if count == 0 then
    error("devloop.gate: all gate list must not be empty")
  end
  if count ~= max_index then
    error("devloop.gate: all gate list must be dense")
  end
end

local function copy_dense_gate_list(gates)
  assert_dense_gate_list(gates)
  local copied = {}
  for index = 1, #gates do
    copied[index] = gates[index]
  end
  return copied
end

local function plain_gate_table(value)
  if type(value) ~= "table" then
    return value
  end
  -- restricted_lua_load preserves dense-array intent with an engine-owned
  -- metatable; gate specs treat that marker as plain list data.
  local copied = {}
  for key, nested in pairs(value) do
    copied[plain_gate_table(key)] = plain_gate_table(nested)
  end
  return copied
end

local function restricted_require_reached(milestone, opts)
  return M.require_reached(milestone, plain_gate_table(opts))
end

local function restricted_all(gates)
  return M.all(plain_gate_table(gates))
end

local function assert_spec_shape(spec, seen)
  if type(spec) ~= "table" or getmetatable(spec) ~= nil then
    error("devloop.gate: gate spec must be a plain data table")
  end
  seen = seen or {}
  if seen[spec] then
    return
  end
  seen[spec] = true
  if spec.op == "all" then
    assert_allowed_keys(spec, { op = true, gates = true })
    assert_dense_gate_list(spec.gates)
    for index = 1, #spec.gates do
      assert_spec_shape(spec.gates[index], seen)
    end
    return
  end
  if spec.op == "reached" then
    assert_allowed_keys(spec, { op = true, milestone = true, opts = true })
    if type(spec.milestone) ~= "string" or spec.milestone == "" then
      error("devloop.gate: reached gate requires a milestone")
    end
    copy_opts(spec.opts)
    return
  end
  error("devloop.gate: unsupported gate operation")
end

local function assert_loaded_gate_spec(spec)
  assert_no_smuggled_executable(spec)
  assert_spec_shape(spec)
  return spec
end

local function gate_key(name)
  if type(name) ~= "string" or name:match("^[A-Za-z_][A-Za-z0-9_]*$") == nil then
    error("devloop.gate: gate name must be a safe segment")
  end
  return name
end

local function gate_bindings()
  return {
    require_reached = restricted_require_reached,
    all = restricted_all,
    type = type,
    tostring = tostring,
    tonumber = tonumber,
    select = select,
    error = error,
    assert = assert,
  }
end

local function load_gate_source(source, chunk_name)
  if type(restricted_lua_load) ~= "function" then
    error("devloop.gate: restricted_lua_load SDK is required to load gate definitions")
  end
  local ok, spec_or_error = pcall(restricted_lua_load, {
    source = source,
    bindings = gate_bindings(),
    mode = "text",
    name = chunk_name,
  })
  if not ok then
    error("devloop.gate: gate definition load failed: " .. tostring(spec_or_error))
  end
  return assert_loaded_gate_spec(plain_gate_table(spec_or_error))
end

local function reached_opts_for_facts(opts)
  local copied = {}
  if opts.domain ~= nil then
    copied.domain = opts.domain
  end
  if opts.milestone_domain ~= nil then
    copied.milestone_domain = opts.milestone_domain
  end
  return copied
end

local function binding_value(bindings, field)
  if type(bindings) ~= "table" or getmetatable(bindings) ~= nil then
    error("devloop.gate: bindings must be a plain data table")
  end
  local value = bindings[field]
  local value_type = type(value)
  if value_type == "nil" then
    return nil
  end
  if value_type == "table" or value_type == "function" or value_type == "thread" or value_type == "userdata" then
    error("devloop.gate: binding values must be scalar")
  end
  return value
end

local function lineage_holds(facts, opts, bindings)
  for field, required in pairs(opts.lineage or {}) do
    if required == true then
      local expected = binding_value(bindings, field)
      if expected == nil or not facts:lineage_equals(field, expected) then
        return false
      end
    end
  end
  return true
end

local function eval(spec, facts, bindings)
  if spec.op == "all" then
    for _, child in ipairs(spec.gates or {}) do
      if not eval(child, facts, bindings) then
        return false
      end
    end
    return true
  end
  if spec.op == "reached" then
    local opts = copy_opts(spec.opts)
    if not lineage_holds(facts, opts, bindings) then
      return false
    end
    return facts:reached(spec.milestone, reached_opts_for_facts(opts))
  end
  error("devloop.gate: unsupported gate operation")
end

function M.install(resolved)
  if type(resolved) ~= "table" then
    error("devloop.gate: resolved gate sources must be a table")
  end
  M._resolved_gate_sources = resolved.sources or {}
  M._resolved_gate_specs = resolved.specs or {}
end

function M.facts(caps)
  if type(caps) ~= "table" or type(caps.reached) ~= "function" or type(caps.lineage_equals) ~= "function" then
    error("devloop.gate: facts requires reached and lineage_equals capabilities")
  end
  local object = {}
  facts_caps[object] = {
    reached = caps.reached,
    lineage_equals = caps.lineage_equals,
  }
  return setmetatable(object, facts_meta)
end

function M.require_reached(milestone, opts)
  if type(milestone) ~= "string" or milestone == "" then
    error("devloop.gate: milestone is required")
  end
  return {
    op = "reached",
    milestone = milestone,
    opts = copy_opts(opts),
  }
end

function M.all(gates)
  return {
    op = "all",
    gates = copy_dense_gate_list(gates),
  }
end

function M.load_gate(name)
  local key = gate_key(name)
  local spec = M._resolved_gate_specs and M._resolved_gate_specs[key]
  if spec ~= nil then
    return assert_loaded_gate_spec(spec)
  end
  local source = M._resolved_gate_sources and M._resolved_gate_sources[key]
  if type(source) ~= "string" or source == "" then
    error("devloop.gate: gate definition not resolved: " .. key)
  end
  return load_gate_source(source, "@gate:" .. key)
end

if fkst ~= nil and fkst.test ~= nil then
  function M._load_gate_source_for_test(source)
    return load_gate_source(source, "@devloop.gate.test")
  end
end

function M.holds(spec, facts, bindings)
  if facts_caps[facts] == nil then
    error("devloop.gate: holds requires an opaque facts capability")
  end
  assert_loaded_gate_spec(spec)
  return eval(spec, facts, bindings or {})
end

return M

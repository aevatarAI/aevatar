-- devloop.di.select_caps: the strict sealed capability projector.
--
-- This is the guard that keeps make_department(caps) DI from decaying back into a service-locator
-- (a renamed god-table). The composition root may hold `all_caps` with every capability, but a
-- department must receive ONLY the capabilities it declared in `spec.caps.requires`, and the
-- projected table is SEALED: reading an undeclared capability or mutating the table errors loudly
-- rather than silently handing back ambient authority. See
-- docs/superpowers/specs/2026-07-02-di-refactor-retire-ambient-m-design.md.

local M = {}

-- Wildcard / god-bundle capability requests are forbidden: they are just the service-locator
-- under a new name. A department must enumerate the narrow roles it needs.
local FORBIDDEN_DEPS = { ["*"] = true, ["core"] = true, all = true, services = true, devloop = true }

local function get_path(root, path)
  local cur = root
  for part in string.gmatch(path, "[^.]+") do
    if type(cur) ~= "table" then
      return nil
    end
    cur = cur[part]
  end
  return cur
end

local function set_path(root, path, value)
  local parts = {}
  for part in string.gmatch(path, "[^.]+") do
    parts[#parts + 1] = part
  end
  local cur = root
  for i = 1, #parts - 1 do
    local part = parts[i]
    cur[part] = cur[part] or {}
    cur = cur[part]
  end
  cur[parts[#parts]] = value
end

-- Seal `data` behind an EMPTY proxy table: because the proxy has no keys of its own, every read
-- routes through __index (erroring on undeclared capabilities) and every write routes through
-- __newindex (erroring). Sealing the data table in place would only trap NEW keys -- Lua skips
-- __newindex when the key already exists -- so overwriting a declared capability would silently
-- succeed. The proxy makes the projection genuinely read-only.
local function seal_table(data, label)
  return setmetatable({}, {
    __index = function(_, key)
      local value = data[key]
      if value == nil then
        error("undeclared capability access: " .. label .. "." .. tostring(key), 2)
      end
      return value
    end,
    __newindex = function()
      error("capability table is read-only: " .. label, 2)
    end,
    -- Hide the metatable so department code cannot get/setmetatable to unseal the projection.
    -- This guards accidental service-locator drift; it is not a defence against deliberate
    -- rawset/debug tampering (department code is our own, not untrusted).
    __metatable = "sealed",
  })
end

-- project(all_caps, cap_deps, opts) -> a sealed table exposing only the declared cap_deps.
-- opts = { department = <name> } for error context. Each dep is a dotted role path (e.g.
-- "state.cas"); missing or forbidden deps error at construction time (fail-closed).
function M.project(all_caps, cap_deps, opts)
  opts = opts or {}
  local department = opts.department or "?"
  assert(type(cap_deps) == "table", "cap_deps must be a table for department " .. department)

  local out = {}
  for _, path in ipairs(cap_deps) do
    if type(path) ~= "string" or FORBIDDEN_DEPS[path] then
      error("forbidden wildcard/god capability dependency '" .. tostring(path)
        .. "' in department " .. department .. "; declare narrow role capabilities instead", 2)
    end
    local value = get_path(all_caps, path)
    if value == nil then
      error("missing capability '" .. path .. "' for department " .. department, 2)
    end
    set_path(out, path, value)
  end

  -- Seal nested namespaces first (so caps.state.<undeclared> also errors), then the root. The
  -- proxy is a new table, so reassign it into the backing data before sealing the root.
  for key, value in pairs(out) do
    if type(value) == "table" then
      out[key] = seal_table(value, "caps." .. key)
    end
  end
  return seal_table(out, "caps")
end

return M
